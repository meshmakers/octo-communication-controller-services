using System.Diagnostics;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.PipelineMethodTestSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     AB#6879: a method with <c>handler: pipeline</c> is executed by its pipeline and the pipeline's execution
///     outcome becomes the method envelope. The pipeline runtime itself runs in the adapter; these tests play the
///     adapter's part by writing the execution the way the adapter hub does (status, OutputData).
/// </summary>
internal class PipelineMethodInvokerTests
{
    private readonly IPipelineMethodCatalog _catalog = Substitute.For<IPipelineMethodCatalog>();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly ITriggerManagementService _trigger = Substitute.For<ITriggerManagementService>();
    private readonly CapturingLogger<PipelineMethodInvoker> _logger = new();
    private readonly PipelineMethodHandlerInstance _instance;
    private readonly PipelineMethodInvoker _invoker;
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly RtPipeline _pipeline = RtEntityCreator.CreatePipeline();

    public PipelineMethodInvokerTests()
    {
        var options = Options.Create(new PipelineMethodOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5), MaxPollInterval = TimeSpan.FromMilliseconds(20)
        });
        _instance = new PipelineMethodHandlerInstance(options, TimeProvider.System);
        var services = new ServiceCollection().AddSingleton(_trigger).BuildServiceProvider();
        _invoker = new PipelineMethodInvoker(_catalog, _repository, services, _instance, options,
            TimeProvider.System, _logger);

        _catalog.IsTenantEnabled(TenantId).Returns(true);
        ArrangeMethod(Method());
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns(_pipeline);
        _trigger.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(),
                Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns(_ => Task.FromResult(new PipelineExecutionDataDto { Id = _executionId, DateTime = DateTime.UtcNow }));
    }

    private void ArrangeMethod(PipelineMethodDefinition method) => _catalog.Find(TenantId, MethodId).Returns(method);

    /// <summary>The adapter reports the execution: first nothing (not yet written), then Running, then the end.</summary>
    private void ArrangeExecution(RtPipelineExecutionStatusEnum finalStatus, string? output, string? error = null,
        int runningPolls = 2)
    {
        var polls = 0;
        _repository.GetPipelineExecutionAsync(TenantId, _executionId.ToString()).Returns(_ =>
        {
            polls++;
            if (polls == 1)
            {
                return Task.FromResult<RtPipelineExecution?>(null);
            }

            var execution = RtEntityCreator.CreatePipelineExecution(_executionId.ToString(),
                polls <= runningPolls ? RtPipelineExecutionStatusEnum.Running : finalStatus);
            if (polls > runningPolls)
            {
                execution.OutputData = output;
                execution.ErrorMessage = error;
            }

            return Task.FromResult<RtPipelineExecution?>(execution);
        });
    }

    [Test]
    public async Task ThePipelineResultIsReturnedInTheSuccessEnvelope()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Completed, "{\"value\": 7}");
        var request = Request();

        var response = await _invoker.InvokeAsync(request);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.ResultJson).IsEqualTo("{\"value\":7}");
        await Assert.That(response.InvocationId).IsEqualTo(request.InvocationId);
        await Assert.That(response.HandlerInstanceId).IsEqualTo(_instance.InstanceId);
    }

    [Test]
    public async Task TheMethodParametersTargetAndMethodReachThePipelineAsInput()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Completed, "{\"value\": 1}");
        string? input = null;
        _trigger.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Do<string?>(i => input = i),
                Arg.Any<bool>(), Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(),
                Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns(_ => Task.FromResult(new PipelineExecutionDataDto { Id = _executionId, DateTime = DateTime.UtcNow }));
        var request = Request("{\"version\":\"2.0\",\"force\":true}");

        await _invoker.InvokeAsync(request);

        var json = JsonNode.Parse(input!)!;
        await Assert.That(json["parameters"]!["version"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(json["parameters"]!["force"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["target"]!["ckTypeId"]!.GetValue<string>()).IsEqualTo(PipelineMethodTestSupport.CkTypeId);
        await Assert.That(json["target"]!["rtId"]!.GetValue<string>()).IsEqualTo(request.TargetRtId);
        await Assert.That(json["method"]!["id"]!.GetValue<string>()).IsEqualTo(MethodId);
        await Assert.That(json["method"]!["invocationId"]!.GetValue<string>()).IsEqualTo(request.InvocationId.ToString());
    }

    [Test]
    public async Task ThePipelineRunsAsTheCallerAndGetsTheForwardedToken()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Completed, "{\"value\": 1}");
        ExecutePipelineCaller? caller = null;
        string? token = null;
        string? tenant = null;
        OctoObjectId? pipelineId = null;
        _trigger.StartExecutePipelineAsync(Arg.Do<string>(t => tenant = t), Arg.Do<OctoObjectId>(p => pipelineId = p),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Do<ExecutePipelineCaller?>(c => caller = c),
                Arg.Do<string?>(t => token = t), Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns(_ => Task.FromResult(new PipelineExecutionDataDto { Id = _executionId, DateTime = DateTime.UtcNow }));

        await _invoker.InvokeAsync(Request());

        await Assert.That(tenant).IsEqualTo(TenantId);
        await Assert.That(pipelineId).IsEqualTo(_pipeline.RtId);
        await Assert.That(caller!.SubjectId).IsEqualTo("user-42");
        await Assert.That(caller.Name).IsEqualTo("Ada Admin");
        await Assert.That(caller.Roles).Contains("TenantAdmin");
        // not re-verified here: weak, never the strong trust of a verified bearer
        await Assert.That(caller.TrustLevel).IsEqualTo(1);
        await Assert.That(token).IsEqualTo(Token);
    }

    [Test]
    public async Task ADeclaredErrorOfThePipelineIsTheErrorOfTheMethod()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Completed,
            "{\"error\": {\"code\": \"FIRMWARE_LOCKED\", \"parameters\": {\"reason\": \"maintenance window\"}}}");

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo("FIRMWARE_LOCKED");
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("maintenance window");
    }

    [Test]
    public async Task AFailingPipelineIsHandlerFailedWithACorrelationIdAndItsMessageStaysInTheLog()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Failed, null, "Node ApplyChanges failed: duplicate key 'abc'");
        var request = Request();

        var response = await _invoker.InvokeAsync(request);

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["correlationId"]).IsEqualTo(request.InvocationId.ToString());
        await Assert.That(response.Error.Parameters.Values.Any(v => v.Contains("duplicate key"))).IsFalse();
        await Assert.That(_logger.All).Contains("duplicate key 'abc'");
        await Assert.That(_logger.All).Contains(request.InvocationId.ToString());
    }

    [Test]
    public async Task APipelineThatOutlivesTheDeadlineIsAMethodTimeout()
    {
        // the execution never ends
        _repository.GetPipelineExecutionAsync(TenantId, _executionId.ToString()).Returns(_ =>
            Task.FromResult<RtPipelineExecution?>(RtEntityCreator.CreatePipelineExecution(_executionId.ToString())));
        var stopwatch = Stopwatch.StartNew();

        var response = await _invoker.InvokeAsync(Request(deadlineSeconds: 1));

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodTimeout);
        await Assert.That(response.Error.Parameters["timeoutSeconds"]).IsEqualTo("5");
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
        await Assert.That(_logger.All).Contains("may still finish");
    }

    [Test]
    public async Task ARequestThatArrivesAfterItsDeadlineIsNotExecuted()
    {
        var response = await _invoker.InvokeAsync(Request(deadlineSeconds: -1));

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodTimeout);
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    public async Task WithoutADeployedPipelineTheAnswerIsHandlerNotAvailableAtOnce()
    {
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns((RtPipeline?)null);
        var stopwatch = Stopwatch.StartNew();

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerNotAvailable);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("pipeline-not-deployed");
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    public async Task AnUndeployedPipelineIsNotRun()
    {
        _pipeline.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerNotAvailable);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("pipeline-not-deployed");
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    public async Task AMethodWithSensitiveParametersIsRefusedAndNeverReachesAPipeline()
    {
        // OD-3, OD-4, OD-9: the pipeline execution stores its input and output.
        ArrangeMethod(Method(sensitiveParameter: true));
        var request = Request("{\"version\":\"1\",\"signingKey\":\"TOP-SECRET-KEY-VALUE\"}");

        var response = await _invoker.InvokeAsync(request);

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerNotAvailable);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("sensitive-parameters-not-supported");
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().GetPipelineByWellKnownNameAsync(default!, default!);
        await Assert.That(_logger.All).DoesNotContain("TOP-SECRET-KEY-VALUE");
        await Assert.That(_logger.All).Contains("signingKey");
    }

    [Test]
    public async Task ASecretTypedParameterIsRefusedLikeASensitiveOne()
    {
        var method = Method();
        method.Definition.Parameters!.Add(new CkMethodParameterDto
            { Name = "apiKey", ValueType = AttributeValueTypesDto.Secret });
        ArrangeMethod(method);

        var response = await _invoker.InvokeAsync(Request("{\"version\":\"1\",\"apiKey\":\"x\"}"));

        await Assert.That(response.Error!.Parameters["reason"]).IsEqualTo("sensitive-parameters-not-supported");
    }

    [Test]
    public async Task AMethodThatIsNotPipelineHandledIsNotAnsweredByTheController()
    {
        ArrangeMethod(Method(handler: CkMethodHandlers.Service));

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerNotAvailable);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("method-not-pipeline-handled");
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    public async Task AnUnknownTenantOrMethodIsHandlerNotAvailable()
    {
        _catalog.IsTenantEnabled(TenantId).Returns(false);
        var tenantResponse = await _invoker.InvokeAsync(Request());

        _catalog.IsTenantEnabled(TenantId).Returns(true);
        _catalog.Find(TenantId, MethodId).Returns((PipelineMethodDefinition?)null);
        var methodResponse = await _invoker.InvokeAsync(Request());

        await Assert.That(tenantResponse.Error!.Parameters["reason"]).IsEqualTo("tenant-not-enabled");
        await Assert.That(methodResponse.Error!.Parameters["reason"]).IsEqualTo("method-not-pipeline-handled");
    }

    [Test]
    public async Task AMissingRequiredParameterIsAValidationFailureAndTheValueIsNeverEchoed()
    {
        var response = await _invoker.InvokeAsync(Request("{\"force\":true}"));

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodValidationFailed);
        await Assert.That(response.Error.Parameters["field"]).IsEqualTo("version");
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[1]")]
    public async Task ParametersThatAreNoJsonObjectAreAValidationFailure(string parametersJson)
    {
        var response = await _invoker.InvokeAsync(Request(parametersJson));

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodValidationFailed);
        await Assert.That(response.Error.Parameters["field"]).IsEqualTo("$");
    }

    [Test]
    public async Task AResultThatDoesNotMatchTheResultRecordIsHandlerFailedWithAValidationParameter()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Completed, "{\"value\": \"seven\"}");

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["field"]).IsEqualTo("value");
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("invalid-result");
    }

    [Test]
    public async Task APipelineThatCannotBeStartedIsHandlerNotAvailableAndTheTokenIsNotLogged()
    {
        _trigger.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(),
                Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns<Task<PipelineExecutionDataDto>>(_ =>
                throw new InvalidOperationException($"adapter offline (bearer {Token})"));

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerNotAvailable);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("pipeline-start-failed");
        await Assert.That(_logger.All).DoesNotContain("SECRET-TOKEN-PAYLOAD");
    }

    [Test]
    public async Task ATransientStoreErrorWhileWaitingDoesNotFailTheCall()
    {
        var polls = 0;
        _repository.GetPipelineExecutionAsync(TenantId, _executionId.ToString()).Returns(_ =>
        {
            polls++;
            if (polls == 1)
            {
                throw new InvalidOperationException("mongo hiccup");
            }

            var done = RtEntityCreator.CreatePipelineExecution(_executionId.ToString(),
                RtPipelineExecutionStatusEnum.Completed);
            done.OutputData = "{\"value\": 3}";
            return Task.FromResult<RtPipelineExecution?>(done);
        });

        var response = await _invoker.InvokeAsync(Request());

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.ResultJson).IsEqualTo("{\"value\":3}");
    }

    [Test]
    public async Task ANewerWireContractVersionIsNotExecuted()
    {
        var request = Request() with { ContractVersion = CkMethodContract.CurrentVersion + 1 };

        var response = await _invoker.InvokeAsync(request);

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodContractVersionUnsupported);
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default!, default);
    }

    [Test]
    public async Task TheCallersOwnCancellationSurfacesAsCancellation()
    {
        _repository.GetPipelineExecutionAsync(TenantId, _executionId.ToString()).Returns(_ =>
            Task.FromResult<RtPipelineExecution?>(RtEntityCreator.CreatePipelineExecution(_executionId.ToString())));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await _invoker.InvokeAsync(Request(deadlineSeconds: 30), cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task NeitherTheTokenNorTheParametersAreEverLogged()
    {
        ArrangeExecution(RtPipelineExecutionStatusEnum.Failed, null, $"boom {Token}");
        var request = Request("{\"version\":\"PARAMETER-VALUE-1234\"}");

        await _invoker.InvokeAsync(request);

        await Assert.That(_logger.All).DoesNotContain("SECRET-TOKEN-PAYLOAD");
        await Assert.That(_logger.All).DoesNotContain("PARAMETER-VALUE-1234");
    }
}
