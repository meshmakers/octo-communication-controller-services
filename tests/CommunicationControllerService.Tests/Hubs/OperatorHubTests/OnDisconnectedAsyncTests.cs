using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

internal class OnDisconnectedAsyncTests : IDisposable
{
    private const string TenantId = "meshtest";
    private const string ConnectionId = "conn-1";
    private const string PoolRtId = "6ad562f3ff7c40ff80275b84";

    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly IPoolService _poolService =
        Substitute.For<IPoolService>();
    private readonly IShutdownState _shutdownState =
        Substitute.For<IShutdownState>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly OperatorHub _hub;

    public OnDisconnectedAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService, _shutdownState,
            _eventService);

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
    public async Task NormalDisconnect_WritesOfflineForEveryOrphanedPool()
    {
        _shutdownState.IsShuttingDown.Returns(false);
        _connectionManager.RemoveOperator(ConnectionId).Returns(
            new[] { (TenantId, PoolRtId) });

        await _hub.OnDisconnectedAsync(exception: null);

        await _poolService.Received(1).SetCommunicationStateOfflineAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == PoolRtId),
            ConnectionId);
    }

    [Test]
    public async Task ShuttingDown_SkipsOfflineWritesButStillRemovesOperator()
    {
        // Rolling-upgrade race regression: the OLD controller pod's
        // OnDisconnectedAsync used to write Offline AFTER the NEW pod had
        // already written Online (newer timestamp wins the
        // AttributeNewerThanGuard), leaving every pool stuck Offline.
        // While the host is stopping the surviving pod is authoritative;
        // this pod must not touch CommunicationState here.
        _shutdownState.IsShuttingDown.Returns(true);
        _connectionManager.RemoveOperator(ConnectionId).Returns(
            new[] { (TenantId, PoolRtId) });

        await _hub.OnDisconnectedAsync(exception: null);

        // The local connection entry should still be cleaned up so any
        // late hub method calls don't see a stale connection.
        _connectionManager.Received(1).RemoveOperator(ConnectionId);

        // Critically: no Offline write — that's the whole point.
        await _poolService.DidNotReceiveWithAnyArgs().SetCommunicationStateOfflineAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string>());
    }
}
