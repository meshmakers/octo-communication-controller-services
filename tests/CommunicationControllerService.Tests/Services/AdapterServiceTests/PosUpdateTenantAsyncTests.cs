using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class PosUpdateTenantAsyncTests : AdapterServiceTestsBase
{
    [Test]
    public async Task PosUpdateTenantAsync_Success_CallsAddOrUpdateTenant()
    {
        // Arrange
        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(Array.Empty<RtAdapter>());

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        AdapterCache.Received(1).AddOrUpdateTenant(TenantId);
    }

    [Test]
    public async Task PosUpdateTenantAsync_DoesNotResetAdapterCommunicationState()
    {
        // The previous behaviour iterated every adapter in the tenant and
        // flipped its DB state to Offline. That clobbered the Online state of
        // pods that kept their SignalR connection across the cache flush —
        // see PreUpdateTenantAsync for the full rationale. PosUpdate now only
        // re-initialises the in-memory cache; CommunicationState writes are
        // owned exclusively by the (dis)connect handlers.
        var adapter1 = RtEntityCreator.CreateAdapter();
        var adapter2 = RtEntityCreator.CreateAdapter();
        adapter1.CommunicationState = RtCommunicationStateEnum.Online;
        adapter2.CommunicationState = RtCommunicationStateEnum.Online;

        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(new[] { adapter1, adapter2 });

        await AdapterService.PosUpdateTenantAsync(TenantId);

        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PosUpdateTenantAsync_WithNoAdapters_DoesNotCallSetCommunicationState()
    {
        // Arrange
        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(Array.Empty<RtAdapter>());

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PosUpdateTenantAsync_CacheThrowsException_WrapsInAdapterServiceException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Cache operation failed");
        AdapterCache.When(x => x.AddOrUpdateTenant(TenantId))
            .Do(_ => throw expectedException);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.PosUpdateTenantAsync(TenantId))
            .Throws<AdapterServiceException>();

        using var _ = Assert.Multiple();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pos update tenant failed"));

        var innerEx = exception?.InnerException;
        await Assert.That(innerEx).IsNotNull()
            .And.IsEqualTo(expectedException);
    }
}
