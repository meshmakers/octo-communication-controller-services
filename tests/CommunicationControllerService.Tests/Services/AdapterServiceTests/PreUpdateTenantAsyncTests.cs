using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
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
    public async Task PreUpdateTenantAsync_TenantWithSingleAdapter_RemovesTenantAndUpdatesAdapterState()
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
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Unregistered);
    }

    [Test]
    public async Task PreUpdateTenantAsync_TenantWithMultipleAdapters_RemovesTenantAndUpdatesAllAdapterStates()
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
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter1.ToRtEntityId(), RtCommunicationStateEnum.Unregistered);
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter2.ToRtEntityId(), RtCommunicationStateEnum.Unregistered);
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter3.ToRtEntityId(), RtCommunicationStateEnum.Unregistered);
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
    public async Task PreUpdateTenantAsync_RepositoryThrowsException_WrapsInAdapterServiceException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        var expectedException = new InvalidOperationException("Repository failed");
        CommunicationRepository.SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Unregistered)
            .Returns(Task.FromException(expectedException));

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.PreUpdateTenantAsync(TenantId))
            .Throws<AdapterServiceException>();

        using var _ = Assert.Multiple();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pre update tenant failed"));

        var innerEx = exception?.InnerException;
        await Assert.That(innerEx).IsNotNull();
        await Assert.That(innerEx!.InnerException).IsNotNull();
        await Assert.That(innerEx.InnerException).IsEqualTo(expectedException);
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

        CommunicationRepository.SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Unregistered)
            .Returns(_ =>
            {
                callOrder.Add("SetAdapterCommunicationState");
                return Task.CompletedTask;
            });

        // Act
        await AdapterService.PreUpdateTenantAsync(TenantId);

        // Assert
        await Assert.That(callOrder).Count().IsEqualTo(3);
        await Assert.That(callOrder[0]).IsEqualTo("PreUpdateTenantCallback");
        await Assert.That(callOrder[1]).IsEqualTo("RemoveTenant");
        await Assert.That(callOrder[2]).IsEqualTo("SetAdapterCommunicationState");
    }
}
