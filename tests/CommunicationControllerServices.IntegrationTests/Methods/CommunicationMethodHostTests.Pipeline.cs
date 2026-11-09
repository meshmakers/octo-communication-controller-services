using System.Text;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#8596: the Pipeline, DataFlow and workload chart-version methods of System.Communication 4.18.0 through the real
///     method host — REST and method on the same substitutes, the same service call and answer; the sensitive pipeline
///     definition and execution input never reach a log line, an event or the answer. Refusals (no role, foreign tenant, no
///     token) run for every method in the theories of the main file.
/// </summary>
public sealed partial class CommunicationMethodHostTests
{
    private const string SecretInDefinition = "pw-Q7d9-never-logged";

    private static readonly string Definition =
        "triggers:\n  - type: FromExecutePipelineCommand@1\ntransformations:\n  - type: HttpRequest@1\n" +
        $"    password: {SecretInDefinition}\n";

    // ------------------------------------------------------------------ Pipeline.Deploy

    [Fact]
    public async Task PipelineDeploy_CallsTheSameServiceAsRest_WithTheAssignedAdapterAndTheDefinition()
    {
        var adapter = ArrangeAdapter();
        var pipeline = ArrangePipeline(adapter);

        var rest = await PipelineController(body: Definition)
            .DeployPipeline(adapter.ToRtEntityId().ToString(), pipeline.ToRtEntityId().ToString());
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId, pipeline.RtId.ToString(),
            new { pipelineDefinition = Definition });

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue();
        await _adapterService.Received(2).DeployPipelineAsync(TenantId, adapter.ToRtEntityId(),
            pipeline.ToRtEntityId(), Definition);
    }

    [Fact]
    public async Task PipelineDeploy_AnExplicitAdapter_AndNoDefinition_DeploysTheStoredOne()
    {
        var adapter = ArrangeAdapter();
        var pipeline = ArrangePipeline(assignedTo: null);

        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId, pipeline.RtId.ToString(),
            new { adapterRtId = adapter.RtId.ToString() });

        method.Success.Should().BeTrue();
        await _adapterService.Received(1).DeployPipelineAsync(TenantId, adapter.ToRtEntityId(),
            pipeline.ToRtEntityId(), null);
    }

    [Fact]
    public async Task PipelineDeploy_WithoutAssignedAdapter_UnknownAdapter_AndUnknownPipeline_AreRefusedBeforeTheService()
    {
        _repository.GetAdaptersAsync(TenantId).Returns([]);
        var pipeline = ArrangePipeline(assignedTo: null);

        var unassigned = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId, pipeline.RtId.ToString());
        var unknownAdapter = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId,
            pipeline.RtId.ToString(), new { adapterRtId = OctoObjectId.GenerateNewId().ToString() });
        var unknownPipeline = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId,
            OctoObjectId.GenerateNewId().ToString());

        unassigned.Error!.Code.Should().Be("PIPELINE_ADAPTER_NOT_ASSIGNED");
        unknownAdapter.Error!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        unknownPipeline.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        await _adapterService.DidNotReceiveWithAnyArgs().DeployPipelineAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PipelineDeploy_AFailure_IsTheDeclaredError_AndTheDefinitionIsNeverEchoed(bool rejectedByAdapter)
    {
        var adapter = ArrangeAdapter();
        var pipeline = ArrangePipeline(adapter);
        var text = $"invalid node in line 'password: {SecretInDefinition}'";
        Exception failure = rejectedByAdapter
            ? HubRejection(text)
            : new InvalidOperationException(text);
        _adapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(), Definition)
            .Throws(failure);

        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineDeployMethodId, pipeline.RtId.ToString(),
            new { pipelineDefinition = Definition });

        method.Error!.Code.Should().Be(rejectedByAdapter ? "PIPELINE_DEPLOY_REJECTED" : "PIPELINE_DEPLOY_FAILED");
        method.Error.Parameters["message"].Should().StartWith("invalid node in line '***'");
        AssertNeverEchoed(SecretInDefinition, method);
    }

    // ------------------------------------------------------------------ Pipeline.Execute

    [Fact]
    public async Task PipelineExecute_StartsLikeRest_WithInputDryRunCallerAndToken_AndAnswersTheExecution()
    {
        var pipeline = ArrangePipeline(assignedTo: null);
        var execution = new PipelineExecutionDataDto { Id = Guid.NewGuid(), DateTime = DateTime.UtcNow };
        _trigger.StartExecutePipelineAsync(TenantId, pipeline.RtId, Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(), Arg.Any<RtPipelineTriggerTypeEnum>(),
                Arg.Any<ExecutePipelineMethodInvocation?>())
            .Returns(execution);
        var token = Token();
        const string input = "{\"customer\":\"secret-customer-77\"}";

        var rest = await PipelineController(body: input, bearer: token).ExecutePipeline(pipeline.RtId, isDryRun: true);
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineExecuteMethodId, pipeline.RtId.ToString(),
            new { input, isDryRun = true }, token);

        ((OkObjectResult)rest).Value.Should().Be(execution);
        method.Success.Should().BeTrue();
        using var json = JsonDocument.Parse(method.ResultJson!);
        json.RootElement.GetProperty("executionId").GetString().Should().Be(execution.Id.ToString());
        json.RootElement.GetProperty("startedAt").GetDateTime().Should().BeCloseTo(execution.DateTime,
            TimeSpan.FromMilliseconds(1));

        var calls = _trigger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITriggerManagementService.StartExecutePipelineAsync))
            .Select(c => c.GetArguments()).ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(a => (string?)a[2] == input && (bool)a[3]! && (string?)a[5] == token);
        calls.Select(a => (ExecutePipelineCaller)a[4]!).Should().OnlyContain(c =>
            c.SubjectId == Subject && c.TrustLevel == 2 && c.Roles!.Contains(CommunicationManagement));
        // no method-invocation marker: this is the REST execute as a method, not a handler: pipeline method
        calls.Should().OnlyContain(a => a[7] == null);
        AssertNeverEchoed("secret-customer-77", method);
    }

    [Fact]
    public async Task PipelineExecute_AStartFailure_IsTheDeclaredError_WithoutTheInputOrTheToken()
    {
        var pipeline = ArrangePipeline(assignedTo: null);
        var token = Token();
        const string input = "secret-input-value-31";
        _trigger.StartExecutePipelineAsync(TenantId, pipeline.RtId, Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(), Arg.Any<RtPipelineTriggerTypeEnum>(),
                Arg.Any<ExecutePipelineMethodInvocation?>())
            .Throws(new InvalidOperationException($"adapter offline; input {input}; token {token}"));

        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineExecuteMethodId, pipeline.RtId.ToString(),
            new { input }, token);

        method.Error!.Code.Should().Be("PIPELINE_EXECUTION_START_FAILED");
        method.Error.Parameters["message"].Should().Be("adapter offline; input ***; token ***");
        AssertNeverEchoed(input, method);
        AssertNeverEchoed(token, method);
    }

    [Fact]
    public async Task PipelineExecute_AnUnknownPipeline_IsTargetNotFound_AndNothingStarts()
    {
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineExecuteMethodId,
            OctoObjectId.GenerateNewId().ToString());

        method.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        _trigger.ReceivedCalls().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Pipeline.SetDebugging

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PipelineSetDebugging_AnswersWhatRestAnswers(bool enabled, bool applied)
    {
        var pipeline = ArrangePipeline(assignedTo: null);
        _adapterService.SetPipelineDebuggingAsync(TenantId, pipeline.ToRtEntityId(), enabled).Returns(applied);

        var rest = await PipelineController().SetPipelineDebugging(pipeline.RtId, new SetPipelineDebugRequestDto(enabled));
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineSetDebuggingMethodId, pipeline.RtId.ToString(),
            new { enabled });

        method.Success.Should().BeTrue();
        SameMembers(((OkObjectResult)rest).Value!, method.ResultJson!);
        await _adapterService.Received(2).SetPipelineDebuggingAsync(TenantId, pipeline.ToRtEntityId(), enabled);
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("source: User via CK method")));
    }

    [Fact]
    public async Task PipelineSetDebugging_AFailure_IsTheDeclaredError_WithTheRestMessage()
    {
        var pipeline = ArrangePipeline(assignedTo: null);
        var failure = HubRejection("adapter refused the configuration");
        _adapterService.SetPipelineDebuggingAsync(TenantId, pipeline.ToRtEntityId(), true).Throws(failure);

        var rest = await PipelineController().SetPipelineDebugging(pipeline.RtId, new SetPipelineDebugRequestDto(true));
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineSetDebuggingMethodId, pipeline.RtId.ToString(),
            new { enabled = true });

        method.Error!.Code.Should().Be("PIPELINE_DEBUG_CHANGE_FAILED");
        method.Error.Parameters["message"].Should()
            .Be(((ErrorResponse)((UnprocessableEntityObjectResult)rest).Value!).ErrorMessage);
        await _events.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(default!, default!);
    }

    // ------------------------------------------------------------------ Pipeline.MoveToAdapter

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PipelineMoveToAdapter_AnswersWhatRestAnswersForOnePipeline(bool redeploy, bool redeployFails)
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var oldAdapter = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId());
        var newAdapter = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId());
        _repository.MovePipelineToAdapterAsync(TenantId, pipelineRtId, newAdapter.RtId)
            .Returns(new PipelineMoveResult(pipelineRtId, oldAdapter, newAdapter));
        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
        if (redeployFails)
        {
            _adapterService.DeployPipelineAsync(TenantId, newAdapter, pipelineRtEntityId)
                .Throws(new InvalidOperationException("target adapter offline"));
        }

        var rest = await PipelineController().MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto([pipelineRtId.ToString()], newAdapter.RtId.ToString(), redeploy));
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineMoveToAdapterMethodId, pipelineRtId.ToString(),
            new { targetAdapterRtId = newAdapter.RtId.ToString(), redeploy });

        method.Success.Should().BeTrue();
        var restEntry = ((MovePipelinesToAdapterResponseDto)((OkObjectResult)rest).Value!).Results.Single();
        using var json = JsonDocument.Parse(method.ResultJson!);
        json.RootElement.GetProperty("oldAdapterRtId").GetString().Should().Be(restEntry.OldAdapterRtId);
        json.RootElement.GetProperty("newAdapterRtId").GetString().Should().Be(restEntry.NewAdapterRtId);
        (json.RootElement.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.String
                ? warning.GetString()
                : null)
            .Should().Be(restEntry.ErrorMessage);
        await _adapterService.Received(redeploy ? 2 : 0).DeployPipelineAsync(TenantId, newAdapter, pipelineRtEntityId);
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("source: User via CK method")));
    }

    [Fact]
    public async Task PipelineMoveToAdapter_ARepositoryFailure_IsTheDeclaredError_AndAnInvalidTargetIsAValidationError()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        _repository.MovePipelineToAdapterAsync(TenantId, pipelineRtId, target)
            .Throws(new CommunicationRepositoryException("adapter not found"));

        var failed = await InvokeAsync(SystemCommunicationCkIds.PipelineMoveToAdapterMethodId, pipelineRtId.ToString(),
            new { targetAdapterRtId = target.ToString() });
        var invalid = await InvokeAsync(SystemCommunicationCkIds.PipelineMoveToAdapterMethodId, pipelineRtId.ToString(),
            new { targetAdapterRtId = "not-an-id" });

        failed.Error!.Code.Should().Be("PIPELINE_MOVE_FAILED");
        invalid.Error!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
    }

    // ------------------------------------------------------------------ DataFlow.Deploy / Undeploy

    [Fact]
    public async Task DataFlowDeployAndUndeploy_CallTheSameServicesAsRest_AndMapAFailure()
    {
        var dataFlowRtId = OctoObjectId.GenerateNewId();

        var restDeploy = await DataFlowController().DeployDataFlow(dataFlowRtId);
        var deploy = await InvokeAsync(SystemCommunicationCkIds.DataFlowDeployMethodId, dataFlowRtId.ToString());
        var restUndeploy = await DataFlowController().UndeployDataFlow(dataFlowRtId);
        var undeploy = await InvokeAsync(SystemCommunicationCkIds.DataFlowUndeployMethodId, dataFlowRtId.ToString());

        restDeploy.Should().BeOfType<NoContentResult>();
        restUndeploy.Should().BeOfType<NoContentResult>();
        deploy.Success.Should().BeTrue();
        undeploy.Success.Should().BeTrue();
        await _adapterService.Received(2).DeployDataFlowAsync(TenantId, dataFlowRtId);
        await _adapterService.Received(2).UndeployDataFlowAsync(TenantId, dataFlowRtId);

        _adapterService.DeployDataFlowAsync(TenantId, dataFlowRtId)
            .Throws(HubRejection("definition rejected"));
        _adapterService.UndeployDataFlowAsync(TenantId, dataFlowRtId)
            .Throws(new InvalidOperationException("adapter gone"));
        var failedDeploy = await InvokeAsync(SystemCommunicationCkIds.DataFlowDeployMethodId, dataFlowRtId.ToString());
        var failedUndeploy =
            await InvokeAsync(SystemCommunicationCkIds.DataFlowUndeployMethodId, dataFlowRtId.ToString());

        failedDeploy.Error!.Code.Should().Be("DATA_FLOW_DEPLOY_FAILED");
        failedDeploy.Error.Parameters["message"].Should().Be("definition rejected");
        failedUndeploy.Error!.Code.Should().Be("DATA_FLOW_UNDEPLOY_FAILED");
        failedUndeploy.Error.Parameters["message"].Should().Be("adapter gone");
    }

    // ------------------------------------------------------------------ DeployableWorkload.SetChartVersion

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" 2.0.0-beta.1 ", "2.0.0-beta.1")]
    [InlineData("", "")]
    public async Task SetChartVersion_WritesWhatRestWrites(string requested, string stored)
    {
        var workloadRtId = OctoObjectId.GenerateNewId();
        _repository.UpdateWorkloadChartVersionAsync(TenantId, workloadRtId, stored).Returns("1.0.0");

        var rest = await WorkloadController().UpdateChartVersion(workloadRtId, new UpdateChartVersionDto(requested));
        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadSetChartVersionMethodId,
            workloadRtId.ToString(), new { chartVersion = requested }, ckTypeId: "System.Communication/Application");

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue();
        await _repository.Received(2).UpdateWorkloadChartVersionAsync(TenantId, workloadRtId, stored);
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.EndsWith("(source: CI/CD via CK method).")));
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.EndsWith("(source: CI/CD).")));
    }

    [Fact]
    public async Task SetChartVersion_AnInvalidVersionAndAnUnknownWorkload_AreRefusedLikeRest()
    {
        var workloadRtId = OctoObjectId.GenerateNewId();
        _repository.UpdateWorkloadChartVersionAsync(TenantId, workloadRtId, "3.0.0")
            .Throws(new CommunicationRepositoryException("workload not found"));

        var restInvalid = await WorkloadController().UpdateChartVersion(workloadRtId, new UpdateChartVersionDto("v1"));
        var invalid = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadSetChartVersionMethodId,
            workloadRtId.ToString(), new { chartVersion = "v1" });
        var restUnknown = await WorkloadController().UpdateChartVersion(workloadRtId, new UpdateChartVersionDto("3.0.0"));
        var unknown = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadSetChartVersionMethodId,
            workloadRtId.ToString(), new { chartVersion = "3.0.0" });

        restInvalid.Should().BeOfType<BadRequestObjectResult>();
        invalid.Error!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        restUnknown.Should().BeOfType<NotFoundObjectResult>();
        unknown.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        await _repository.DidNotReceive().UpdateWorkloadChartVersionAsync(TenantId, workloadRtId, "v1");
    }

    // ------------------------------------------------------------------ helpers

    private RtPipeline ArrangePipeline(RtAdapter? assignedTo)
    {
        var pipeline = new RtPipeline
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId
        };
        _repository.GetPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(pipeline);
        _repository.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(assignedTo);
        return pipeline;
    }

    /// <summary>The adapter's rejection (the exception has private constructors; the service creates it).</summary>
    private static Exception HubRejection(string message) =>
        (Exception)Activator.CreateInstance(typeof(AdapterHubCallbackException),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [message], null)!;

    /// <summary>A sensitive value appears in no log line, no stored event and not in the answer.</summary>
    private void AssertNeverEchoed(string value, CkMethodResponse response)
    {
        _logs.All.Should().NotContain(value);
        (response.ResultJson ?? string.Empty).Should().NotContain(value);
        JsonSerializer.Serialize(response.Error).Should().NotContain(value);
        foreach (var call in _events.ReceivedCalls())
        {
            call.GetArguments().OfType<string>().Should().NotContain(a => a.Contains(value));
        }
    }

    private PipelineController PipelineController(string? body = null, string? bearer = null)
    {
        var controller = new PipelineController(NullLogger<PipelineController>.Instance, _trigger, _adapterService,
            _repository, _events);
        var context = HttpContextOf(null);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
        if (bearer != null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearer}";
            context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", Subject),
                    new System.Security.Claims.Claim("role", CommunicationManagement)
                ], "Bearer", "name", "role"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private DataFlowController DataFlowController()
    {
        var controller = new DataFlowController(NullLogger<DataFlowController>.Instance, _adapterService,
            Substitute.For<IPipelineExecutionService>());
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(null) };
        return controller;
    }

    private WorkloadController WorkloadController()
    {
        var controller = new WorkloadController(NullLogger<WorkloadController>.Instance, _repository, _events);
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(null) };
        return controller;
    }
}
