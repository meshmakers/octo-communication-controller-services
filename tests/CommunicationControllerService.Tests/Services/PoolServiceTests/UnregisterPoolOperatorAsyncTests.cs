using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal class UnregisterPoolOperatorAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task UnregisterPoolOperatorAsync_TenantNotInCache_NoOp()
    {
        GivenTenantNotInCache();

        await PoolService.UnregisterPoolOperatorAsync(TenantId, PoolName);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolDeploymentStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtDeploymentStateEnum>());
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolNotInTenant_NoOp()
    {
        GivenTenantInCache();
        // Don't add the pool

        await PoolService.UnregisterPoolOperatorAsync(TenantId, PoolName);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_WritesUnregisteredBeforeRemovingFromCache()
    {
        // This is the regression for the bug observed in production: the order of writes
        // matters. SetPoolCommunicationStateAsync must run while the pool is still in the
        // _poolCache, otherwise the subsequent OnDisconnectedAsync (which is the only
        // other path that flips the state) can no longer locate the pool either, and the
        // UI keeps showing the pool as Online forever.
        GivenTenantInCache();
        AddPoolToTenant();

        var receivedStateAtRepoCall = (Online: false, Offline: false, Unregistered: false);

        // Capture whether the pool was still in the cache at the moment the repo write happened.
        await CommunicationRepository.SetPoolCommunicationStateAsync(TenantId, PoolRtId,
            Arg.Do<RtCommunicationStateEnum>(_ =>
            {
                receivedStateAtRepoCall.Unregistered = PoolTenant.PoolsById.ContainsKey(PoolRtId);
            }));

        await PoolService.UnregisterPoolOperatorAsync(TenantId, PoolName);

        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1)
            .SetPoolCommunicationStateAsync(TenantId, PoolRtId, RtCommunicationStateEnum.Unregistered);
        await Assert.That(receivedStateAtRepoCall.Unregistered).IsTrue();
        await Assert.That(PoolTenant.PoolsById.ContainsKey(PoolRtId)).IsFalse();
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_SetsDeploymentStatePending()
    {
        GivenTenantInCache();
        AddPoolToTenant();

        await PoolService.UnregisterPoolOperatorAsync(TenantId, PoolName);

        await CommunicationRepository.Received(1)
            .SetPoolDeploymentStateAsync(TenantId, PoolRtId, RtDeploymentStateEnum.Pending);
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_StoresInformationEvent()
    {
        GivenTenantInCache();
        AddPoolToTenant();

        await PoolService.UnregisterPoolOperatorAsync(TenantId, PoolName);

        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId,
                Arg.Is<string>(s => s.Contains(PoolName) && s.Contains("unregistered")),
                Arg.Any<RtEntityId>());
    }
}
