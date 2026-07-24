using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class CkModelChangedAsyncTests : AdapterServiceTestsBase
{
    [Test]
    public async Task DelegatesToHubCallbacks()
    {
        // Act
        await AdapterService.CkModelChangedAsync(TenantId);

        // Assert
        await AdapterHubCallbacks.Received(1).CkModelChangedAsync(TenantId);
    }

    [Test]
    public async Task HubSendFails_ThrowsAdapterServiceException()
    {
        // Arrange
        AdapterHubCallbacks.CkModelChangedAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("hub send failed"));

        // Act + Assert
        await Assert.ThrowsAsync<AdapterServiceException>(
            () => AdapterService.CkModelChangedAsync(TenantId));
    }
}
