using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     AB#7476 — MongoDB implementation of <see cref="IExecutionLeaseClaimStore" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Where.</b> Collection <see cref="CollectionName" /> in the SYSTEM database, written with the controller's
///         own admin client (<see cref="AdminMongoRepositoryClient" />, the same connection rules as the engine). It is
///         not a CK entity and not in a tenant database: the runtime API / GraphQL / MCP generic tools only reach CK
///         collections of tenant databases, and the per-tenant database user that a pool member receives with a lease
///         has no access to the system database.
///     </para>
///     <para>
///         <b>Atomic.</b> <c>_id = "{tenantId}/{claimId}"</c>; the claim is one upsert whose fields all keep an existing
///         value (insert-if-absent). The first caller inserts (<c>UpsertedId</c>), every other one finds the record;
///         times are the server's <c>$$NOW</c>, so replicas' clocks play no role.
///     </para>
///     <para>
///         <b>Retention.</b> Every record carries <c>expireAtUtc</c> and a TTL index (<c>expireAfterSeconds: 0</c>)
///         deletes it. The retention must outlive the maximum age a sealed execution is accepted at (the lease service
///         refuses older seals), see <c>CommunicationControllerOptions.QueuedSealMaxAgeDays</c>; otherwise an expired
///         record would make an old seal usable again.
///     </para>
/// </remarks>
internal sealed class ExecutionLeaseClaimStore : IExecutionLeaseClaimStore, IDisposable
{
    internal const string CollectionName = "execution_lease_claims";

    private const string TtlIndexName = "execution_lease_claims_expire_ttl";
    private const string LeaseIndexName = "execution_lease_claims_lease";
    private static readonly TimeSpan IndexRetryBackOff = TimeSpan.FromMinutes(1);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISystemContext _systemContext;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IMongoCollection<BsonDocument>? _collection;
    private volatile bool _indexEnsured;
    private DateTime _nextIndexAttemptUtc = DateTime.MinValue;
    private AdminMongoRepositoryClient? _client;

    public ExecutionLeaseClaimStore(ISystemContext systemContext, IOptions<OctoSystemConfiguration> systemConfiguration,
        IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _systemContext = systemContext;
        _systemConfiguration = systemConfiguration;
        _serviceProvider = serviceProvider;
    }

    internal static string KeyOf(string tenantId, string claimId) => $"{tenantId}/{claimId}";

    public async Task<LeaseClaimResult> TryClaimAsync(string tenantId, string claimId, string leaseId,
        OctoObjectId executionRtId, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var key = KeyOf(tenantId, claimId);

        // 🔴 All times are the MongoDB server's ($$NOW), so controller replicas with different clocks agree on the age
        // of a claim. An upsert whose every field is "keep what is there, else set": the first caller inserts the
        // record (insert-if-absent), every later caller leaves it untouched except for `probedAtUtc`, the server time of
        // its own probe, which is what the age is measured against. The upsert on _id is atomic; a concurrent first
        // insert that loses the race surfaces as a duplicate key and is treated as "already claimed".
        var stage = new BsonDocument("$set", new BsonDocument
        {
            { "tenantId", IfNull("tenantId", tenantId) },
            { "leaseId", IfNull("leaseId", leaseId) },
            { "executionRtId", IfNull("executionRtId", executionRtId.ToString()) },
            { "claimedAtUtc", IfNull("claimedAtUtc", "$$NOW") },
            {
                "expireAtUtc", new BsonDocument("$ifNull", new BsonArray
                {
                    "$expireAtUtc",
                    new BsonDocument("$add", new BsonArray { "$$NOW", (long)retention.TotalMilliseconds })
                })
            },
            { "probedAtUtc", "$$NOW" }
        });

        bool inserted;
        try
        {
            var result = await collection.UpdateOneAsync(new BsonDocument("_id", key),
                Builders<BsonDocument>.Update.Pipeline(new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>(
                    [stage])),
                new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
            inserted = result.UpsertedId is not null;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            inserted = false;
        }

        if (inserted)
        {
            return new LeaseClaimResult(LeaseClaimOutcome.Claimed);
        }

        var existing = await collection.Find(new BsonDocument("_id", key))
            .Project(new BsonDocument { { "claimedAtUtc", 1 }, { "probedAtUtc", 1 } })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        // The record can expire between the upsert and the read: it is then older than any accepted seal.
        var age = existing is not null && existing.TryGetValue("claimedAtUtc", out var claimedAt) &&
                  claimedAt.IsBsonDateTime && existing.TryGetValue("probedAtUtc", out var probedAt) &&
                  probedAt.IsBsonDateTime
            ? probedAt.ToUniversalTime() - claimedAt.ToUniversalTime()
            : TimeSpan.MaxValue;
        return new LeaseClaimResult(LeaseClaimOutcome.AlreadyClaimed, age);
    }

    private static BsonDocument IfNull(string field, BsonValue value) =>
        new("$ifNull", new BsonArray { "$" + field, value });

    public async Task<bool> TryMarkRequeuedAsync(string tenantId, string claimId, string leaseId,
        OctoObjectId executionRtId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var filter = new BsonDocument
        {
            { "_id", KeyOf(tenantId, claimId) },
            { "leaseId", leaseId },
            { "executionRtId", executionRtId.ToString() },
            { "requeuedAtUtc", new BsonDocument("$exists", false) }
        };
        var stage = new BsonDocument("$set", new BsonDocument("requeuedAtUtc", "$$NOW"));
        var matched = await collection.FindOneAndUpdateAsync(filter,
            Builders<BsonDocument>.Update.Pipeline(new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>(
                [stage])),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = false }, cancellationToken).ConfigureAwait(false);
        return matched is not null;
    }

    public async Task<bool> HasLeaseClaimAsync(string tenantId, string leaseId,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        return await collection.Find(new BsonDocument { { "tenantId", tenantId }, { "leaseId", leaseId } })
            .Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string tenantId, string claimId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        await collection.DeleteOneAsync(new BsonDocument("_id", KeyOf(tenantId, claimId)), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The collection itself, for the integration tests (location, TTL index, ageing a record).</summary>
    internal Task<IMongoCollection<BsonDocument>> GetCollectionForTestsAsync() => GetCollectionAsync(CancellationToken.None);

    private async Task<IMongoCollection<BsonDocument>> GetCollectionAsync(CancellationToken cancellationToken)
    {
        if (_collection is { } ready && (_indexEnsured || DateTime.UtcNow < _nextIndexAttemptUtc))
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_collection is null)
            {
                var databaseName = _systemContext.DatabaseName;
                _client = new AdminMongoRepositoryClient(_loggerFactory.CreateLogger<AdminMongoRepositoryClient>(),
                    _systemConfiguration, _serviceProvider, databaseName);
                var repository = (MongoRepository)_client.GetRepository(databaseName);
                _collection = repository.Database.GetCollection<BsonDocument>(CollectionName);
            }

            if (!_indexEnsured && DateTime.UtcNow >= _nextIndexAttemptUtc)
            {
                // The indexes are retention hygiene and a lookup aid, not correctness: the unique key is the primary
                // key. A failure to create them must not stop leases; it is retried with a back-off, not on every call.
                try
                {
                    await _collection.Indexes.CreateManyAsync(
                    [
                        new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expireAtUtc"),
                            new CreateIndexOptions { Name = TtlIndexName, ExpireAfter = TimeSpan.Zero }),
                        new CreateIndexModel<BsonDocument>(
                            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("leaseId"),
                            new CreateIndexOptions { Name = LeaseIndexName })
                    ], cancellationToken).ConfigureAwait(false);
                    _indexEnsured = true;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _nextIndexAttemptUtc = DateTime.UtcNow + IndexRetryBackOff;
                    Logger.Warn(e,
                        "Could not ensure the indexes of '{Collection}'; claim records are not cleaned up until they " +
                        "exist. Next attempt after {BackOff}",
                        CollectionName, IndexRetryBackOff);
                }
            }

            return _collection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _client?.Dispose();
    }
}
