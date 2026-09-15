using System.Collections.Concurrent;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <inheritdoc cref="IAdapterPoolConnectionManager" />
internal class AdapterPoolConnectionManager : IAdapterPoolConnectionManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<string, PoolMemberConnection> _membersByConnection = new();

    // 🔴 One lock for the claim/release transitions, not a lock-free update. TryClaimMember has to
    // pick an idle member AND mark it busy as one step; a compare-and-swap per member would still
    // let two concurrent grants both observe the same member as idle before either writes. The
    // critical section is a dictionary scan over the members of one pool — single digit in practice,
    // bounded by MaxReplicas — and it is never held across an await.
    private readonly Lock _claimLock = new();

    public PoolMemberConnection RegisterMember(string connectionId, string memberId, string poolTenantId,
        string poolRtId, IReadOnlyList<NodeDescriptorDto>? nodeDescriptors = null,
        string? pipelineSchemaJson = null)
    {
        var member = new PoolMemberConnection(connectionId, memberId, poolTenantId, poolRtId,
            ActiveLease: null, IsDraining: false, LastSeenUtc: DateTime.UtcNow,
            // AB#4924: an empty list and "did not report any" are the same thing to every caller,
            // and null is the value the fallback path already understands.
            NodeDescriptors: nodeDescriptors is { Count: > 0 } ? nodeDescriptors : null,
            PipelineSchemaJson: string.IsNullOrWhiteSpace(pipelineSchemaJson) ? null : pipelineSchemaJson);

        lock (_claimLock)
        {
            _membersByConnection[connectionId] = member;
        }

        Logger.Info(
            "Pool member '{MemberId}' registered on connection '{ConnectionId}' for pool {PoolRtId} " +
            "of tenant '{PoolTenantId}' with {NodeCount} node descriptor(s) and {SchemaState} pipeline " +
            "schema; {Count} member(s) now registered on this controller",
            memberId, connectionId, poolRtId, poolTenantId, member.NodeDescriptors?.Count ?? 0,
            member.PipelineSchemaJson == null ? "no" : "a", _membersByConnection.Count);

        return member;
    }

    public PoolMemberCapabilities? TryGetPoolCapabilities(string poolTenantId, string poolRtId)
    {
        // Deterministic and draining-last; see the interface remarks for why neither half is
        // cosmetic. No lock: a stale read here can only pick a member that just disconnected, and
        // its descriptors describe the same workload as its replacement's.
        var member = _membersByConnection.Values
            .Where(m => Matches(m, poolTenantId, poolRtId) && m.NodeDescriptors is { Count: > 0 })
            .OrderBy(m => m.IsDraining)
            .ThenBy(m => m.MemberId, StringComparer.Ordinal)
            .FirstOrDefault();

        return member is null
            ? null
            : new PoolMemberCapabilities(member.MemberId, member.NodeDescriptors!, member.PipelineSchemaJson);
    }

    public PoolMemberConnection? RemoveMember(string connectionId)
    {
        lock (_claimLock)
        {
            if (!_membersByConnection.TryRemove(connectionId, out var member))
            {
                return null;
            }

            Logger.Info(
                "Pool member '{MemberId}' removed (connection '{ConnectionId}'), held lease: {LeaseId}",
                member.MemberId, connectionId, member.ActiveLease?.LeaseId ?? "<none>");
            return member;
        }
    }

    public PoolMemberConnection? TryGetMember(string connectionId)
    {
        return _membersByConnection.GetValueOrDefault(connectionId);
    }

    public IReadOnlyCollection<PoolMemberConnection> GetMembers(string poolTenantId, string poolRtId)
    {
        return _membersByConnection.Values
            .Where(m => Matches(m, poolTenantId, poolRtId))
            .ToList();
    }

    public IReadOnlyCollection<PoolMemberConnection> GetAllMembers()
    {
        return _membersByConnection.Values.ToList();
    }

    public PoolMemberConnection? TryClaimMember(string poolTenantId, string poolRtId, LeaseDto lease)
    {
        lock (_claimLock)
        {
            // Least-recently-seen first, so work spreads over the members instead of always landing
            // on the first one the dictionary happens to enumerate. Not a scheduling policy — that is
            // increment 7 and it lives one level up, across tenants — just a tie-break that keeps a
            // single hot member from being the only one that ever warms up.
            var candidate = _membersByConnection.Values
                .Where(m => Matches(m, poolTenantId, poolRtId) && m.IsAvailable)
                .OrderBy(m => m.LastSeenUtc)
                .FirstOrDefault();

            if (candidate is null)
            {
                return null;
            }

            var claimed = candidate with { ActiveLease = lease };
            _membersByConnection[candidate.ConnectionId] = claimed;
            return claimed;
        }
    }

    public LeaseDto? ReleaseLease(string connectionId, string leaseId)
    {
        lock (_claimLock)
        {
            if (!_membersByConnection.TryGetValue(connectionId, out var member))
            {
                return null;
            }

            if (member.ActiveLease is null)
            {
                return null;
            }

            // 🔴 A release naming a different lease is stale — a message from a lease the controller
            // already expired, arriving after the member was handed a new one. Honouring it would
            // free a lease that is genuinely in flight and let a second borrower onto the same
            // process while the first one is still running.
            if (!string.Equals(member.ActiveLease.LeaseId, leaseId, StringComparison.Ordinal))
            {
                Logger.Warn(
                    "Ignoring a stale release of lease '{ReleasedLeaseId}' from member '{MemberId}': " +
                    "it currently holds lease '{HeldLeaseId}'",
                    leaseId, member.MemberId, member.ActiveLease.LeaseId);
                return null;
            }

            var released = member.ActiveLease;
            _membersByConnection[connectionId] = member with
            {
                ActiveLease = null, LastSeenUtc = DateTime.UtcNow
            };
            return released;
        }
    }

    public void MarkDraining(string connectionId)
    {
        lock (_claimLock)
        {
            if (_membersByConnection.TryGetValue(connectionId, out var member))
            {
                _membersByConnection[connectionId] = member with { IsDraining = true };
            }
        }
    }

    public void Heartbeat(string connectionId, DateTime sampledAtUtc)
    {
        lock (_claimLock)
        {
            if (_membersByConnection.TryGetValue(connectionId, out var member))
            {
                _membersByConnection[connectionId] = member with { LastSeenUtc = sampledAtUtc };
            }
        }
    }

    private static bool Matches(PoolMemberConnection member, string poolTenantId, string poolRtId)
    {
        return string.Equals(member.PoolTenantId, poolTenantId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(member.PoolRtId, poolRtId, StringComparison.OrdinalIgnoreCase);
    }
}
