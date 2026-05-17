using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Services;

/// <summary>
/// Integration tests for the AdapterService against a real MongoDB instance.
///
/// CommunicationState transitions are owned by the SignalR (dis)connect handlers, not by the
/// tenant cache lifecycle hooks. The Pre/Pos tenant update flow must therefore leave the
/// persisted <c>CommunicationState</c> untouched. These tests pin that behavior against the
/// regression where <c>PosUpdateTenantAsync</c> would mass-reset every adapter to Offline on
/// the nightly tenant cache reload, flipping live adapters offline in the UI.
/// </summary>
[Collection("CommunicationController")]
public class AdapterServiceTests(CommunicationControllerFixture fixture)
{
    private const string FakeConnectionId = "fake-connection-id";

    [Fact]
    public async Task PosUpdateTenantAsync_AdapterNotInCache_PreservesPersistedState()
    {
        // Regression test: a tenant cache reload must not rewrite persisted CommunicationState.
        // An adapter that is Online in the DB but absent from the in-memory cache (because the
        // cache was just flushed by PreUpdate) must stay Online — the live SignalR connection
        // is what owns this value, and the (dis)connect handlers are the only writers.
        var tenantId = fixture.TestTenantId;
        var adapterRtEntityId = await CreateAdapterInDatabaseAsync(RtCommunicationStateEnum.Online);

        try
        {
            var adapterService = fixture.GetService<IAdapterService>();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var repository = fixture.GetService<ICommunicationRepository>();
            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        }
        finally
        {
            await CleanupAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task PosUpdateTenantAsync_AdapterConnectedInCache_PreservesOnlineState()
    {
        // Regression test: a connected adapter (active SignalR connection in the cache)
        // must not be flipped to Offline in the DB by a tenant post-update.
        var tenantId = fixture.TestTenantId;
        var adapterRtEntityId = await CreateAdapterInDatabaseAsync(RtCommunicationStateEnum.Online);

        var adapterCache = fixture.GetService<IAdapterCache>();
        adapterCache.AddOrUpdateTenant(tenantId);
        adapterCache.TryGetTenant(tenantId, out var adapterTenant).Should().BeTrue();
        adapterTenant!.AddAdapter(adapterRtEntityId, FakeConnectionId,
            new AdapterConfigurationDto(adapterRtEntityId, null, []));

        try
        {
            var adapterService = fixture.GetService<IAdapterService>();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var repository = fixture.GetService<ICommunicationRepository>();
            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        }
        finally
        {
            adapterTenant.RemoveAdapter(adapterRtEntityId);
            await CleanupAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task PosUpdateTenantAsync_AdapterInCacheWithoutConnection_PreservesPersistedState()
    {
        // An adapter that was registered earlier but has since lost its ConnectionId in the cache
        // must NOT be flipped by a tenant post-update. The SignalR OnDisconnectedAsync handler
        // is responsible for writing Offline; the tenant cache reload is not.
        var tenantId = fixture.TestTenantId;
        var adapterRtEntityId = await CreateAdapterInDatabaseAsync(RtCommunicationStateEnum.Online);

        var adapterCache = fixture.GetService<IAdapterCache>();
        adapterCache.AddOrUpdateTenant(tenantId);
        adapterCache.TryGetTenant(tenantId, out var adapterTenant).Should().BeTrue();
        adapterTenant!.AddAdapter(adapterRtEntityId, FakeConnectionId,
            new AdapterConfigurationDto(adapterRtEntityId, null, []));
        adapterTenant.RemoveConnectionId(adapterRtEntityId);

        try
        {
            var adapterService = fixture.GetService<IAdapterService>();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var repository = fixture.GetService<ICommunicationRepository>();
            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        }
        finally
        {
            adapterTenant.RemoveAdapter(adapterRtEntityId);
            await CleanupAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task PreThenPosUpdate_AdapterWithoutLiveConnection_PreservesPersistedStateAndFlushesCache()
    {
        // Validates the new Pre→Pos sequence at the persistence layer: neither phase touches
        // the persisted CommunicationState. The only observable side-effect is that PreUpdate
        // drops the tenant entry from the in-memory adapter cache; PosUpdate is a no-op for
        // persisted state. State transitions are owned by the SignalR (dis)connect handlers.
        var tenantId = fixture.TestTenantId;
        var adapterRtEntityId = await CreateAdapterInDatabaseAsync(RtCommunicationStateEnum.Online);

        var adapterCache = fixture.GetService<IAdapterCache>();
        adapterCache.AddOrUpdateTenant(tenantId);
        adapterCache.TryGetTenant(tenantId, out var adapterTenant).Should().BeTrue();
        adapterTenant!.AddAdapter(adapterRtEntityId, FakeConnectionId,
            new AdapterConfigurationDto(adapterRtEntityId, null, []));
        adapterTenant.RemoveConnectionId(adapterRtEntityId);

        try
        {
            var adapterService = fixture.GetService<IAdapterService>();
            var repository = fixture.GetService<ICommunicationRepository>();

            await adapterService.PreUpdateTenantAsync(tenantId);

            var afterPre = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            afterPre.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
            adapterCache.TryGetTenant(tenantId, out _).Should().BeFalse();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var afterPos = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            afterPos.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        }
        finally
        {
            if (adapterCache.TryGetTenant(tenantId, out var existingTenant))
            {
                existingTenant.RemoveAdapter(adapterRtEntityId);
            }

            await CleanupAdapterAsync(adapterRtEntityId);
        }
    }

    private async Task<RtEntityId> CreateAdapterInDatabaseAsync(RtCommunicationStateEnum initialState)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var adapter = new RtAdapter
        {
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = $"int-test-adapter-{Guid.NewGuid():N}",
            CommunicationState = initialState,
            DeploymentState = RtDeploymentStateEnum.Undeployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured
        };

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapter>> { EntityUpdateInfo<RtAdapter>.CreateInsert(adapter) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(
                $"Failed to insert test adapter: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        return new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
    }

    private async Task CleanupAdapterAsync(RtEntityId adapterRtEntityId)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtAdapter>>
                {
                    EntityUpdateInfo<RtAdapter>.CreateDelete(adapterRtEntityId)
                },
                operationResult);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            // Cleanup is best-effort; swallow to avoid masking the original test result.
        }
    }
}
