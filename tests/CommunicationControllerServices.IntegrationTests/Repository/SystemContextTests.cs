using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for system context and tenant management.
/// </summary>
[Collection("CommunicationController")]
public class SystemContextTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task IsSystemTenantExisting_ShouldReturnTrue()
    {
        var systemContext = fixture.GetSystemContext();
        var result = await systemContext.IsSystemTenantExistingAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetTestTenant_ShouldReturnTestTenantContext()
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var tenant = await systemContext.GetChildTenantAsync(session, fixture.TestTenantId);

        await session.CommitTransactionAsync();

        tenant.TenantId.Should().Be(fixture.TestTenantId.ToLower());
        tenant.DatabaseName.Should().Be(fixture.TestTenantId.ToLower());
    }

    [Fact]
    public async Task CreateAndDeleteChildTenant_ShouldSucceed()
    {
        var systemContext = fixture.GetSystemContext();
        var tempTenantId = $"temp-tenant-{Guid.NewGuid():N}";

        // Create tenant
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tempTenantId, tempTenantId);
            await session.CommitTransactionAsync();
        }

        // Verify tenant exists
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            var exists = await systemContext.IsChildTenantExistingAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
            exists.Should().BeTrue();
        }

        // Delete tenant
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
        }

        // Verify tenant is deleted
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            var exists = await systemContext.IsChildTenantExistingAsync(session, tempTenantId);
            await session.CommitTransactionAsync();
            exists.Should().BeFalse();
        }
    }
}
