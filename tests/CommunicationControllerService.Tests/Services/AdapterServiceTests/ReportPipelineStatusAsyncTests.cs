using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5385: a trigger's status line is written to the pipeline's live <c>StatusMessage</c> — and
/// only if the pipeline is one the controller deployed to the reporting adapter. The cache is the
/// authority on that; the adapter identity itself comes from the hub connection.
/// </summary>
internal class ReportPipelineStatusAsyncTests : AdapterServiceTestsBase
{
    private static PipelineStatusReportDto Status(RtEntityId pipelineRtEntityId, string message, bool isError = false)
        => new()
        {
            PipelineRtEntityId = pipelineRtEntityId,
            Message = message,
            IsError = isError,
            TimestampUtc = DateTime.UtcNow
        };

    private RtEntityId RegisterAdapterWithPipeline(RtEntityId pipelineRtEntityId)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            "{}",
            [new PipelineConfigurationDto(rtDataFlow.RtId, pipelineRtEntityId, false, "name: test", [])]
        ));
        return rtAdapter.ToRtEntityId();
    }

    [Test]
    public async Task ReportPipelineStatusAsync_PipelineOfTheAdapter_WritesOnlyTheStatusMessage()
    {
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RegisterAdapterWithPipeline(pipeline);
        const string line = "2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox · seen 12, imported 12, failed 0, skipped 0";

        var written = await AdapterService.ReportPipelineStatusAsync(TenantId, adapter, Status(pipeline, line));

        await Assert.That(written).IsTrue();
        await CommunicationRepository.Received(1).SetPipelineStatusMessageAsync(TenantId, pipeline, line,
            false, Arg.Any<DateTime>());
        // Never the deployment-state path: it would run the error tracking and clear LastDeploymentError.
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_ErrorLineIsWrittenVerbatim()
    {
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RegisterAdapterWithPipeline(pipeline);
        const string line = "ERROR 2026-09-26T17:40:12Z · Mail folder 'Inbox.02_Steuern' not found in mailbox";

        await AdapterService.ReportPipelineStatusAsync(TenantId, adapter, Status(pipeline, line, isError: true));

        await CommunicationRepository.Received(1).SetPipelineStatusMessageAsync(TenantId, pipeline, line,
            true, Arg.Any<DateTime>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_PipelineNotDeployedToTheAdapter_IsRejected()
    {
        // The adapter runs ONE pipeline; it reports for another one of the tenant.
        var adapter = RegisterAdapterWithPipeline(RtEntityCreator.CreatePipeline().ToRtEntityId());
        var foreignPipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();

        var written = await AdapterService.ReportPipelineStatusAsync(TenantId, adapter,
            Status(foreignPipeline, "line"));

        await Assert.That(written).IsFalse();
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineStatusMessageAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_AdapterNotRegistered_IsRejected()
    {
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var unregisteredAdapter = RtEntityCreator.CreateAdapter().ToRtEntityId();

        var written = await AdapterService.ReportPipelineStatusAsync(TenantId, unregisteredAdapter,
            Status(pipeline, "line"));

        await Assert.That(written).IsFalse();
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineStatusMessageAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<DateTime>());
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task ReportPipelineStatusAsync_TenantNotInCache_IsRejected()
    {
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>()).Returns(false);
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RtEntityCreator.CreateAdapter().ToRtEntityId();

        var written = await AdapterService.ReportPipelineStatusAsync("unknownTenant", adapter,
            Status(pipeline, "line"));

        await Assert.That(written).IsFalse();
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineStatusMessageAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_LongLine_IsTruncatedTo1000Characters()
    {
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RegisterAdapterWithPipeline(pipeline);
        var line = new string('x', AdapterService.MaxPipelineStatusMessageLength + 500);

        await AdapterService.ReportPipelineStatusAsync(TenantId, adapter, Status(pipeline, line));

        await CommunicationRepository.Received(1).SetPipelineStatusMessageAsync(TenantId, pipeline,
            Arg.Is<string>(m => m.Length == AdapterService.MaxPipelineStatusMessageLength),
            Arg.Any<bool>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_ErrorPrefixWithoutFlag_IsCountedAsFailure()
    {
        // AB#5618: a sender that follows the "ERROR " line convention but does not set the flag
        // (IsError deserialises as false) must not reset the failure streak.
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RegisterAdapterWithPipeline(pipeline);
        const string line = "ERROR 2026-10-06T08:00:00Z · login failed";

        await AdapterService.ReportPipelineStatusAsync(TenantId, adapter, Status(pipeline, line, isError: false));

        await CommunicationRepository.Received(1).SetPipelineStatusMessageAsync(TenantId, pipeline, line,
            true, Arg.Any<DateTime>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_PassesTheControllerReceiveTimeInUtc()
    {
        // AB#5618: LastSuccessfulStatusAt runs on the controller's clock, not the adapter's.
        var pipeline = RtEntityCreator.CreatePipeline().ToRtEntityId();
        var adapter = RegisterAdapterWithPipeline(pipeline);
        var skewed = new PipelineStatusReportDto
        {
            PipelineRtEntityId = pipeline,
            Message = "line",
            IsError = false,
            TimestampUtc = DateTime.UtcNow.AddDays(1)
        };
        Clock.UtcNow = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

        await AdapterService.ReportPipelineStatusAsync(TenantId, adapter, skewed);

        await CommunicationRepository.Received(1).SetPipelineStatusMessageAsync(TenantId, pipeline, "line",
            false, Arg.Is<DateTime>(t => t.Kind == DateTimeKind.Utc && t == Clock.UtcNow));
    }
}
