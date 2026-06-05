using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterHubTests;

internal class ReportAdapterMetricsAsyncTests : IDisposable
{
    private const string ConnectionId = "conn-1";

    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly IPipelineDebugService _pipelineDebugService = Substitute.For<IPipelineDebugService>();
    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly IPipelineExecutionService _pipelineExecutionService =
        Substitute.For<IPipelineExecutionService>();
    private readonly IPipelineExecutionReportQueue _executionReportQueue =
        Substitute.For<IPipelineExecutionReportQueue>();
    private readonly IShutdownState _shutdownState = Substitute.For<IShutdownState>();
    private readonly AdapterHub _hub;

    public ReportAdapterMetricsAsyncTests()
    {
        _hub = new AdapterHub(
            _adapterService,
            _pipelineDebugService,
            _eventService,
            _pipelineExecutionService,
            _executionReportQueue,
            _shutdownState);

        // Intentionally no HttpContext on the mock — the handler must swallow
        // the resulting "TenantId is null" exception, see test below.
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        _hub.Context = context;
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    private static AdapterMetricsSampleDto BuildSample()
        => new()
        {
            AdapterRtEntityId = RtEntityCreator.CreateAdapter().ToRtEntityId(),
            Timestamp = DateTime.UtcNow,
            CpuPercent = 13,
            WorkingSetBytes = 1024,
            GcHeapBytes = 512,
            ThreadCount = 8
        };

    [Test]
    public async Task ReportAdapterMetricsAsync_MissingTenantContext_SwallowsAndDoesNotInvokeService()
    {
        // Without an HttpContext the GetTenantId helper throws InvalidOperationException.
        // The handler MUST swallow it so the fire-and-forget SignalR connection
        // is not torn down for the rest of the hub's traffic.
        var sample = BuildSample();

        await _hub.ReportAdapterMetricsAsync(sample);

        _adapterService.DidNotReceiveWithAnyArgs()
            .RecordMetricsSample(Arg.Any<string>(), Arg.Any<AdapterMetricsSampleDto>());
    }

    [Test]
    public async Task ReportAdapterMetricsAsync_ServiceThrows_Swallowed()
    {
        // Defense-in-depth: even if the service throws, the hub method
        // must return successfully — telemetry is non-critical.
        var sample = BuildSample();
        _adapterService
            .When(s => s.RecordMetricsSample(Arg.Any<string>(), Arg.Any<AdapterMetricsSampleDto>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        // Should not throw.
        await _hub.ReportAdapterMetricsAsync(sample);
    }
}
