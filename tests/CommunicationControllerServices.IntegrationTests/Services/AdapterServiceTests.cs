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
/// These tests cover the Pre/Pos tenant update flow against persisted state, in particular the
/// regression where <c>PosUpdateTenantAsync</c> would unconditionally mark every adapter Offline —
/// including those still holding a live SignalR connection — leading to a connected adapter being
/// reported as offline in the UI.
/// </summary>
[Collection("CommunicationController")]
public class AdapterServiceTests(CommunicationControllerFixture fixture)
{
    private const string FakeConnectionId = "fake-connection-id";

    [Fact]
    public async Task PosUpdateTenantAsync_AdapterNotInCache_MarksAdapterOfflineInDatabase()
    {
        var tenantId = fixture.TestTenantId;
        var adapterRtEntityId = await CreateAdapterInDatabaseAsync(RtCommunicationStateEnum.Online);

        try
        {
            var adapterService = fixture.GetService<IAdapterService>();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var repository = fixture.GetService<ICommunicationRepository>();
            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Offline);
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
    public async Task PosUpdateTenantAsync_AdapterInCacheWithoutConnection_MarksOffline()
    {
        // An adapter that was registered earlier but has since disconnected (no
        // ConnectionId in the cache) must be marked Offline by a tenant post-update.
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
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Offline);
        }
        finally
        {
            adapterTenant.RemoveAdapter(adapterRtEntityId);
            await CleanupAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task PreThenPosUpdate_AdapterWithoutLiveConnection_TransitionsThroughUnregisteredToOffline()
    {
        // Validates the proper Pre→Pos sequence at the persistence layer:
        //   Pre marks the adapter Unregistered (and clears it from the cache),
        //   Pos then marks it Offline since it has no live connection any more.
        // The adapter has no ConnectionId so the SignalR fan-out in PreUpdate is a no-op.
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
            afterPre.CommunicationState.Should().Be(RtCommunicationStateEnum.Unregistered);
            adapterCache.TryGetTenant(tenantId, out _).Should().BeFalse();

            await adapterService.PosUpdateTenantAsync(tenantId);

            var afterPos = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            afterPos.CommunicationState.Should().Be(RtCommunicationStateEnum.Offline);
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
