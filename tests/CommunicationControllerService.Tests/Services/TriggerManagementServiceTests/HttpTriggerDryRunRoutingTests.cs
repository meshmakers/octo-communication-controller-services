using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

/// <summary>
///     AB#10015: an HTTP-only pipeline has no consumer on the execute address. Pins the invariant "an HTTP-only pipeline
///     runs for real only through its route": a real execute is refused before anything is sent, a dry run goes to the
///     dry-run address of its FromHttpRequest@2 trigger, and a pipeline with an execute trigger keeps the execute address.
/// </summary>
internal class HttpTriggerDryRunRoutingTests : TriggerManagementServiceTestsBase
{
    private const string HttpOnly = """
                                    triggers:
                                      - type: FromHttpRequest@2
                                        method: Post
                                        path: /maintenance-tickets
                                    transformations: []
                                    """;

    private const string HttpAndExecute = """
                                          triggers:
                                            - type: FromHttpRequest@2
                                              method: Post
                                              path: /tickets
                                            - type: FromExecutePipelineCommand@1
                                          transformations: []
                                          """;

    private const string HttpV1Only = """
                                      triggers:
                                        - type: FromHttpRequest@1
                                          method: Post
                                          path: /legacy
                                      transformations: []
                                      """;

    private const string CronOnly = """
                                    triggers:
                                      - type: FromPipelineTriggerEvent@1
                                    transformations: []
                                    """;

    private readonly OctoObjectId _pipelineRtId = OctoObjectId.GenerateNewId();
    private string? _sentAddress;

    private string ExecuteAddress =>
        $"{PipelineQueueNames.ExecutePipelineCommand.ToLower()}-{TenantId.ToLower()}-pipeline-{_pipelineRtId.ToString().ToLower()}";

    private void ArrangePipeline(string definition)
    {
        CommunicationRepository.GetPipelineAsync(TenantId, Arg.Is<RtEntityId>(e => e.RtId == _pipelineRtId))
            .Returns(new RtPipeline { PipelineDefinition = definition });
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                _sentAddress = call.ArgAt<string>(0);
                return new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow);
            });
    }

    [Test]
    public async Task RealExecute_OfAnHttpOnlyPipeline_IsRefusedBeforeAnythingIsSent()
    {
        ArrangePipeline(HttpOnly);

        var exception = await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}"))
            .Throws<TriggerManagementServiceException>();

        await Assert.That(exception!.Message).Contains("runs for real only through its route");
        await Assert.That(_sentAddress).IsNull();
        await WorkloadLifecycleService.DidNotReceive()
            .EnsureWorkloadRunningForPipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("FromHttpRequest@2")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task DryRun_OfAnHttpOnlyPipeline_GoesToTheDryRunAddress()
    {
        ArrangePipeline(HttpOnly);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}", isDryRun: true);

        await Assert.That(_sentAddress).IsEqualTo(ExecuteAddress + "-http-dry-run");
    }

    [Test]
    public async Task DryRun_OfAPipelineWithAnExecuteTrigger_KeepsTheExecuteAddress()
    {
        ArrangePipeline(HttpAndExecute);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}", isDryRun: true);

        await Assert.That(_sentAddress).IsEqualTo(ExecuteAddress);
    }

    [Test]
    public async Task RealExecute_OfAPipelineWithAnExecuteTrigger_KeepsTheExecuteAddress()
    {
        ArrangePipeline(HttpAndExecute);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}");

        await Assert.That(_sentAddress).IsEqualTo(ExecuteAddress);
    }

    [Test]
    public async Task RealExecute_OfAFromHttpRequestV1Pipeline_IsRefused()
    {
        ArrangePipeline(HttpV1Only);

        var exception = await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}"))
            .Throws<TriggerManagementServiceException>();

        await Assert.That(exception!.Message).Contains("FromHttpRequest@1");
        await Assert.That(_sentAddress).IsNull();
    }

    [Test]
    public async Task DryRun_OfAFromHttpRequestV1Pipeline_IsRefused()
    {
        ArrangePipeline(HttpV1Only);

        var exception = await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, "{}", isDryRun: true))
            .Throws<TriggerManagementServiceException>();

        await Assert.That(exception!.Message).Contains("FromHttpRequest@1");
        await Assert.That(_sentAddress).IsNull();
    }

    [Test]
    public async Task PipelineWithoutHttpTrigger_OrUnknownDefinition_KeepsTheExecuteAddress()
    {
        ArrangePipeline(CronOnly);
        await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, null);
        await Assert.That(_sentAddress).IsEqualTo(ExecuteAddress);

        ArrangePipeline("triggers: [ this is : not yaml");
        await TriggerManagementService.StartExecutePipelineAsync(TenantId, _pipelineRtId, null, isDryRun: true);
        await Assert.That(_sentAddress).IsEqualTo(ExecuteAddress);
    }

    [Test]
    public async Task Decide_MatchesNodeNamesCaseInsensitively()
    {
        var (route, refusal) = HttpTriggerDryRunRouting.Decide(["fromhttprequest@2"], true, _pipelineRtId);

        await Assert.That(route).IsEqualTo(ExecuteCommandRoute.HttpDryRun);
        await Assert.That(refusal).IsNull();
    }
}
