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
    private readonly ConcurrentDictionary<string, DateTime> _claims = new(StringComparer.Ordinal);

    /// <summary>Makes the next <see cref="TryClaimAsync" /> throw (an unreachable store).</summary>
    public bool Unavailable { get; set; }

    public int Count => _claims.Count;

    public Task<LeaseClaimResult> TryClaimAsync(string tenantId, string claimId, OctoObjectId executionRtId,
        DateTime nowUtc, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (Unavailable)
        {
            throw new InvalidOperationException("claim store unavailable");
        }

        var key = $"{tenantId}/{claimId}";
        return Task.FromResult(_claims.TryAdd(key, nowUtc)
            ? new LeaseClaimResult(LeaseClaimOutcome.Claimed)
            : new LeaseClaimResult(LeaseClaimOutcome.AlreadyClaimed, _claims[key]));
    }

    public Task ReleaseAsync(string tenantId, string claimId, CancellationToken cancellationToken = default)
    {
        _claims.TryRemove($"{tenantId}/{claimId}", out _);
        return Task.CompletedTask;
    }

    /// <summary>Pretends the claim was taken <paramref name="age" /> ago (to leave the in-flight window).</summary>
    public void AgeAll(TimeSpan age)
    {
        foreach (var key in _claims.Keys)
        {
            _claims[key] = DateTime.UtcNow - age;
        }
    }
}
