using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#5349 — re-linking a leased adapter that AB#5271 left without a <c>LentFrom</c> edge, against
///     real MongoDB.
/// </summary>
/// <remarks>
///     <para>
///         🔴 This is the half a mock cannot show, and it is the whole crux of the fix. AB#5271 removed
///         <c>Adapter.LentFromTenantId</c> / <c>.LentFromAdapterPoolRtId</c> from the CK model, and the
///         removal rode the engine's post-chain schema-only bridge, which upgrades the schema and does
///         <b>not</b> delete attribute values. So the values are still in the documents of every tenant
///         carried over from 4.0.0 — but there is no generated property for them any more, and a
///         substituted repository can only ever return whatever a test put on an in-memory entity. What
///         has to be proven on a real database is that the values survive a write/read round trip
///         through a model that does not declare them, and that they come back under the keys the code
///         asks for.
///     </para>
///     <para>
///         🔴 <b>The stored key is <c>lentFromPoolRtId</c>, not <c>lentFromAdapterPoolRtId</c></b> — the
///         <c>Pool</c> → <c>AdapterPool</c> rename happened later in the same 4.x line and the data
///         carries the older spelling. Verified in MongoDB on the local kind cluster
///         (tenant <c>salzburgdev</c>, adapter <c>49240000000000000000bb01</c>, whose
///         <c>lentFrom.totalCount</c> was 0 while both attribute values were intact), and pinned here
///         by reading the raw document rather than by trusting the accessor that wrote it.
///     </para>
///     <para>
///         The sweep is tenant-wide, so these tests lend the shared test tenant a pool of its own. That
///         is not a shape the resolver would ever permit — it is the resolver that decides who may
///         borrow, and it is substituted here; the repository neither knows nor cares whether lender and
///         borrower are the same tenant, which is exactly what makes it usable as a fixture.
///     </para>
/// </remarks>
[Collection("CommunicationController")]
public class LentAdapterPoolRelinkTests(CommunicationControllerFixture fixture)
{
    /// <summary>
    ///     The round trip on its own: a leased adapter whose only record of what it borrowed is two
    ///     attribute values the CK model no longer declares.
    /// </summary>
    [Fact]
    public async Task LeftoverAttributeValues_SurviveAModelThatNoLongerDeclaresThem()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var poolRtId = OctoObjectId.GenerateNewId().ToString();
            var adapter = await CreateLeasedAdapterWithLeftoversAsync(data, fixture.TestTenantId, poolRtId);

            // 🔴 Read back through the ordinary repository call the sweep uses. Nothing declares these
            // attributes, so the only reason they are here is that the engine's attribute dictionary is
            // model-agnostic on both write and read.
            var workloads = await repository.GetWorkloadsAsync(fixture.TestTenantId);
            var loaded = workloads.OfType<RtAdapter>().Single(a => a.RtId == adapter.RtId);

            loaded.LifecycleMode.Should().Be(RtLifecycleModeEnum.Leased);
            loaded.GetAttributeValueOrDefault("LentFromTenantId").Should().Be(fixture.TestTenantId);
            loaded.GetAttributeValueOrDefault("LentFromPoolRtId").Should().Be(poolRtId);

            // And the stored spelling, read off the document itself: camelCase, and `lentFromPoolRtId`
            // rather than the current `AdapterPool` name. Asking for the wrong one finds nothing and the
            // sweep would then silently never fire.
            var stored = await ReadStoredAttributesAsync(adapter.RtId);
            stored.Should().NotBeNull();
            stored!.Contains("lentFromTenantId").Should().BeTrue();
            stored.Contains("lentFromPoolRtId").Should().BeTrue();
            stored.Contains("lentFromAdapterPoolRtId").Should().BeFalse();
            stored["lentFromPoolRtId"].AsString.Should().Be(poolRtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The fix: one reconcile turns an adapter carrying only the leftovers into an adapter with a
    ///     <c>LentFrom</c> edge — and a second reconcile changes nothing.
    /// </summary>
    [Fact]
    public async Task AnAdapterCarryingOnlyTheLeftovers_GetsItsLentFromEdge_AndTheSecondRunIsANoOp()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var pool = await CreateAdapterPoolAsync(data);
            var adapter = await CreateLeasedAdapterWithLeftoversAsync(data, fixture.TestTenantId,
                pool.RtId.ToString());

            // Nothing points anywhere yet — the measured starting state (`lentFrom.totalCount = 0`).
            (await repository.GetLentAdapterPoolForAdapterAsync(fixture.TestTenantId, adapter.RtId))
                .Should().BeNull();

            var service = CreateService();

            var first = await service.ProvisionForBorrowerAsync(fixture.TestTenantId,
                TestContext.Current.CancellationToken);
            first.AdaptersRelinked.Should().Be(1);
            first.IsNoOp.Should().BeFalse();

            var mirror = await repository.GetLentAdapterPoolForAdapterAsync(fixture.TestTenantId, adapter.RtId);
            mirror.Should().NotBeNull();
            mirror!.Lender!.LenderTenantId.Should().Be(fixture.TestTenantId);
            mirror.Lender.LenderAdapterPoolRtId.Should().Be(pool.RtId.ToString());
            data.Mirrors.Add(new RtEntityId(SystemCommunicationCkIds.RtCkLentAdapterPoolTypeId, mirror.RtId));

            // 🔴 The leftovers are not deleted. They are harmless dead data and the only remaining
            // evidence of what the adapter borrowed.
            var afterRelink = (await repository.GetWorkloadsAsync(fixture.TestTenantId))
                .OfType<RtAdapter>().Single(a => a.RtId == adapter.RtId);
            afterRelink.GetAttributeValueOrDefault("LentFromTenantId").Should().Be(fixture.TestTenantId);
            afterRelink.GetAttributeValueOrDefault("LentFromPoolRtId").Should().Be(pool.RtId.ToString());

            // Idempotent: nothing to re-link, nothing to write, and the edge still points at the same
            // mirror rather than at a second one.
            var second = await service.ProvisionForBorrowerAsync(fixture.TestTenantId,
                TestContext.Current.CancellationToken);
            second.AdaptersRelinked.Should().Be(0);

            var mirrorAfterSecondRun =
                await repository.GetLentAdapterPoolForAdapterAsync(fixture.TestTenantId, adapter.RtId);
            mirrorAfterSecondRun!.RtId.Should().Be(mirror.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 Leftovers that match no mirror change nothing. A leased adapter pointing at a pool nobody
    ///     lent it is worse than one pointing at nothing — the latter is already a named refusal at the
    ///     enqueue (AB#5329).
    /// </summary>
    [Fact]
    public async Task LeftoversNamingAPoolThatDoesNotLendHere_LeaveTheAdapterUnlinked()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            // A real, lending pool exists — so the tenant does hold a mirror and the sweep really runs —
            // but the adapter's leftovers name a different pool.
            var pool = await CreateAdapterPoolAsync(data);
            var adapter = await CreateLeasedAdapterWithLeftoversAsync(data, fixture.TestTenantId,
                OctoObjectId.GenerateNewId().ToString());

            var service = CreateService();
            var result = await service.ProvisionForBorrowerAsync(fixture.TestTenantId,
                TestContext.Current.CancellationToken);

            result.AdaptersRelinked.Should().Be(0);
            (await repository.GetLentAdapterPoolForAdapterAsync(fixture.TestTenantId, adapter.RtId))
                .Should().BeNull();

            // The mirror of the pool that does lend here was still provisioned — this is not a run that
            // gave up, it is a run that declined one adapter.
            var mirrors = await repository.GetLentAdapterPoolMirrorsAsync(fixture.TestTenantId);
            var provisioned = mirrors.Single(m => m.Lender?.LenderAdapterPoolRtId == pool.RtId.ToString());
            data.Mirrors.Add(new RtEntityId(SystemCommunicationCkIds.RtCkLentAdapterPoolTypeId, provisioned.RtId));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The real service over the real repository, with only the lending <b>decision</b> substituted:
    ///     that decision belongs to <c>ITenantLendingScopeResolver</c>, which walks the tenant tree
    ///     through admin sessions this fixture has no second tenant for.
    /// </summary>
    private AdapterPoolMirrorProvisioningService CreateService()
    {
        var lendingScopeResolver = Substitute.For<ITenantLendingScopeResolver>();
        lendingScopeResolver
            .ResolveCandidateLenderTenantsAsync(fixture.TestTenantId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)new[] { fixture.TestTenantId });
        lendingScopeResolver
            .MayLendAsync(fixture.TestTenantId, fixture.TestTenantId, Arg.Any<LendingScope>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        return new AdapterPoolMirrorProvisioningService(
            NullLogger<AdapterPoolMirrorProvisioningService>.Instance,
            fixture.GetService<ICommunicationRepository>(),
            lendingScopeResolver,
            Substitute.For<ICommunicationEventService>());
    }

    /// <summary>
    ///     A leased adapter with no <c>LentFrom</c> edge and the two orphaned attribute values.
    /// </summary>
    /// <remarks>
    ///     Written with <c>SetAttributeRawValue</c> under the engine's PascalCase dictionary keys — there
    ///     is no generated property to assign, and the MongoDB attribute serializer camel-cases on the
    ///     way out, producing exactly the document shape measured on the carried-over tenant.
    /// </remarks>
    private async Task<RtAdapter> CreateLeasedAdapterWithLeftoversAsync(TestData data, string lenderTenantId,
        string lenderPoolRtId)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = $"int-test-relink-borrower-{Guid.NewGuid():N}",
            CommunicationState = RtCommunicationStateEnum.Unregistered,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured,
            LifecycleMode = RtLifecycleModeEnum.Leased
        };

        adapter.SetAttributeRawValue("LentFromTenantId", lenderTenantId);
        adapter.SetAttributeRawValue("LentFromPoolRtId", lenderPoolRtId);

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapter>> { EntityUpdateInfo<RtAdapter>.CreateInsert(adapter) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert test adapter: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
        data.Adapters.Add(adapter.ToRtEntityId());
        return adapter;
    }

    private async Task<RtAdapterPool> CreateAdapterPoolAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var pool = new RtAdapterPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = $"int-test-relink-pool-{Guid.NewGuid():N}",
            DeploymentState = RtDeploymentStateEnum.Deployed,
            MinReplicas = 1,
            MaxReplicas = 3,
            ScaleUpPolicy = RtPoolScaleUpPolicyEnum.QueueDepthOrWaitSeconds,
            ScaleUpQueueDepthThreshold = 0,
            ScaleUpQueueWaitSeconds = 0,
            SharingMode = RtAdapterSharingModeEnum.Descendants
        };

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapterPool>> { EntityUpdateInfo<RtAdapterPool>.CreateInsert(pool) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert test adapter pool: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
        data.AdapterPools.Add(pool.ToRtEntityId());
        return pool;
    }

    /// <summary>
    ///     The <c>attributes</c> sub-document of one entity, read with the raw driver.
    /// </summary>
    /// <remarks>
    ///     The collection is located by searching for the id rather than hardcoded: adapters live in the
    ///     <b>polymorphic</b> collection of their base type (there is no
    ///     <c>RtEntity_SystemCommunicationAdapter</c>), and the point of this read is the stored key
    ///     spelling, not the collection name.
    /// </remarks>
    private async Task<BsonDocument?> ReadStoredAttributesAsync(OctoObjectId rtId)
    {
        var client = new MongoClient(fixture.GetConnectionString());
        var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(rtId.ToString()));

        using var databases = await client.ListDatabaseNamesAsync();
        foreach (var databaseName in await databases.ToListAsync())
        {
            if (databaseName is "admin" or "local" or "config")
            {
                continue;
            }

            var database = client.GetDatabase(databaseName);
            using var collections = await database.ListCollectionNamesAsync();
            foreach (var collectionName in await collections.ToListAsync())
            {
                if (!collectionName.StartsWith("RtEntity_", StringComparison.Ordinal))
                {
                    continue;
                }

                var document = await database.GetCollection<BsonDocument>(collectionName)
                    .Find(filter).FirstOrDefaultAsync();
                if (document is not null && document.Contains("attributes"))
                {
                    return document["attributes"].AsBsonDocument;
                }
            }
        }

        return null;
    }

    private async Task CleanupAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        foreach (var adapter in data.Adapters)
        {
            await TryDeleteAsync<RtAdapter>(tenantRepository, adapter);
        }

        foreach (var mirror in data.Mirrors)
        {
            await TryDeleteAsync<RtLentAdapterPool>(tenantRepository, mirror);
        }

        foreach (var pool in data.AdapterPools)
        {
            await TryDeleteAsync<RtAdapterPool>(tenantRepository, pool);
        }
    }

    private static async Task TryDeleteAsync<TEntity>(ITenantRepository tenantRepository, RtEntityId entity)
        where TEntity : RtEntity
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<TEntity>> { EntityUpdateInfo<TEntity>.CreateDelete(entity) },
                operationResult);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
        }
    }

    private sealed class TestData
    {
        public List<RtEntityId> Adapters { get; } = new();
        public List<RtEntityId> AdapterPools { get; } = new();
        public List<RtEntityId> Mirrors { get; } = new();
    }
}
