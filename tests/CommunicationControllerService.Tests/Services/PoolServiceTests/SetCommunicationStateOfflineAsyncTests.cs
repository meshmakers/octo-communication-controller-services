using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal class SetCommunicationStateOfflineAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task SetCommunicationStateOfflineAsync_PoolFoundByName_WritesOffline()
    {
        // Happy path for the OnDisconnectedAsync fix in PoolHub.cs: when the hub passes the
        // correct poolName, the service must look the pool up, find it in the cache and
        // write Offline. This is the only path that flips state when the operator's
        // SignalR connection drops without a graceful UnregisterPoolOperatorAsync.
        GivenTenantInCache();
        AddPoolToTenant();

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, PoolName);

        await CommunicationRepository.Received(1)
            .SetPoolCommunicationStateAsync(TenantId, PoolRtId, RtCommunicationStateEnum.Offline);
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

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, "some-signalr-connection-id");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_TenantNotInCache_NoOp()
    {
        GivenTenantNotInCache();

        await PoolService.SetCommunicationStateOfflineAsync(TenantId, PoolName);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }
}
