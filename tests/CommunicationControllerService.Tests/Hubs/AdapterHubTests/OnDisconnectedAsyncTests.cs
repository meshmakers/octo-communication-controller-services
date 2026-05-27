using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterHubTests;

internal class OnDisconnectedAsyncTests : IDisposable
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

    public OnDisconnectedAsyncTests()
    {
        _hub = new AdapterHub(
            _adapterService,
            _pipelineDebugService,
            _eventService,
            _pipelineExecutionService,
            _executionReportQueue,
            _shutdownState);

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        _hub.Context = context;
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task ShuttingDown_SkipsOfflineWriteAndInterruptedMark()
    {
        // Rolling-upgrade race regression: the OLD controller pod's
        // OnDisconnectedAsync used to write Offline AFTER the NEW pod had
        // already written Online (newer timestamp wins the
        // AttributeNewerThanGuard), leaving the adapter stuck Offline in
        // the UI even though it was happily processing pipelines on the
        // surviving pod. The shutdown guard runs before GetTenantId() /
        // GetAdapterRtEntityId() so this path works without a usable
        // HttpContext too.
        _shutdownState.IsShuttingDown.Returns(true);

        // Should not throw — and importantly should not touch the HTTP
        // context (no HttpContext set up on the mock).
        await _hub.OnDisconnectedAsync(exception: null);

        await _pipelineExecutionService.DidNotReceiveWithAnyArgs()
            .MarkExecutionsAsInterruptedAsync(Arg.Any<string>(), Arg.Any<RtEntityId>());
        await _adapterService.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateOfflineAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>());
    }
}
