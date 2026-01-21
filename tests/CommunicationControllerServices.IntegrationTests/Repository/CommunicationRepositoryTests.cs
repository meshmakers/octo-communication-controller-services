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
[Collection("Sequential")]
public class CommunicationRepositoryTests(CommunicationControllerFixture fixture)
    : IClassFixture<CommunicationControllerFixture>
{
    [Fact]
    public async Task GetAdapter_WhenNotExists_ShouldReturnNull()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterId = new RtEntityId(SystemCommunicationCkIds.RtCkEdgeAdapterTypeId, OctoObjectId.GenerateNewId());

        var adapter = await repository.GetAdapterAsync(fixture.TestTenantId, adapterId);

        adapter.Should().BeNull();
    }

    [Fact]
    public async Task GetAdapters_WhenEmpty_ShouldReturnEmptyList()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var adapters = await repository.GetAdaptersAsync(fixture.TestTenantId);

        adapters.Should().NotBeNull();
        adapters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPools_WhenEmpty_ShouldReturnEmptyList()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var pools = await repository.GetPoolsAsync(fixture.TestTenantId);

        pools.Should().NotBeNull();
        pools.Should().BeEmpty();
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

        // Act
        await repository.CreatePoolAsync(fixture.TestTenantId, poolName);

        // Assert
        var pools = await repository.GetPoolByNameAsync(fixture.TestTenantId, poolName);
        pools.Should().ContainSingle();
        pools.First().Name.Should().Be(poolName);
    }
}
