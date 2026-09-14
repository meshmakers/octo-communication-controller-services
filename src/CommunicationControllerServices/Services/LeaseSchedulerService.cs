using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ILeaseSchedulerService" />
internal class LeaseSchedulerService : ILeaseSchedulerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IAdapterCache _adapterCache;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IAdapterPoolConnectionManager _connectionManager;
    private readonly ICommunicationEventService _eventService;
    private readonly ILeaseService _leaseService;
    private readonly CommunicationControllerOptions _options;
    private readonly IPoolService _poolService;

    /// <summary>
    ///     Which borrower tenants each pool serves. Rebuilt on a slower cadence than the scheduling
    ///     round: it is a read of every enabled tenant's workloads, and doing that every few seconds
    ///     would make the scheduler the most expensive thing in the controller.
    /// </summary>
    private readonly SemaphoreSlim _topologyLock = new(1, 1);

    private IReadOnlyDictionary<PoolKey, IReadOnlyList<PoolBorrower>> _topology =
        new Dictionary<PoolKey, IReadOnlyList<PoolBorrower>>();

    private DateTime _topologyBuiltAtUtc = DateTime.MinValue;

    /// <summary>
    ///     Where each pool's rotation stands: the tenant served last. The next round starts with the
    ///     tenant <b>after</b> it, which is what makes turns actually rotate instead of always
    ///     beginning at the alphabetically first borrower.
    /// </summary>
    private readonly ConcurrentDictionary<PoolKey, string> _rotationCursor = new();

    /// <summary>Queue-depth samples per pool, for the scale-up averaging window (§9.4).</summary>
    private readonly ConcurrentDictionary<PoolKey, List<DepthSample>> _depthSamples = new();

    /// <summary>When each pool last had a scale-up requested, so one signal does not fire a burst.</summary>
    private readonly ConcurrentDictionary<PoolKey, DateTime> _lastScaleUpUtc = new();

    /// <summary>
    ///     Pools currently observed with queued work and no idle member, so the condition is logged
    ///     once when it starts rather than on every five-second round while it lasts (AB#4924
    ///     increment 9).
    /// </summary>
    private readonly ConcurrentDictionary<PoolKey, DateTime> _exhaustedPools = new();

    public LeaseSchedulerService(IAdapterCache adapterCache,
        ICommunicationRepository communicationRepository,
        IAdapterPoolConnectionManager connectionManager,
        ICommunicationEventService eventService,
        ILeaseService leaseService,
        IPoolService poolService,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _communicationRepository = communicationRepository;
        _connectionManager = connectionManager;
        _eventService = eventService;
        _leaseService = leaseService;
        _poolService = poolService;
        _options = options.Value;
    }

    /// <inheritdoc />
    public void InvalidateTopology()
    {
        _topologyBuiltAtUtc = DateTime.MinValue;
    }

    /// <inheritdoc />
    public async Task<int> RunSchedulingRoundAsync(CancellationToken cancellationToken = default)
    {
        var topology = await GetTopologyAsync(cancellationToken);
        var granted = 0;

        foreach (var (key, borrowers) in topology)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                granted += await ScheduleForPoolAsync(key, borrowers, cancellationToken);
            }
            catch (Exception e)
            {
                // One unreachable borrower tenant must not stop every other pool from being served.
                Logger.Error(e, "Scheduling round failed for adapter pool {PoolRtId} of tenant '{LenderTenantId}'",
                    key.PoolRtId, key.LenderTenantId);
            }
        }

        // 🔴 A pool that left the topology must stop publishing rather than freeze at its last
        // reading. A depth gauge stuck at "12 queued" for a pool whose last borrower was re-pointed
        // is an alert that can never clear, and the first thing anyone does with an alert that never
        // clears is stop reading it. By age, not by "not in this round's topology" — several
        // controller pods sweep independently and each sees a different subset.
        AdapterLeasingMetrics.SweepStalePools();

        return granted;
    }

    /// <inheritdoc />
    public async Task<int> ReapExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var reaped = 0;

        foreach (var member in _connectionManager.GetAllMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lease = member.ActiveLease;
            if (lease is null || lease.ExpiresAtUtc > now)
            {
                continue;
            }

            Logger.Warn(
                "Lease '{LeaseId}' of tenant '{BorrowerTenantId}' on member '{MemberId}' expired at " +
                "{ExpiresAtUtc:O}; reclaiming it",
                lease.LeaseId, lease.TenantId, member.MemberId, lease.ExpiresAtUtc);

            var requeuedExecutionId = await _leaseService.InterruptAndRequeueAsync(lease,
                LeaseInterruptReason.TtlExpiry,
                $"The lease on pool member '{member.MemberId}' expired before the member released it.");

            // 🔴 Free the lease BEFORE draining, so the member is not left holding a lease that no
            // longer exists anywhere else. Concept §6: the member itself is drained and restarted
            // rather than re-used, because its post-lease cleanliness is unproven — it is the one
            // thing a TTL expiry genuinely tells us nothing about.
            _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
            await _leaseService.DrainMemberAsync(member.ConnectionId,
                $"its lease '{lease.LeaseId}' expired without a release");

            // Counted here rather than inside DrainMemberAsync, which only knows a connection id:
            // the drain counter is scoped to the POOL, because a drain loop is a property of the
            // pool and the member id changes on every restart the loop causes.
            AdapterLeasingMetrics.RecordMemberDrained(member.PoolTenantId, member.PoolRtId,
                LeaseDrainReason.TtlExpiry);

            await _eventService.StoreErrorEventAsync(lease.TenantId,
                $"The adapter pool lease serving execution '{lease.ExecutionId}' expired before the pool member " +
                $"released it" +
                (requeuedExecutionId is null
                    ? "."
                    : $"; the work was re-queued as execution '{requeuedExecutionId}'.") +
                " The member was drained and is replaced by a fresh process.");

            reaped++;
        }

        return reaped;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdapterPoolQueueEntry>> GetQueueAsync(string lenderTenantId,
        OctoObjectId poolRtId, CancellationToken cancellationToken = default)
    {
        var key = PoolKey.Create(lenderTenantId, poolRtId.ToString());
        var topology = await GetTopologyAsync(cancellationToken);
        topology.TryGetValue(key, out var borrowers);

        var entries = new List<AdapterPoolQueueEntry>();

        // Everything this instance currently has leased out of the pool, first: it is what the
        // waiting entries are waiting behind, and it is the only place LeasedOnMemberId comes from.
        foreach (var member in _connectionManager.GetMembers(lenderTenantId, poolRtId.ToString()))
        {
            var lease = member.ActiveLease;
            if (lease is null || string.IsNullOrWhiteSpace(lease.ExecutionId))
            {
                continue;
            }

            var view = await _communicationRepository.GetExecutionQueueEntryAsync(lease.TenantId, lease.ExecutionId);
            entries.Add(new AdapterPoolQueueEntry(
                lease.ExecutionId,
                lease.TenantId,
                view?.PipelineRtId?.ToString(),
                view?.PipelineName,
                view?.ExecutionClass ?? QueuedExecution.BatchClass,
                view?.QueuedAtUtc ?? lease.GrantedAtUtc,
                PositionInTenant: 0,
                TenantsAheadInRotation: 0,
                member.MemberId,
                lease.ExpiresAtUtc));
        }

        if (borrowers is not null)
        {
            var byTenant = await ReadQueueByTenantAsync(borrowers, cancellationToken);
            var rotation = BuildRotation(byTenant.Keys);
            var start = ResolveRotationStart(key, rotation);

            foreach (var (tenantId, items) in byTenant)
            {
                var tenantIndex = rotation.IndexOf(tenantId);
                var tenantsAhead = rotation.Count == 0
                    ? 0
                    : (tenantIndex - start + rotation.Count) % rotation.Count;

                for (var position = 0; position < items.Count; position++)
                {
                    var item = items[position].Queued;
                    entries.Add(new AdapterPoolQueueEntry(
                        item.ExecutionId,
                        tenantId,
                        item.PipelineRtId?.ToString(),
                        item.PipelineName,
                        item.ExecutionClass,
                        item.QueuedAtUtc,
                        position + 1,
                        tenantsAhead,
                        LeasedOnMemberId: null,
                        LeaseExpiresAtUtc: null));
                }
            }
        }

        return entries
            .OrderBy(e => e.LeasedOnMemberId is null ? 1 : 0)
            .ThenBy(e => e.TenantsAheadInRotation)
            .ThenBy(e => e.BorrowerTenantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.PositionInTenant)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<QueueCancellationResult> CancelQueuedExecutionAsync(string lenderTenantId,
        OctoObjectId poolRtId, string executionId, CancellationToken cancellationToken = default)
    {
        // A lease in flight decides the answer before any tenant database is touched: the entry is
        // not in a queue any more, and cancelling it means interrupting a running pipeline — a
        // different operation on a different path (concept §5, "Cancellation").
        foreach (var member in _connectionManager.GetMembers(lenderTenantId, poolRtId.ToString()))
        {
            if (string.Equals(member.ActiveLease?.ExecutionId, executionId, StringComparison.Ordinal))
            {
                return QueueCancellationResult.AlreadyLeased;
            }
        }

        var key = PoolKey.Create(lenderTenantId, poolRtId.ToString());
        var topology = await GetTopologyAsync(cancellationToken);
        if (!topology.TryGetValue(key, out var borrowers))
        {
            return QueueCancellationResult.NotFound;
        }

        foreach (var tenantId in borrowers.Select(b => b.TenantId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RtPipelineExecution? execution;
            try
            {
                execution = await _communicationRepository.GetPipelineExecutionAsync(tenantId, executionId);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "[{TenantId}] Could not read execution '{ExecutionId}' while cancelling a queue entry",
                    tenantId, executionId);
                continue;
            }

            if (execution is null)
            {
                continue;
            }

            if (execution.Status != RtPipelineExecutionStatusEnum.Queued)
            {
                return execution.Status == RtPipelineExecutionStatusEnum.Running
                    ? QueueCancellationResult.AlreadyLeased
                    : QueueCancellationResult.NotFound;
            }

            var cancelled = await _communicationRepository.TryCancelQueuedExecutionAsync(tenantId, executionId,
                "Cancelled from the adapter pool queue before a lease was granted.");
            if (!cancelled)
            {
                // It left the queue between the read and the write — a lease won the race.
                return QueueCancellationResult.AlreadyLeased;
            }

            await _eventService.StoreInformationEventAsync(tenantId,
                $"Queued pipeline execution '{executionId}' was cancelled before it was leased.");
            return QueueCancellationResult.Cancelled;
        }

        return QueueCancellationResult.NotFound;
    }

    /// <summary>
    ///     Serves one pool: read its queue, evaluate scale-up, then hand out what the members can take.
    /// </summary>
    private async Task<int> ScheduleForPoolAsync(PoolKey key, IReadOnlyList<PoolBorrower> borrowers,
        CancellationToken cancellationToken)
    {
        var byTenant = await ReadQueueByTenantAsync(borrowers, cancellationToken);
        var depth = byTenant.Values.Sum(items => items.Count);

        var poolRtId = new OctoObjectId(key.PoolRtId);
        var pool = await TryReadPoolAsync(key, poolRtId);

        var oldestWait = depth == 0
            ? TimeSpan.Zero
            : DateTime.UtcNow - byTenant.Values.SelectMany(i => i).Min(i => i.Queued.QueuedAtUtc);

        var members = _connectionManager.GetMembers(key.LenderTenantId, key.PoolRtId);

        // Named before anything can be counted for this pool, so every series carries something a
        // human can read from the first measurement rather than from the second round onwards.
        AdapterLeasingMetrics.NamePool(key.LenderTenantId, key.PoolRtId, pool?.Name);

        var scaleUp = await EvaluateScaleUpAsync(key, pool, depth, oldestWait, members.Count);

        // 🔴 AB#4924 increment 9 (plan §11). Everything continuous about this pool is published
        // here, once per round, as gauges — depth per borrowing tenant, the oldest wait, the member
        // states, the scale-up signals and the window that produced them. Deliberately NOT logged:
        // a scheduling round runs every 5 s per pool, and a log line per round per pool is exactly
        // the shape of the adapter log-level flood this estate has already lived through.
        var available = members.Count(m => m.IsAvailable);
        AdapterLeasingMetrics.ObserveRound(key.LenderTenantId, key.PoolRtId,
            byTenant.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase),
            oldestWait,
            // Mutually exclusive on purpose, so the three sum to the member count: a member that is
            // draining while it still holds a lease counts as leased, because that is what it is
            // doing — it stops taking work when the lease ends.
            (available,
                members.Count(m => m.ActiveLease is not null),
                members.Count(m => m is { IsDraining: true, ActiveLease: null })),
            scaleUp.Window, scaleUp.DepthSignal, scaleUp.WaitSignal, scaleUp.Undersized);

        if (depth == 0)
        {
            _exhaustedPools.TryRemove(key, out _);
            return 0;
        }

        if (available == 0)
        {
            // Concept §6, "Pool exhausted": the queue simply grows and stays visible. Work is never
            // dropped and never rejected because no member happens to be free.
            //
            // Counted per borrowing tenant, because "which tenant is not being served" is the
            // question this state raises and an aggregate cannot answer it.
            foreach (var tenantId in byTenant.Keys)
            {
                AdapterLeasingMetrics.RecordRefused(tenantId, key.LenderTenantId, key.PoolRtId,
                    LeaseStage.Schedule, LeaseRefusalReason.PoolExhausted);
            }

            // 🔴 Logged on the TRANSITION into exhaustion, not on every round. At a five-second
            // cadence the previous form produced twelve lines a minute per pool for as long as the
            // condition lasted — which is precisely when nobody can read the log. The continuous
            // signal is octo.lease.pool.members / octo.lease.queue.depth; this line only says when
            // it started.
            if (_exhaustedPools.TryAdd(key, DateTime.UtcNow))
            {
                Logger.Info(
                    "Adapter pool {PoolRtId} of tenant '{LenderTenantId}' has {Depth} work item(s) queued and no " +
                    "idle member on this controller instance",
                    key.PoolRtId, key.LenderTenantId, depth);
            }

            return 0;
        }

        _exhaustedPools.TryRemove(key, out _);

        var activePerTenant = members
            .Where(m => m.ActiveLease is not null)
            .GroupBy(m => m.ActiveLease!.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var perTenantCap = ReadPerTenantCap(pool);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.LeaseTtlMinutes));

        var rotation = BuildRotation(byTenant.Keys);
        var start = ResolveRotationStart(key, rotation);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var granted = 0;

        // 🔴 Round-robin, one work item per tenant per pass — never global FIFO. A tenant with 200
        // queued jobs gets exactly one turn per pass, the same as a tenant with one, which is the
        // displacement problem this whole design exists to remove.
        var madeProgress = true;
        while (available > 0 && madeProgress)
        {
            madeProgress = false;

            for (var offset = 0; offset < rotation.Count && available > 0; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tenantId = rotation[(start + offset) % rotation.Count];
                if (blocked.Contains(tenantId))
                {
                    continue;
                }

                var items = byTenant[tenantId];
                if (items.Count == 0)
                {
                    continue;
                }

                if (perTenantCap is { } cap && activePerTenant.GetValueOrDefault(tenantId) >= cap)
                {
                    // LendingMaxConcurrentLeasesPerTenant caps CONCURRENCY, not queue depth: the
                    // excess stays Queued and visible rather than being rejected. Counted so that
                    // "this tenant's work is slow" can be told apart from "the pool is full" —
                    // the two look identical in the queue view and have opposite remedies.
                    AdapterLeasingMetrics.RecordRefused(tenantId, key.LenderTenantId, key.PoolRtId,
                        LeaseStage.Schedule, LeaseRefusalReason.PerTenantCap);
                    blocked.Add(tenantId);
                    continue;
                }

                var candidate = items[0];
                var result = await TryGrantAsync(key, poolRtId, candidate, ttl, cancellationToken);
                if (result is null)
                {
                    // Nothing was granted for this tenant. The item stays where it is — both in the
                    // database and at the head of this tenant's list — so the next round serves it
                    // in the same order. Skipping past it would quietly reorder the tenant's queue.
                    blocked.Add(tenantId);
                    continue;
                }

                // 🔴 The wait is recorded here and nowhere else: only queued work has a wait, and
                // LeaseService also serves the hand-driven POST .../lease, which has no QueuedAt to
                // measure from. Per borrowing tenant, because an equal served-count with a wildly
                // unequal wait is not fairness.
                AdapterLeasingMetrics.RecordQueueWait(tenantId, key.LenderTenantId, key.PoolRtId,
                    DateTime.UtcNow - candidate.Queued.QueuedAtUtc);

                items.RemoveAt(0);
                activePerTenant[tenantId] = activePerTenant.GetValueOrDefault(tenantId) + 1;
                _rotationCursor[key] = tenantId;
                available--;
                granted++;
                madeProgress = true;
            }
        }

        if (granted > 0)
        {
            Logger.Info(
                "Adapter pool {PoolRtId} of tenant '{LenderTenantId}' granted {Granted} lease(s) across " +
                "{TenantCount} borrowing tenant(s); {Remaining} work item(s) still queued",
                key.PoolRtId, key.LenderTenantId, granted, rotation.Count, depth - granted);
        }

        return granted;
    }

    /// <summary>
    ///     Grants one lease and takes the work item out of the queue in the same step. Returns the
    ///     member that took it, or null when nothing was granted.
    /// </summary>
    private async Task<string?> TryGrantAsync(PoolKey key, OctoObjectId poolRtId, QueueCandidate candidate,
        TimeSpan ttl, CancellationToken cancellationToken)
    {
        // 🔴 AB#4924 §9.9 / D4 — the lease carries the work. The scheduler decided WHICH queued item
        // this lease serves, so it names the pipeline and the input; a member re-deriving either from
        // the tenant's queue could pick a different one, and the claim below would then have latched a
        // different work item than the one that runs.
        var request = new LeaseRequest(candidate.Borrower.TenantId, candidate.Borrower.AdapterRtEntityId.RtId,
            candidate.Queued.ExecutionId, ttl, candidate.Queued.PipelineRtId, candidate.Queued.InputData);

        var result = await _leaseService.GrantLeaseAsync(key.LenderTenantId, poolRtId, request, cancellationToken,
            // The admission gate — see ILeaseService.GrantLeaseAsync. This is where the work item
            // leaves the queue: after a member is reserved (so LeasedOnMemberId names a real member)
            // and before the member is handed the lease (so a second controller instance that
            // claimed the same item first can stop this one from dispatching it).
            async (lease, member, token) =>
            {
                _ = token;
                return await _communicationRepository.TryClaimQueuedExecutionAsync(candidate.Borrower.TenantId,
                    candidate.Queued.ExecutionId,
                    new LeaseClaim(lease.LeaseId, key.LenderTenantId, poolRtId.ToString(), member.MemberId,
                        DateTime.UtcNow));
            });

        if (result.Granted)
        {
            return result.MemberId;
        }

        Logger.Debug(
            "No lease granted for execution '{ExecutionId}' of tenant '{BorrowerTenantId}' on pool {PoolRtId}: " +
            "{Reason}",
            candidate.Queued.ExecutionId, candidate.Borrower.TenantId, key.PoolRtId, result.StatusMessage);
        return null;
    }

    /// <summary>
    ///     Reads the queue of every borrower of one pool, grouped by borrowing tenant and ordered
    ///     inside each tenant by class and then arrival.
    /// </summary>
    private async Task<Dictionary<string, List<QueueCandidate>>> ReadQueueByTenantAsync(
        IReadOnlyList<PoolBorrower> borrowers, CancellationToken cancellationToken)
    {
        var byTenant = new Dictionary<string, List<QueueCandidate>>(StringComparer.OrdinalIgnoreCase);
        var take = Math.Max(1, _options.LeaseQueueReadLimitPerAdapter);

        foreach (var borrower in borrowers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<QueuedExecution> queued;
            try
            {
                queued = await _communicationRepository.GetQueuedExecutionsForAdapterAsync(borrower.TenantId,
                    borrower.AdapterRtEntityId, take);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "[{TenantId}] Could not read the lease queue of adapter '{AdapterName}'",
                    borrower.TenantId, borrower.AdapterName ?? borrower.AdapterRtEntityId.RtId.ToString());
                continue;
            }

            if (queued.Count == 0)
            {
                continue;
            }

            if (!byTenant.TryGetValue(borrower.TenantId, out var items))
            {
                items = [];
                byTenant[borrower.TenantId] = items;
            }

            items.AddRange(queued.Select(q => new QueueCandidate(borrower, q)));
        }

        foreach (var items in byTenant.Values)
        {
            // 🔴 Interactive before Batch, INSIDE one tenant's list only. The rotation below never
            // consults the class, so a tenant can not overtake another by declaring its work
            // interactive.
            items.Sort((a, b) => QueuedExecution.SchedulingOrder.Compare(a.Queued, b.Queued));
        }

        return byTenant;
    }

    /// <summary>
    ///     The rotation ring: every borrowing tenant with queued work, in a stable order.
    /// </summary>
    private static List<string> BuildRotation(IEnumerable<string> tenantIds)
    {
        return tenantIds.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Where in the ring this round begins: just after the tenant served last.
    /// </summary>
    private int ResolveRotationStart(PoolKey key, List<string> rotation)
    {
        if (rotation.Count == 0)
        {
            return 0;
        }

        if (!_rotationCursor.TryGetValue(key, out var lastServed))
        {
            return 0;
        }

        var index = rotation.FindIndex(t => string.Equals(t, lastServed, StringComparison.OrdinalIgnoreCase));

        // A cursor naming a tenant that has nothing queued right now is not stale — it is the whole
        // memory of the rotation. Fall back to the start rather than losing it.
        return index < 0 ? 0 : (index + 1) % rotation.Count;
    }

    /// <summary>
    ///     Queue-driven scale-up (§9.4, concept §8 Q14).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The averaging window applies to the depth signal, and only to it.</b> Depth is an
    ///         instantaneous reading: a burst of six work items that drains in two seconds is not a
    ///         pool that needs another member, and reacting to it would start a process that is idle
    ///         by the time it is ready. The wait signal needs no window at all — an item that has
    ///         waited sixty seconds has already integrated sixty seconds of pressure.
    ///     </para>
    ///     <para>
    ///         🔴 <b>The window's default is derived, not invented.</b> Q14 deliberately leaves it to
    ///         be measured rather than guessed, so hard-coding a number here would be exactly the
    ///         wrong move. With <c>LeaseScaleUpAveragingWindowSeconds</c> left at 0 the window is the
    ///         pool's own <c>ScaleUpQueueWaitSeconds</c>: the author has already declared how long a
    ///         work item may wait before the pool should grow, and a burst that clears faster than
    ///         that is by their own definition not worth a member. Set the option to override it
    ///         globally once increment 9 has produced real queue-depth data.
    ///     </para>
    /// </remarks>
    private async Task<ScaleUpEvaluation> EvaluateScaleUpAsync(PoolKey key, RtAdapterPool? pool, int depth,
        TimeSpan oldestWait, int membersHere)
    {
        if (pool is null)
        {
            // No entity, no thresholds, so no signal was evaluated. Depth, wait and member counts
            // are still real observations and are still published by the caller; only the window
            // and the signals read as "not evaluated" (window 0), which is what they are.
            return ScaleUpEvaluation.Unknown;
        }

        var now = DateTime.UtcNow;
        var window = ResolveAveragingWindow(pool);

        var samples = _depthSamples.GetOrAdd(key, _ => []);
        bool depthSignal;
        lock (samples)
        {
            samples.Add(new DepthSample(now, depth));

            // 🔴 Retained for TWICE the window, not for the window. Pruning at exactly the window
            // would drop the very sample that proves the history is long enough — with a 60 s window
            // and a 61 s old sample, the ring would be emptied down to the newest reading and the
            // signal could never fire. The evaluation below still averages over the window only.
            samples.RemoveAll(s => now - s.SampledAtUtc > window + window);

            var inWindow = samples.Where(s => now - s.SampledAtUtc <= window).ToList();

            // Two conditions, and both matter: the observation has to REACH BACK at least a whole
            // window (one reading is not an average, and acting on one would make the window
            // decorative), and the average inside the window has to clear the threshold.
            depthSignal = samples.Count >= 2
                          && now - samples[0].SampledAtUtc >= window
                          && inWindow.Count > 0
                          && inWindow.Average(s => s.Depth) >= pool.ScaleUpQueueDepthThreshold;
        }

        var waitSignal = pool.ScaleUpQueueWaitSeconds > 0
                         && oldestWait.TotalSeconds >= pool.ScaleUpQueueWaitSeconds;

        var fires = pool.ScaleUpPolicy switch
        {
            RtPoolScaleUpPolicyEnum.QueueDepth => depthSignal,
            RtPoolScaleUpPolicyEnum.QueueWaitSeconds => waitSignal,
            _ => depthSignal || waitSignal
        };

        var evaluation = new ScaleUpEvaluation(window, depthSignal, waitSignal,
            // 🔴 The alertable condition, decided where the knowledge lives. "The queue is waiting"
            // and "the pool is at its ceiling" are two facts that live in different places, and an
            // alert rule joining them would be a second implementation of the judgement this method
            // already makes every round — the same reasoning as octo.workload.offline_unexpected.
            //
            // ⚠️ Read with the same caveat the scale-up arithmetic below carries: membersHere counts
            // the members connected to THIS controller instance. With more than one controller pod
            // each sees a subset, so each underestimates the pool and this flag under-reports. That
            // is a property of increment 7's scale-up, not of the metric; see plan §11.8.
            Undersized: fires && Math.Max(pool.MinReplicas, membersHere) + 1 > pool.MaxReplicas);

        if (!fires)
        {
            return evaluation;
        }

        // One scale-up per window per pool. Kubernetes needs longer than a scheduling round to make
        // a member ready, so a pool would otherwise be asked to grow on every tick of a burst it is
        // already responding to.
        if (_lastScaleUpUtc.TryGetValue(key, out var last) && now - last < window)
        {
            return evaluation;
        }

        var desired = Math.Max(pool.MinReplicas, membersHere) + 1;
        if (desired > pool.MaxReplicas)
        {
            // Concept §6, "Pool exhausted". Said once per window rather than per round, and said at
            // all — a queue that is growing against a ceiling is an operator decision (scale the
            // pool, or move the tenant to a dedicated adapter), not something to log at Debug.
            _lastScaleUpUtc[key] = now;
            Logger.Warn(
                "Adapter pool '{PoolName}' ({PoolRtId}) of tenant '{LenderTenantId}' is at its ceiling of " +
                "{MaxReplicas} member(s) with {Depth} work item(s) queued and the oldest waiting {WaitSeconds:F0}s",
                pool.Name, key.PoolRtId, key.LenderTenantId, pool.MaxReplicas, depth, oldestWait.TotalSeconds);
            await _eventService.StoreErrorEventAsync(key.LenderTenantId,
                $"Adapter pool '{pool.Name}' is at its ceiling of {pool.MaxReplicas} member(s) while " +
                $"{depth} work item(s) wait for a lease. Raise MaxReplicas, or move a borrower to a dedicated " +
                "adapter.");
            AdapterLeasingMetrics.RecordScaleUp(key.LenderTenantId, key.PoolRtId,
                AdapterLeasingMetrics.ScaleUpOutcomes.AtCeiling);
            return evaluation;
        }

        _lastScaleUpUtc[key] = now;

        try
        {
            // 🔴 Through PoolService, which routes into WorkloadLifecycleService.RequestScaleAsync
            // and its MinReplicas..MaxReplicas clamp (increment 5). A second clamp written here
            // would be a second opinion about the pool's declared range, and the two would drift.
            var effective = await _poolService.ScaleAdapterPoolAsync(key.LenderTenantId, pool.RtId, desired);
            AdapterLeasingMetrics.RecordScaleUp(key.LenderTenantId, key.PoolRtId,
                AdapterLeasingMetrics.ScaleUpOutcomes.Scaled);
            Logger.Info(
                "Adapter pool '{PoolName}' ({PoolRtId}) of tenant '{LenderTenantId}' scaling to {Effective} " +
                "member(s): depth={Depth} (threshold {Threshold}, window {WindowSeconds}s, signal={DepthSignal}), " +
                "oldest wait={WaitSeconds:F0}s (threshold {WaitThreshold}s, signal={WaitSignal})",
                pool.Name, key.PoolRtId, key.LenderTenantId, effective, depth, pool.ScaleUpQueueDepthThreshold,
                window.TotalSeconds, depthSignal, oldestWait.TotalSeconds, pool.ScaleUpQueueWaitSeconds, waitSignal);
        }
        catch (Exception e)
        {
            AdapterLeasingMetrics.RecordScaleUp(key.LenderTenantId, key.PoolRtId,
                AdapterLeasingMetrics.ScaleUpOutcomes.Failed);
            Logger.Warn(e,
                "Could not scale adapter pool '{PoolName}' ({PoolRtId}) of tenant '{LenderTenantId}' to " +
                "{Desired} member(s)",
                pool.Name, key.PoolRtId, key.LenderTenantId, desired);
        }

        return evaluation;
    }

    /// <summary>
    ///     What one round's scale-up evaluation saw, so the gauges can publish the inputs next to
    ///     the decision they produced (AB#4924 increment 9, concept §8 Q14).
    /// </summary>
    /// <param name="Window">The depth-averaging window in force for this pool.</param>
    /// <param name="DepthSignal">Whether averaged queue depth cleared the pool's threshold.</param>
    /// <param name="WaitSignal">Whether the oldest wait cleared the pool's threshold.</param>
    /// <param name="Undersized">
    ///     Whether the pool wants to grow and is already at <c>MaxReplicas</c> — it cannot self-heal
    ///     and needs a human.
    /// </param>
    private readonly record struct ScaleUpEvaluation(TimeSpan Window, bool DepthSignal, bool WaitSignal,
        bool Undersized)
    {
        /// <summary>A round that could not read the pool entity and therefore evaluated nothing.</summary>
        public static ScaleUpEvaluation Unknown => new(TimeSpan.Zero, false, false, false);
    }

    /// <summary>
    ///     The window queue depth is averaged over before a scale-up fires.
    /// </summary>
    private TimeSpan ResolveAveragingWindow(RtAdapterPool pool)
    {
        if (_options.LeaseScaleUpAveragingWindowSeconds > 0)
        {
            return TimeSpan.FromSeconds(_options.LeaseScaleUpAveragingWindowSeconds);
        }

        return TimeSpan.FromSeconds(Math.Max(1, pool.ScaleUpQueueWaitSeconds));
    }

    /// <summary>
    ///     The pool's per-tenant concurrency cap, or null when it declares none.
    /// </summary>
    /// <remarks>
    ///     Read through <c>GetAttributeValueOrDefault</c> rather than the generated property, for the
    ///     same reason the credential projection does: the attribute is optional, and a pool written
    ///     before it existed must degrade to "no cap" (concept §8 Q11 deliberately left it unset)
    ///     rather than throw inside a scheduling round.
    /// </remarks>
    private static int? ReadPerTenantCap(RtAdapterPool? pool)
    {
        if (pool is null)
        {
            return null;
        }

        var raw = pool.GetAttributeValueOrDefault(nameof(RtAdapterPool.LendingMaxConcurrentLeasesPerTenant));
        return raw switch
        {
            int value and > 0 => value,
            long value and > 0 => (int)value,
            _ => null
        };
    }

    private async Task<RtAdapterPool?> TryReadPoolAsync(PoolKey key, OctoObjectId poolRtId)
    {
        try
        {
            return await _communicationRepository.GetWorkloadByRtIdAsync(key.LenderTenantId, poolRtId)
                as RtAdapterPool;
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{LenderTenantId}] Could not read adapter pool {PoolRtId} for a scheduling round",
                key.LenderTenantId, poolRtId);
            return null;
        }
    }

    /// <summary>
    ///     Which borrowers each pool serves, rebuilt at most every
    ///     <c>LeaseTopologyRefreshSeconds</c>.
    /// </summary>
    /// <remarks>
    ///     🔴 Built from the BORROWERS' declarations, not from the pools. The borrowing relationship
    ///     is the borrower's own statement (<c>LifecycleMode = Leased</c> plus
    ///     <c>LentFromTenantId</c>/<c>LentFromPoolRtId</c>), and the lender's sharing scope is checked
    ///     per lease by <c>LeaseService</c>. Walking from the pools instead would require resolving
    ///     the lending scope to a tenant list on every round, and would put the lender in a position
    ///     to push work into a tenant that never asked for it.
    /// </remarks>
    private async Task<IReadOnlyDictionary<PoolKey, IReadOnlyList<PoolBorrower>>> GetTopologyAsync(
        CancellationToken cancellationToken)
    {
        var refresh = TimeSpan.FromSeconds(Math.Max(5, _options.LeaseTopologyRefreshSeconds));
        if (DateTime.UtcNow - _topologyBuiltAtUtc < refresh)
        {
            return _topology;
        }

        await _topologyLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _topologyBuiltAtUtc < refresh)
            {
                return _topology;
            }

            var built = new Dictionary<PoolKey, List<PoolBorrower>>();

            foreach (var tenantId in _adapterCache.GetEnabledTenantIds())
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyCollection<RtDeployableWorkload> workloads;
                try
                {
                    workloads = await _communicationRepository.GetWorkloadsAsync(tenantId);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "[{TenantId}] Could not read workloads while building the lease topology",
                        tenantId);
                    continue;
                }

                foreach (var workload in workloads)
                {
                    if (workload is not RtAdapter adapter ||
                        adapter.LifecycleMode != RtLifecycleModeEnum.Leased)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(adapter.LentFromTenantId) ||
                        string.IsNullOrWhiteSpace(adapter.LentFromPoolRtId) ||
                        !OctoObjectId.TryParse(adapter.LentFromPoolRtId!, out var poolRtId))
                    {
                        // The deploy guard refuses this pair (increment 2); an entity that reached
                        // this state anyway is skipped rather than guessed at.
                        Logger.Warn(
                            "[{TenantId}] Leased adapter '{AdapterName}' names no usable pool " +
                            "(LentFromTenantId='{LentFromTenantId}', LentFromPoolRtId='{LentFromPoolRtId}')",
                            tenantId, adapter.Name, adapter.LentFromTenantId, adapter.LentFromPoolRtId);
                        continue;
                    }

                    var key = PoolKey.Create(adapter.LentFromTenantId!, poolRtId.ToString());
                    if (!built.TryGetValue(key, out var borrowers))
                    {
                        borrowers = [];
                        built[key] = borrowers;
                    }

                    borrowers.Add(new PoolBorrower(tenantId,
                        new RtEntityId(adapter.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId),
                        adapter.Name));
                }
            }

            _topology = built.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PoolBorrower>)kv.Value);
            _topologyBuiltAtUtc = DateTime.UtcNow;
            return _topology;
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    /// <summary>One pool, identified across tenants.</summary>
    private readonly record struct PoolKey(string LenderTenantId, string PoolRtId)
    {
        public static PoolKey Create(string lenderTenantId, string poolRtId)
        {
            // Normalised at construction so the dictionary key, the rotation cursor and the sample
            // ring all agree on what "the same pool" means, whatever casing a caller used.
            return new PoolKey(lenderTenantId.ToLowerInvariant(), poolRtId.ToLowerInvariant());
        }
    }

    /// <summary>One adapter that borrows from a pool.</summary>
    private sealed record PoolBorrower(string TenantId, RtEntityId AdapterRtEntityId, string? AdapterName);

    /// <summary>One queued work item together with the borrower it belongs to.</summary>
    private sealed record QueueCandidate(PoolBorrower Borrower, QueuedExecution Queued);

    /// <summary>One queue-depth reading of one pool.</summary>
    private readonly record struct DepthSample(DateTime SampledAtUtc, int Depth);
}
