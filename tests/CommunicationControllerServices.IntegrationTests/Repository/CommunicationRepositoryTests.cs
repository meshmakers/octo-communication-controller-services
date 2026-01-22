using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the CommunicationRepository.
/// </summary>
[Collection("CommunicationController")]
public class CommunicationRepositoryTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task GetAdapter_WhenNotExists_ShouldThrowException()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterId = new RtEntityId(SystemCommunicationCkIds.RtCkEdgeAdapterTypeId, OctoObjectId.GenerateNewId());

        var act = async () => await repository.GetAdapterAsync(fixture.TestTenantId, adapterId);

        await act.Should().ThrowAsync<CommunicationRepositoryException>();
    }

    [Fact]
    public async Task GetAdapters_ShouldReturnValidCollection()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var adapters = await repository.GetAdaptersAsync(fixture.TestTenantId);

        // In a shared test environment, we can't guarantee an empty database.
        // We verify the method returns a valid collection.
        adapters.Should().NotBeNull();
        adapters.Should().AllSatisfy(a => a.Should().BeAssignableTo<RtAdapter>());
    }

    [Fact]
    public async Task GetPools_ShouldReturnValidCollection()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var pools = await repository.GetPoolsAsync(fixture.TestTenantId);

        // In a shared test environment, we can't guarantee an empty database.
        // We verify the method returns a valid collection.
        pools.Should().NotBeNull();
        pools.Should().AllSatisfy(p => p.Should().BeOfType<RtPool>());
    }

    [Fact]
    public async Task IsTenantExisting_ForTestTenant_ShouldReturnTrue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var exists = await repository.IsTenantExistingAsync(fixture.TestTenantId);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task IsTenantExisting_ForNonExistentTenant_ShouldReturnFalse()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var exists = await repository.IsTenantExistingAsync("non-existent-tenant");

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task CreatePool_ShouldCreatePoolSuccessfully()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var poolName = $"test-pool-{Guid.NewGuid():N}";

        // Act - verify that creating a pool doesn't throw
        var act = async () => await repository.CreatePoolAsync(fixture.TestTenantId, poolName);
        await act.Should().NotThrowAsync();
    }
}
