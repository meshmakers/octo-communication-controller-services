using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal class SetCommunicationStateOfflineAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task SetCommunicationStateOfflineAsync_PoolFoundByNameAndConnectionMatches_WritesOffline()
    {
        // Happy path for the OperatorHub.OnDisconnectedAsync fix: when the hub passes the
        // correct poolName and the disconnecting connection still owns the cached pool,
        // the service must write Offline. This is the only path that flips state when
        // the operator's SignalR connection drops without a graceful UnregisterPoolOperatorAsync.
        GivenTenantInCache();
        AddPoolToTenant();

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, PoolName, ConnectionId);

        await CommunicationRepository.Received(1)
            .SetPoolCommunicationStateAsync(TenantId, PoolRtId, RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_StaleDisconnectFromReplacedConnection_NoOp()
    {
        // Regression: a controller restart drops the operator's previous SignalR connection.
        // The operator auto-reconnects with a NEW connection id and re-registers its pools
        // (cache.ConnectionId = new id). Some time later the previous connection's
        // OnDisconnectedAsync finally fires on the controller, attempting to mark every pool
        // it had claimed Offline. Without the stale-disconnect guard this overwrites the
        // freshly-written Online state and the UI shows the pool offline even though the
        // operator is connected. Mirrors the adapter-side guard in
        // AdapterService.SetAdapterCommunicationStateOfflineAsync.
        GivenTenantInCache();
        AddPoolToTenant(connectionId: "new-connection-id");

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, PoolName,
            "stale-old-connection-id");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_CalledWithConnectionIdInsteadOfPoolName_NoOp()
    {
        // Regression: PoolHub.OnDisconnectedAsync used to pass Context.ConnectionId where a
        // poolName was expected. The lookup then silently failed and the pool stayed Online
        // forever. This test pins the contract so the bug cannot be reintroduced by
        // swapping arguments back.
        GivenTenantInCache();
        AddPoolToTenant();

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, "some-signalr-connection-id",
            ConnectionId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_TenantNotInCache_NoOp()
    {
        GivenTenantNotInCache();

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, PoolName, ConnectionId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }
}
