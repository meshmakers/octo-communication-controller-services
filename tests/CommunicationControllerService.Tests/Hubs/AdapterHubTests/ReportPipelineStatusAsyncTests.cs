using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterHubTests;

/// <summary>
/// AB#5385: the status line a trigger node reports after a poll. The hub resolves tenant and
/// adapter from the connection and hands the line to the service; like the metrics sample it is
/// fire-and-forget on the wire, so nothing thrown here may reach the connection.
/// </summary>
internal class ReportPipelineStatusAsyncTests : IDisposable
{
    private const string ConnectionId = "conn-1";
    private const string TenantId = "meshtest";

    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly IPipelineDebugService _pipelineDebugService = Substitute.For<IPipelineDebugService>();
    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly IPipelineExecutionService _pipelineExecutionService =
        Substitute.For<IPipelineExecutionService>();
    private readonly IPipelineExecutionReportQueue _executionReportQueue =
        Substitute.For<IPipelineExecutionReportQueue>();
    private readonly IShutdownState _shutdownState = Substitute.For<IShutdownState>();

    // Dev-lane only: AdapterHub gained IWorkloadLifecycleService (the on-demand wake path). This
    // test arrived with the AB#5385 merge from main, where the constructor still has six parameters.
    private readonly IWorkloadLifecycleService _workloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();
    private readonly AdapterHub _hub;
    private readonly HubCallerContext _context = Substitute.For<HubCallerContext>();

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    public ReportPipelineStatusAsyncTests()
    {
        _hub = new AdapterHub(
            _adapterService,
            _pipelineDebugService,
            _eventService,
            _pipelineExecutionService,
            _executionReportQueue,
            _shutdownState,
            _workloadLifecycleService);

        _context.ConnectionId.Returns(ConnectionId);
        _hub.Context = _context;
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Installs the request an adapter connection carries: the tenant as route value and the
    /// adapter identity in the two headers <c>GetAdapterRtEntityId</c> reads.
    /// </summary>
    private RtEntityId InstallAdapterConnection()
    {
        var adapter = RtEntityCreator.CreateAdapter().ToRtEntityId();
        // The route value and the two header names are the ones Constants.GetTenantId /
        // GetAdapterRtEntityId read (the header constants are private there).
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        httpContext.Request.Headers["adapter-rtId"] = adapter.RtId.ToString();
        httpContext.Request.Headers["adapter-ckTypeId"] = adapter.CkTypeId.ToString();

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));
        _context.Features.Returns(features);
        return adapter;
    }

    private static PipelineStatusReportDto BuildStatus(bool isError = false)
        => new()
        {
            PipelineRtEntityId = RtEntityCreator.CreatePipeline().ToRtEntityId(),
            Message = "2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox · seen 12, imported 12",
            IsError = isError,
            TimestampUtc = DateTime.UtcNow
        };

    [Test]
    public async Task ReportPipelineStatusAsync_ForwardsTenantAdapterAndLineToTheService()
    {
        var adapter = InstallAdapterConnection();
        var status = BuildStatus();

        await _hub.ReportPipelineStatusAsync(status);

        await _adapterService.Received(1).ReportPipelineStatusAsync(TenantId, adapter, status);
    }

    [Test]
    public async Task ReportPipelineStatusAsync_MissingTenantContext_SwallowsAndDoesNotInvokeService()
    {
        // No HttpContext on the mock: GetTenantId throws InvalidOperationException. The handler
        // must swallow it — the report is fire-and-forget and the connection stays in use.
        await _hub.ReportPipelineStatusAsync(BuildStatus());

        await _adapterService.DidNotReceiveWithAnyArgs()
            .ReportPipelineStatusAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<PipelineStatusReportDto>());
    }

    [Test]
    public async Task ReportPipelineStatusAsync_ServiceThrows_Swallowed()
    {
        InstallAdapterConnection();
        _adapterService
            .ReportPipelineStatusAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<PipelineStatusReportDto>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        // Should not throw.
        await _hub.ReportPipelineStatusAsync(BuildStatus(isError: true));
    }
}
