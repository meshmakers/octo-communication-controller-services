using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
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
            .RecordMetricsSample(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdapterMetricsSampleDto>());
    }

    [Test]
    public async Task ReportAdapterMetricsAsync_ServiceThrows_Swallowed()
    {
        // Defense-in-depth: even if the service throws, the hub method
        // must return successfully — telemetry is non-critical.
        var sample = BuildSample();
        _adapterService
            .When(s => s.RecordMetricsSample(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdapterMetricsSampleDto>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        // Should not throw.
        await _hub.ReportAdapterMetricsAsync(sample);
    }

    private const string TenantId = "meshdev";

    private ISingleClientProxy GivenConnectionOfTenant()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature(httpContext));
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        context.Features.Returns(features);
        _hub.Context = context;

        var clients = Substitute.For<IHubCallerClients>();
        var caller = Substitute.For<ISingleClientProxy>();
        clients.Caller.Returns(caller);
        _hub.Clients = clients;
        return caller;
    }

    private sealed class HttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    [Test]
    public async Task ReportAdapterMetricsAsync_PassesTheConnectionIdToTheService()
    {
        GivenConnectionOfTenant();
        var sample = BuildSample();

        await _hub.ReportAdapterMetricsAsync(sample);

        _adapterService.Received(1).RecordMetricsSample(TenantId, ConnectionId, sample);
    }

    [Test]
    public async Task ReportAdapterMetricsAsync_RegistrationLost_AsksTheCallerToRestartAndRegisterAgain()
    {
        // AB#5827: the only signal every adapter build understands as "register again" is the
        // PreUpdateTenant restart callback; it goes to the calling connection, nobody else.
        var caller = GivenConnectionOfTenant();
        var sample = BuildSample();
        _adapterService.RecordMetricsSample(TenantId, ConnectionId, sample)
            .Returns(MetricsSampleOutcome.RegistrationLost);

        await _hub.ReportAdapterMetricsAsync(sample);

        await caller.Received(1).SendCoreAsync(nameof(IAdapterHubCallbacks.PreUpdateTenantAsync),
            Arg.Is<object?[]>(a => a.Length == 1 && (string)a[0]! == TenantId), Arg.Any<CancellationToken>());
        await _eventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("register again")), sample.AdapterRtEntityId);
    }

    [Test]
    [Arguments(MetricsSampleOutcome.Recorded)]
    [Arguments(MetricsSampleOutcome.Dropped)]
    public async Task ReportAdapterMetricsAsync_OtherOutcomes_SendNothing(MetricsSampleOutcome outcome)
    {
        var caller = GivenConnectionOfTenant();
        var sample = BuildSample();
        _adapterService.RecordMetricsSample(TenantId, ConnectionId, sample).Returns(outcome);

        await _hub.ReportAdapterMetricsAsync(sample);

        await caller.DidNotReceiveWithAnyArgs()
            .SendCoreAsync(default!, default!, default);
    }

    [Test]
    public async Task ReportAdapterMetricsAsync_EventStoreFails_StillAskedTheAdapterAndDoesNotThrow()
    {
        var caller = GivenConnectionOfTenant();
        var sample = BuildSample();
        _adapterService.RecordMetricsSample(TenantId, ConnectionId, sample)
            .Returns(MetricsSampleOutcome.RegistrationLost);
        _eventService.StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>())
            .Returns(Task.FromException(new InvalidOperationException("mongo down")));

        await _hub.ReportAdapterMetricsAsync(sample);

        await caller.Received(1).SendCoreAsync(nameof(IAdapterHubCallbacks.PreUpdateTenantAsync),
            Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }
}
