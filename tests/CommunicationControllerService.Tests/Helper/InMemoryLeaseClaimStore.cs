using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     AB#7476 — in-memory stand-in for the controller-owned claim collection: an atomic insert-if-absent on the
///     (tenant, claim id) key, like the unique primary key in MongoDB.
/// </summary>
internal sealed class InMemoryLeaseClaimStore : IExecutionLeaseClaimStore
{
    private readonly ConcurrentDictionary<string, (DateTime ClaimedAt, string LeaseId, string TenantId)> _claims =
        new(StringComparer.Ordinal);

    /// <summary>Makes the next <see cref="TryClaimAsync" /> throw (an unreachable store).</summary>
    public bool Unavailable { get; set; }

    /// <summary>
    ///     When true (the default), <see cref="HasLeaseClaimAsync" /> answers "yes" for every lease, so the many tests
    ///     that arrange a leased execution by hand need no claim. The single-use suites switch it off to exercise the
    ///     lease-bound requeue.
    /// </summary>
    public bool LenientLeaseBinding { get; set; } = true;

    public int Count => _claims.Count;

    public Task<LeaseClaimResult> TryClaimAsync(string tenantId, string claimId, string leaseId,
        OctoObjectId executionRtId, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("claim store unavailable");
        }

        var key = $"{tenantId}/{claimId}";
        return Task.FromResult(_claims.TryAdd(key, (DateTime.UtcNow, leaseId, tenantId))
            ? new LeaseClaimResult(LeaseClaimOutcome.Claimed)
            : new LeaseClaimResult(LeaseClaimOutcome.AlreadyClaimed, DateTime.UtcNow - _claims[key].ClaimedAt));
    }

    public Task<bool> HasLeaseClaimAsync(string tenantId, string leaseId, CancellationToken cancellationToken = default)
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("claim store unavailable");
        }

        return Task.FromResult(LenientLeaseBinding ||
                               _claims.Values.Any(c => c.TenantId == tenantId && c.LeaseId == leaseId));
    }

    public Task ReleaseAsync(string tenantId, string claimId, CancellationToken cancellationToken = default)
    {
        _claims.TryRemove($"{tenantId}/{claimId}", out _);
        return Task.CompletedTask;
    }

    /// <summary>Pretends the claims were taken <paramref name="age" /> ago (to leave the in-flight window).</summary>
    public void AgeAll(TimeSpan age)
    {
        foreach (var key in _claims.Keys)
        {
            var c = _claims[key];
            _claims[key] = (DateTime.UtcNow - age, c.LeaseId, c.TenantId);
        }
    }
}
