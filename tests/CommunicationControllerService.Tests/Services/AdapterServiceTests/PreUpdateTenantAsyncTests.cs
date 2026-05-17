using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class PreUpdateTenantAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PreUpdateTenantAsync_TenantNotInCache_DoesNotThrow()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        // Act - should not throw
        await AdapterService.PreUpdateTenantAsync("unknownTenant");

        // Assert - no callbacks should be made
        await AdapterHubCallbacks.DidNotReceive()
            .PreUpdateTenantAsync(Arg.Any<string>());
        AdapterCache.DidNotReceive()
            .RemoveTenant(Arg.Any<string>());
    }

    [Test]
    public async Task PreUpdateTenantAsync_TenantWithNoAdapters_RemovesTenantAndCallsCallback()
    {
        // Arrange - Tenant exists but has no adapters

        // Act
        await AdapterService.PreUpdateTenantAsync(TenantId);

        // Assert
        using var _ = Assert.Multiple();

        await AdapterHubCallbacks.Received(1).PreUpdateTenantAsync(TenantId);
        AdapterCache.Received(1).RemoveTenant(TenantId);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PreUpdateTenantAsync_TenantWithSingleAdapter_RemovesTenantAndPreservesAdapterState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        // Act
        await AdapterService.PreUpdateTenantAsync(TenantId);

        // Assert
        using var _ = Assert.Multiple();

        await AdapterHubCallbacks.Received(1).PreUpdateTenantAsync(TenantId);
        AdapterCache.Received(1).RemoveTenant(TenantId);
        // Pre/PosUpdate no longer mass-resets CommunicationState; live
        // adapter pods keep their SignalR connection through the tenant
        // cache reload and would have their Online state clobbered if we
        // wrote Unregistered here.
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PreUpdateTenantAsync_TenantWithMultipleAdapters_RemovesTenantAndPreservesAllAdapterStates()
    {
        // Arrange
        var rtAdapter1 = RtEntityCreator.CreateAdapter();
        var rtAdapter2 = RtEntityCreator.CreateAdapter();
        var rtAdapter3 = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(rtAdapter1.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter1.ToRtEntityId(),
            null,
            []
        ));

        AdapterTenant.AddAdapter(rtAdapter2.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter2.ToRtEntityId(),
            null,
            []
        ));

        AdapterTenant.AddAdapter(rtAdapter3.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter3.ToRtEntityId(),
            null,
            []
        ));

        // Act
        await AdapterService.PreUpdateTenantAsync(TenantId);

        // Assert
        using var _ = Assert.Multiple();

        await AdapterHubCallbacks.Received(1).PreUpdateTenantAsync(TenantId);
        AdapterCache.Received(1).RemoveTenant(TenantId);
        // CommunicationState is never touched here regardless of how many
        // adapters the tenant has — see the single-adapter test above for
        // the rationale.
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PreUpdateTenantAsync_CallbackThrowsException_WrapsInAdapterServiceException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Callback failed");
        AdapterHubCallbacks.PreUpdateTenantAsync(TenantId)
            .Returns(Task.FromException(expectedException));

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.PreUpdateTenantAsync(TenantId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pre update tenant failed"));

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.InnerException, inner => inner.IsEqualTo(expectedException));
    }

    [Test]
    public async Task PreUpdateTenantAsync_CallbackThrowsAfterCacheFlush_WrapsInAdapterServiceException()
    {
        // Arrange — there's no mass-reset of CommunicationState any more,
        // so the only repo-touching call left is via the AdapterHubCallbacks
        // path. Reuse the same exception-propagation contract via the
        // callback fault path instead.
        var rtAdapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        var expectedException = new InvalidOperationException("Callback failed");
        AdapterHubCallbacks.PreUpdateTenantAsync(TenantId)
            .Returns(Task.FromException(expectedException));

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.PreUpdateTenantAsync(TenantId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pre update tenant failed"));

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.InnerException, inner => inner.IsEqualTo(expectedException));
    }

    [Test]
    public async Task PreUpdateTenantAsync_CallsInCorrectOrder()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var callOrder = new List<string>();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        AdapterHubCallbacks.PreUpdateTenantAsync(TenantId)
            .Returns(_ =>
            {
                callOrder.Add("PreUpdateTenantCallback");
                return Task.CompletedTask;
            });

        AdapterCache.When(x => x.RemoveTenant(TenantId))
            .Do(_ => callOrder.Add("RemoveTenant"));

        // Act
        await AdapterService.PreUpdateTenantAsync(TenantId);

        // Assert — callback must run before the cache flush so adapters
        // get one last "tenant is going down" signal while the cache still
        // lets us reach them. The repo-write step that used to follow was
        // removed; CommunicationState is owned by the (dis)connect handlers.
        await Assert.That(callOrder).Count().IsEqualTo(2);
        await Assert.That(callOrder[0]).IsEqualTo("PreUpdateTenantCallback");
        await Assert.That(callOrder[1]).IsEqualTo("RemoveTenant");
    }
}
