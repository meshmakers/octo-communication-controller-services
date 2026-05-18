using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     Regression coverage for the stale-state-write race:
///     a controller pod mid-shutdown commits Offline LATE — after the replacement pod has
///     already written Online — leaving the DB stuck on Offline even though the adapter is
///     healthy and re-registered. CommunicationRepository.SetAdapterCommunicationStateAsync
///     and SetPoolCommunicationStateAsync now write via
///     EntityUpdateInfo.CreateConditionalUpdate with an AttributeNewerThanGuard on
///     communicationStateTimestamp, so a write whose timestamp is older than the persisted
///     one is silently dropped at the MongoDB filter level.
/// </summary>
[Collection("CommunicationController")]
public class StaleStateWriteProtectionTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task SetAdapterCommunicationStateAsync_OlderThanPersisted_DoesNotOverwrite()
    {
        // Arrange: insert an adapter with a future communicationStateTimestamp. This stands
        // in for "the replacement pod has already committed a more-recent state".
        var repository = fixture.GetService<ICommunicationRepository>();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        var adapterRtId = OctoObjectId.GenerateNewId();
        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapterRtId);
        var futureTimestamp = DateTime.UtcNow.AddHours(1);

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtAdapter = await tenantRepository.CreateTransientRtEntityAsync<RtAdapter>();
            rtAdapter.RtId = adapterRtId;
            rtAdapter.Name = "stale-write-test-adapter";
            rtAdapter.CommunicationState = RtCommunicationStateEnum.Online;
            rtAdapter.CommunicationStateTimestamp = futureTimestamp;
            await tenantRepository.InsertOneRtEntityAsync(session, rtAdapter);
            await session.CommitTransactionAsync();
        }

        // Act: a stale writer (using DateTime.UtcNow, which is < the inserted future ts)
        // attempts to flip the state to Offline. The guard must reject this.
        await repository.SetAdapterCommunicationStateAsync(fixture.TestTenantId, adapterRtEntityId,
            RtCommunicationStateEnum.Offline);

        // Assert: the persisted state is unchanged.
        // BeCloseTo (not Be) because MongoDB stores DateTime at millisecond precision —
        // .NET DateTime carries ticks, so the round-trip drops sub-ms digits.
        var loaded = await repository.GetAdapterAsync(fixture.TestTenantId, adapterRtEntityId);
        loaded.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        loaded.CommunicationStateTimestamp.Should().BeCloseTo(futureTimestamp, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SetAdapterCommunicationStateAsync_NewerThanPersisted_AppliesWrite()
    {
        // Arrange: insert an adapter with a past communicationStateTimestamp. The next
        // SetAdapterCommunicationStateAsync captures a NewValue = DateTime.UtcNow, which is
        // newer than the persisted value, so the guard allows the write.
        var repository = fixture.GetService<ICommunicationRepository>();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        var adapterRtId = OctoObjectId.GenerateNewId();
        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapterRtId);
        var pastTimestamp = DateTime.UtcNow.AddHours(-1);

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtAdapter = await tenantRepository.CreateTransientRtEntityAsync<RtAdapter>();
            rtAdapter.RtId = adapterRtId;
            rtAdapter.Name = "happy-path-adapter";
            rtAdapter.CommunicationState = RtCommunicationStateEnum.Offline;
            rtAdapter.CommunicationStateTimestamp = pastTimestamp;
            await tenantRepository.InsertOneRtEntityAsync(session, rtAdapter);
            await session.CommitTransactionAsync();
        }

        await repository.SetAdapterCommunicationStateAsync(fixture.TestTenantId, adapterRtEntityId,
            RtCommunicationStateEnum.Online);

        var loaded = await repository.GetAdapterAsync(fixture.TestTenantId, adapterRtEntityId);
        loaded.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        loaded.CommunicationStateTimestamp.Should().BeAfter(pastTimestamp);
    }

    [Fact]
    public async Task SetPoolCommunicationStateAsync_OlderThanPersisted_DoesNotOverwrite()
    {
        // Same scenario for pools: a late-arriving Offline write from a previous controller
        // pod must not flip a pool that has already been re-registered as Online by the
        // replacement pod.
        var repository = fixture.GetService<ICommunicationRepository>();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        var poolRtId = OctoObjectId.GenerateNewId();
        var futureTimestamp = DateTime.UtcNow.AddHours(1);

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var rtPool = await tenantRepository.CreateTransientRtEntityAsync<RtPool>();
            rtPool.RtId = poolRtId;
            rtPool.Name = $"stale-write-pool-{Guid.NewGuid():N}";
            rtPool.CommunicationState = RtCommunicationStateEnum.Online;
            rtPool.CommunicationStateTimestamp = futureTimestamp;
            await tenantRepository.InsertOneRtEntityAsync(session, rtPool);
            await session.CommitTransactionAsync();
        }

        await repository.SetPoolCommunicationStateAsync(fixture.TestTenantId, poolRtId,
            RtCommunicationStateEnum.Offline);

        var pools = await repository.GetPoolsAsync(fixture.TestTenantId);
        var loaded = pools.Single(p => p.RtId == poolRtId);
        loaded.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
        loaded.CommunicationStateTimestamp.Should().BeCloseTo(futureTimestamp, TimeSpan.FromMilliseconds(1));
    }
}
