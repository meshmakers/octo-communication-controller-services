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
///         <b>Atomic.</b> <c>_id = "{tenantId}/{claimId}"</c>; the claim is a plain <c>InsertOne</c>. A duplicate key
///         error is the answer "already claimed" (any instance, any time); no read-before-write.
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

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISystemContext _systemContext;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IMongoCollection<BsonDocument>? _collection;
    private volatile bool _indexEnsured;
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

    public async Task<LeaseClaimResult> TryClaimAsync(string tenantId, string claimId, OctoObjectId executionRtId,
        DateTime nowUtc, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimId);

        var collection = await GetCollectionAsync(cancellationToken).ConfigureAwait(false);
        var key = KeyOf(tenantId, claimId);
        var document = new BsonDocument
        {
            { "_id", key },
            { "tenantId", tenantId },
            { "executionRtId", executionRtId.ToString() },
            { "claimedAtUtc", new BsonDateTime(nowUtc) },
            { "expireAtUtc", new BsonDateTime(nowUtc + retention) }
        };

        try
        {
            await collection.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LeaseClaimResult(LeaseClaimOutcome.Claimed);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await collection.Find(new BsonDocument("_id", key))
                .Project(new BsonDocument { { "claimedAtUtc", 1 } })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            // The record can expire between the insert and the read: then the claim is gone, which for the TTL rule
            // means the seal is older than the accepted age anyway; report "claimed long ago".
            var claimedAt = existing is not null && existing.TryGetValue("claimedAtUtc", out var value) &&
                            value.IsBsonDateTime
                ? value.ToUniversalTime()
                : DateTime.MinValue;
            return new LeaseClaimResult(LeaseClaimOutcome.AlreadyClaimed, claimedAt);
        }
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
        if (_collection is { } ready && _indexEnsured)
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

            if (!_indexEnsured)
            {
                // The TTL index is retention hygiene, not correctness: the unique key is the primary key. A failure
                // to create it must not stop leases; it is retried on the next call.
                try
                {
                    await _collection.Indexes.CreateOneAsync(
                        new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expireAtUtc"),
                            new CreateIndexOptions { Name = TtlIndexName, ExpireAfter = TimeSpan.Zero }),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    _indexEnsured = true;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Logger.Warn(e,
                        "Could not ensure the TTL index of '{Collection}'; claim records are not cleaned up until it exists",
                        CollectionName);
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
