using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#8716: every error that System.Communication declares with "Parameter message" carries the message parameter on its
///     failure path, also when the service throws an exception of unknown origin (then a fixed text with the invocation id,
///     never the exception text). The model test keeps the list below complete: a new error with a message parameter fails it
///     until it is covered here.
/// </summary>
public sealed partial class CommunicationMethodHostTests
{
    private const string InternalDetail = "internal detail mongodb://user@host";

    /// <summary>Error code → the method whose failure path produces it.</summary>
    private static readonly IReadOnlyDictionary<string, string> MessageErrors = new Dictionary<string, string>
    {
        ["PIPELINE_DEPLOY_REJECTED"] = SystemCommunicationCkIds.PipelineDeployMethodId,
        ["PIPELINE_DEPLOY_FAILED"] = SystemCommunicationCkIds.PipelineDeployMethodId,
        ["PIPELINE_EXECUTION_START_FAILED"] = SystemCommunicationCkIds.PipelineExecuteMethodId,
        ["PIPELINE_DEBUG_CHANGE_FAILED"] = SystemCommunicationCkIds.PipelineSetDebuggingMethodId,
        ["PIPELINE_MOVE_FAILED"] = SystemCommunicationCkIds.PipelineMoveToAdapterMethodId,
        ["DATA_FLOW_DEPLOY_FAILED"] = SystemCommunicationCkIds.DataFlowDeployMethodId,
        ["DATA_FLOW_UNDEPLOY_FAILED"] = SystemCommunicationCkIds.DataFlowUndeployMethodId
    };

    public static TheoryData<string> MessageErrorCodes() => new(MessageErrors.Keys);

    [Fact]
    public void TheModel_DeclaresAMessageParameterForExactlyTheCoveredErrors()
    {
        var cache = fixture.GetService<ICkCacheService>();
        var declared = cache.GetCkTypes(TenantId)
            .Where(t => t.CkTypeId.ModelId.Name == "System.Communication")
            .SelectMany(t => t.DefinedMethods)
            .SelectMany(m => m.Errors ?? [])
            .Where(e => e.Description?.Contains("Parameter message", StringComparison.Ordinal) == true)
            .Select(e => e.Code)
            .ToHashSet();

        declared.Should().BeEquivalentTo(MessageErrors.Keys,
            "every error declared with a message parameter needs a failure-path test in this file");
    }

    [Theory]
    [MemberData(nameof(MessageErrorCodes))]
    public async Task EveryDeclaredMessageError_CarriesTheMessage_AndNeverAnUnknownExceptionText(string code)
    {
        var adapter = ArrangeAdapter();
        var pipeline = ArrangePipeline(adapter);
        var dataFlowRtId = OctoObjectId.GenerateNewId();
        var unexpected = new InvalidOperationException(InternalDetail);
        object? parameters = null;
        var target = pipeline.RtId.ToString();
        switch (code)
        {
            case "PIPELINE_DEPLOY_REJECTED":
                _adapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(), null)
                    .Throws(HubRejection("node 3 is invalid"));
                break;
            case "PIPELINE_DEPLOY_FAILED":
                _adapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(), null)
                    .Throws(unexpected);
                break;
            case "PIPELINE_EXECUTION_START_FAILED":
                _trigger.StartExecutePipelineAsync(TenantId, pipeline.RtId, Arg.Any<string?>(), Arg.Any<bool>(),
                        Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(), Arg.Any<RtPipelineTriggerTypeEnum>(),
                        Arg.Any<ExecutePipelineMethodInvocation?>())
                    .Throws(unexpected);
                break;
            case "PIPELINE_DEBUG_CHANGE_FAILED":
                _adapterService.SetPipelineDebuggingAsync(TenantId, pipeline.ToRtEntityId(), true).Throws(unexpected);
                parameters = new { enabled = true };
                break;
            case "PIPELINE_MOVE_FAILED":
                // the handler lets only the repository's own (caller-safe) exception become this error
                var targetAdapterRtId = OctoObjectId.GenerateNewId();
                _repository.MovePipelineToAdapterAsync(TenantId, pipeline.RtId, targetAdapterRtId)
                    .Throws(new CommunicationRepositoryException("target adapter not found"));
                parameters = new { targetAdapterRtId = targetAdapterRtId.ToString() };
                break;
            case "DATA_FLOW_DEPLOY_FAILED":
                _adapterService.DeployDataFlowAsync(TenantId, dataFlowRtId).Throws(unexpected);
                target = dataFlowRtId.ToString();
                break;
            case "DATA_FLOW_UNDEPLOY_FAILED":
                _adapterService.UndeployDataFlowAsync(TenantId, dataFlowRtId).Throws(unexpected);
                target = dataFlowRtId.ToString();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(code), code, "add the failure arrangement");
        }

        var invocationId = Guid.NewGuid();
        var method = await InvokeAsync(MessageErrors[code], target, parameters, invocationId: invocationId);

        method.Error!.Code.Should().Be(code);
        method.Error.Parameters.Should().ContainKey("message");
        method.Error.Parameters["message"].Should().NotBeNullOrWhiteSpace();
        JsonSerializer.Serialize(method.Error).Should().NotContain("internal detail");
        if (code is not ("PIPELINE_DEPLOY_REJECTED" or "PIPELINE_MOVE_FAILED"))
        {
            method.Error.Parameters["message"].Should().EndWith($"(invocation {invocationId}).");
        }
    }
}
