using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class PosUpdateTenantAsyncTests : AdapterServiceTestsBase
{
    [Test]
    public async Task PosUpdateTenantAsync_Success_CallsAddOrUpdateTenant()
    {
        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        AdapterCache.Received(1).AddOrUpdateTenant(TenantId);
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
