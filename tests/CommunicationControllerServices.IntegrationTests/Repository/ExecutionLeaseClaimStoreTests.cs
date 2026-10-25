using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#7476 — the controller-owned single-use claim collection against real MongoDB: atomic across callers, TTL
///     retention, and not part of any tenant (runtime) database.
/// </summary>
[Collection("CommunicationController")]
public class ExecutionLeaseClaimStoreTests(CommunicationControllerFixture fixture)
{
    private ExecutionLeaseClaimStore Store => (ExecutionLeaseClaimStore)fixture.GetService<IExecutionLeaseClaimStore>();

    private static string NewClaimId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TheSameClaimIsTakenOnce_ByAnyNumberOfConcurrentCallers()
    {
        var claimId = NewClaimId();
        var execution = OctoObjectId.GenerateNewId();

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            Store.TryClaimAsync(fixture.TestTenantId, claimId, execution, DateTime.UtcNow, TimeSpan.FromDays(31),
                TestContext.Current.CancellationToken))));

        results.Count(r => r.Outcome == LeaseClaimOutcome.Claimed).Should().Be(1);
        results.Where(r => r.Outcome == LeaseClaimOutcome.AlreadyClaimed).Should().OnlyContain(r => r.ClaimedAtUtc != null);
    }

    [Fact]
    public async Task AClaimIsPerTenant_AndAReleasedClaimCanBeTakenAgain()
    {
        var claimId = NewClaimId();
        var execution = OctoObjectId.GenerateNewId();
        var ct = TestContext.Current.CancellationToken;

        (await Store.TryClaimAsync("tenant-a", claimId, execution, DateTime.UtcNow, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
        (await Store.TryClaimAsync("tenant-b", claimId, execution, DateTime.UtcNow, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
        (await Store.TryClaimAsync("tenant-a", claimId, execution, DateTime.UtcNow, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.AlreadyClaimed);

        await Store.ReleaseAsync("tenant-a", claimId, ct);
        (await Store.TryClaimAsync("tenant-a", claimId, execution, DateTime.UtcNow, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
    }

    [Fact]
    public async Task TheRecordCarriesItsRetention_AndATtlIndexDeletesIt()
    {
        var claimId = NewClaimId();
        var now = DateTime.UtcNow;
        await Store.TryClaimAsync(fixture.TestTenantId, claimId, OctoObjectId.GenerateNewId(), now,
            TimeSpan.FromDays(31), TestContext.Current.CancellationToken);

        var collection = await Store.GetCollectionForTestsAsync();
        var document = await collection.Find(new BsonDocument("_id", ExecutionLeaseClaimStore.KeyOf(
            fixture.TestTenantId, claimId))).FirstAsync(TestContext.Current.CancellationToken);
        document["expireAtUtc"].ToUniversalTime().Should().BeCloseTo(now.AddDays(31), TimeSpan.FromSeconds(1));
        // the identity is not stored as a field of its own besides the key; only audit data is
        document.Names.Should().BeEquivalentTo("_id", "tenantId", "executionRtId", "claimedAtUtc", "expireAtUtc");

        var indexCursor = await collection.Indexes.ListAsync(TestContext.Current.CancellationToken);
        var indexes = await indexCursor.ToListAsync(TestContext.Current.CancellationToken);
        var ttl = indexes.Single(i => i["name"].AsString == "execution_lease_claims_expire_ttl");
        ttl["key"].AsBsonDocument.Names.Should().Equal("expireAtUtc");
        ttl["expireAfterSeconds"].ToInt32().Should().Be(0);
    }

    /// <summary>
    ///     The claim records are no CK entities and live in the system database, so neither the runtime API / GraphQL /
    ///     MCP generic tools of a tenant nor the per-tenant database user of a pool member can reach them.
    /// </summary>
    [Fact]
    public async Task TheCollectionLivesInTheSystemDatabase_AndNotInTheTenantDatabase()
    {
        await Store.TryClaimAsync(fixture.TestTenantId, NewClaimId(), OctoObjectId.GenerateNewId(), DateTime.UtcNow,
            TimeSpan.FromDays(1), TestContext.Current.CancellationToken);
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(fixture.TestTenantId);
        var admin = new AdminMongoRepositoryClient(
            fixture.GetService<ILoggerFactory>().CreateLogger<AdminMongoRepositoryClient>(),
            fixture.GetService<IOptions<OctoSystemConfiguration>>(), fixture.Provider!, systemContext.DatabaseName);

        (await admin.ListCollectionNamesAsync(systemContext.DatabaseName)).Should()
            .Contain(ExecutionLeaseClaimStore.CollectionName);
        (await admin.ListCollectionNamesAsync(tenantContext.DatabaseName)).Should()
            .NotContain(ExecutionLeaseClaimStore.CollectionName);
    }
}
