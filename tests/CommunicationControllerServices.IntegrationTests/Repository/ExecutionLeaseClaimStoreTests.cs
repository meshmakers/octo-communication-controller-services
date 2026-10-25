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
        var leasePrefix = NewClaimId();
        var execution = OctoObjectId.GenerateNewId();

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            Store.TryClaimAsync(fixture.TestTenantId, claimId, $"{leasePrefix}-{i}", execution, TimeSpan.FromDays(31),
                TestContext.Current.CancellationToken))));

        results.Count(r => r.Outcome == LeaseClaimOutcome.Claimed).Should().Be(1);
        results.Where(r => r.Outcome == LeaseClaimOutcome.AlreadyClaimed).Should().OnlyContain(r => r.Age != null);
        // the lease that won is the one recorded; the others are not
        var winner = Array.FindIndex(results, r => r.Outcome == LeaseClaimOutcome.Claimed);
        (await Store.HasLeaseClaimAsync(fixture.TestTenantId, $"{leasePrefix}-{winner}", TestContext.Current.CancellationToken))
            .Should().BeTrue();
        (await Store.HasLeaseClaimAsync(fixture.TestTenantId, $"{leasePrefix}-{(winner + 1) % 24}", TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AClaimIsPerTenant_AndAReleasedClaimCanBeTakenAgain()
    {
        var claimId = NewClaimId();
        var execution = OctoObjectId.GenerateNewId();
        var ct = TestContext.Current.CancellationToken;

        (await Store.TryClaimAsync("tenant-a", claimId, "lease-a", execution, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
        (await Store.TryClaimAsync("tenant-b", claimId, "lease-b", execution, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
        (await Store.TryClaimAsync("tenant-a", claimId, "lease-a", execution, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.AlreadyClaimed);

        await Store.ReleaseAsync("tenant-a", claimId, ct);
        (await Store.TryClaimAsync("tenant-a", claimId, "lease-a", execution, TimeSpan.FromDays(1), ct))
            .Outcome.Should().Be(LeaseClaimOutcome.Claimed);
    }

    [Fact]
    public async Task TheRecordCarriesItsRetention_OnTheServerClock_AndATtlIndexDeletesIt()
    {
        var claimId = NewClaimId();
        await Store.TryClaimAsync(fixture.TestTenantId, claimId, "lease-1", OctoObjectId.GenerateNewId(),
            TimeSpan.FromDays(31), TestContext.Current.CancellationToken);

        var collection = await Store.GetCollectionForTestsAsync();
        var document = await collection.Find(new BsonDocument("_id", ExecutionLeaseClaimStore.KeyOf(
            fixture.TestTenantId, claimId))).FirstAsync(TestContext.Current.CancellationToken);
        // times come from the server ($$NOW): claimed == probed on the first claim, expiry = claimed + retention
        document["expireAtUtc"].ToUniversalTime().Should().BeCloseTo(
            document["claimedAtUtc"].ToUniversalTime().AddDays(31), TimeSpan.FromSeconds(1));
        document["probedAtUtc"].ToUniversalTime().Should().Be(document["claimedAtUtc"].ToUniversalTime());
        document.Names.Should().BeEquivalentTo("_id", "tenantId", "leaseId", "executionRtId", "claimedAtUtc",
            "expireAtUtc", "probedAtUtc");

        // a later probe keeps claimedAt/expireAt and moves probedAt; the age is the server-side difference
        var again = await Store.TryClaimAsync(fixture.TestTenantId, claimId, "lease-2", OctoObjectId.GenerateNewId(),
            TimeSpan.FromDays(31), TestContext.Current.CancellationToken);
        again.Outcome.Should().Be(LeaseClaimOutcome.AlreadyClaimed);
        again.Age.Should().BeLessThan(TimeSpan.FromMinutes(1));
        var after = await collection.Find(new BsonDocument("_id", ExecutionLeaseClaimStore.KeyOf(
            fixture.TestTenantId, claimId))).FirstAsync(TestContext.Current.CancellationToken);
        after["claimedAtUtc"].Should().Be(document["claimedAtUtc"]);
        after["expireAtUtc"].Should().Be(document["expireAtUtc"]);
        after["leaseId"].AsString.Should().Be("lease-1", "the first claim's lease stays recorded");

        var indexCursor = await collection.Indexes.ListAsync(TestContext.Current.CancellationToken);
        var indexes = await indexCursor.ToListAsync(TestContext.Current.CancellationToken);
        indexes.Should().Contain(i => i["name"].AsString == "execution_lease_claims_lease");
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
        await Store.TryClaimAsync(fixture.TestTenantId, NewClaimId(), "lease-x", OctoObjectId.GenerateNewId(),
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

    /// <summary>
    ///     AB#7476 N1 — the requeue mark is bound to the lease AND the execution of the claim and happens once, also
    ///     across concurrent callers.
    /// </summary>
    [Fact]
    public async Task TheRequeueMark_NeedsTheRightLeaseAndExecution_AndHappensOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var claimId = NewClaimId();
        var execution = OctoObjectId.GenerateNewId();
        await Store.TryClaimAsync(fixture.TestTenantId, claimId, "lease-1", execution, TimeSpan.FromDays(1), ct);

        (await Store.TryMarkRequeuedAsync(fixture.TestTenantId, claimId, "lease-OTHER", execution, ct)).Should().BeFalse();
        (await Store.TryMarkRequeuedAsync(fixture.TestTenantId, claimId, "lease-1", OctoObjectId.GenerateNewId(), ct))
            .Should().BeFalse();
        (await Store.TryMarkRequeuedAsync(fixture.TestTenantId, "no-such-claim", "lease-1", execution, ct)).Should().BeFalse();

        var marks = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            Store.TryMarkRequeuedAsync(fixture.TestTenantId, claimId, "lease-1", execution, ct))));
        marks.Count(m => m).Should().Be(1);
        (await Store.TryMarkRequeuedAsync(fixture.TestTenantId, claimId, "lease-1", execution, ct)).Should().BeFalse();

        var collection = await Store.GetCollectionForTestsAsync();
        var document = await collection.Find(new BsonDocument("_id", ExecutionLeaseClaimStore.KeyOf(
            fixture.TestTenantId, claimId))).FirstAsync(ct);
        document.Contains("requeuedAtUtc").Should().BeTrue();
    }
}
