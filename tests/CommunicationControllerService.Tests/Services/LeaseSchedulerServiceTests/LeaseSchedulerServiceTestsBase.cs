using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     Shared arrangement for the pool queue scheduler (AB#4924 increment 7).
/// </summary>
/// <remarks>
///     <para>
///         The connection manager is the <b>real</b> one: which member a lease lands on, and whether a
///         member is still available afterwards, is half of what the scheduler decides. Everything
///         that reads a tenant database is substituted, and <see cref="ILeaseService" /> is
///         substituted with a stub that behaves like the real one in the two respects that matter —
///         it claims a member, and it runs the admission gate before reporting success.
///     </para>
///     <para>
///         🔴 The stub deliberately mirrors the real ordering (reserve member → admission gate → push
///         → report). A stub that reported success without running the gate would make every fairness
///         test below pass while the queue was never actually drained.
///     </para>
/// </remarks>
internal abstract class LeaseSchedulerServiceTestsBase
{
    protected const string LenderTenantId = "lender";
    protected const string TenantA = "tenant-a";
    protected const string TenantB = "tenant-b";
    protected const string TenantC = "tenant-c";

    /// <summary>
    ///     A fresh pool per test instance, not a shared constant. The leasing metrics of increment 9
    ///     are process-wide statics tagged by pool rtId, and TUnit runs these tests concurrently — a
    ///     shared pool id would let one test read another's measurements.
    /// </summary>
    protected readonly OctoObjectId AdapterPoolRtId = OctoObjectId.GenerateNewId();

    protected readonly IAdapterCache AdapterCache = Substitute.For<IAdapterCache>();
    protected readonly ICommunicationRepository CommunicationRepository =
        Substitute.For<ICommunicationRepository>();
    protected readonly IAdapterPoolConnectionManager ConnectionManager = new AdapterPoolConnectionManager();
    protected readonly ICommunicationEventService EventService = Substitute.For<ICommunicationEventService>();
    protected readonly ILeaseService LeaseService = Substitute.For<ILeaseService>();
    protected readonly IDeploymentSiteService DeploymentSiteService = Substitute.For<IDeploymentSiteService>();

    protected readonly CommunicationControllerOptions Options = new();
    protected readonly ILeaseSchedulerService Scheduler;

    /// <summary>Every (tenant, executionId) the scheduler actually took out of a queue, in order.</summary>
    protected readonly List<(string TenantId, string ExecutionId)> Claimed = [];

    /// <summary>Per borrowing tenant, the adapter that borrows from the pool.</summary>
    protected readonly Dictionary<string, RtAdapter> Borrowers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<QueuedExecution>> _queues =
        new(StringComparer.OrdinalIgnoreCase);

    private int _memberSequence;

    protected LeaseSchedulerServiceTestsBase()
    {
        // The topology refresh floor is 5 s; keep the default so a second round inside one test
        // reuses the cached topology instead of re-reading every tenant's workloads.
        Options.LeaseTopologyRefreshSeconds = 60;

        ArrangeLeaseServiceStub();

        Scheduler = new LeaseSchedulerService(AdapterCache, CommunicationRepository, ConnectionManager,
            EventService, LeaseService, DeploymentSiteService, new OptionsWrapper<CommunicationControllerOptions>(Options));
    }

    /// <summary>
    ///     Registers <paramref name="count" /> idle members of the pool on this controller instance.
    /// </summary>
    protected void ArrangeMembers(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var index = _memberSequence++;
            ConnectionManager.RegisterMember($"conn-{index}", $"member-{index}", LenderTenantId,
                AdapterPoolRtId.ToString());
        }
    }

    /// <summary>
    ///     Registers a member that is already busy serving <paramref name="busyForTenantId" />, so the
    ///     per-tenant concurrency cap has something to count.
    /// </summary>
    protected string ArrangeBusyMember(string busyForTenantId, DateTime? expiresAtUtc = null)
    {
        var index = _memberSequence++;
        var connectionId = $"conn-{index}";
        ConnectionManager.RegisterMember(connectionId, $"member-{index}", LenderTenantId, AdapterPoolRtId.ToString());
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId.ToString(), new LeaseDto
        {
            LeaseId = $"lease-{index}",
            TenantId = busyForTenantId,
            AdapterPoolTenantId = LenderTenantId,
            AdapterPoolRtId = AdapterPoolRtId.ToString(),
            ExecutionId = $"busy-execution-{index}",
            GrantedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(14)
        });
        return connectionId;
    }

    /// <summary>
    ///     Declares that <paramref name="tenantId" /> borrows from the pool and queues
    ///     <paramref name="entries" /> for it.
    /// </summary>
    protected void ArrangeQueue(string tenantId, params QueuedExecution[] entries)
    {
        if (!Borrowers.TryGetValue(tenantId, out var adapter))
        {
            adapter = RtEntityCreator.CreateAdapter();
            adapter.Name = $"{tenantId}-adapter";
            adapter.LifecycleMode = RtLifecycleModeEnum.Leased;
            adapter.LentFromTenantId = LenderTenantId;
            adapter.LentFromPoolRtId = AdapterPoolRtId.ToString();
            Borrowers[tenantId] = adapter;

            CommunicationRepository.GetWorkloadsAsync(tenantId)
                .Returns(new List<RtDeployableWorkload> { adapter });
        }

        var queue = _queues.TryGetValue(tenantId, out var existing) ? existing : [];
        queue.AddRange(entries);
        _queues[tenantId] = queue;

        AdapterCache.GetEnabledTenantIds().Returns(_ => _queues.Keys.ToList());

        var adapterRtEntityId = new RtEntityId(adapter.CkTypeId!, adapter.RtId);
        CommunicationRepository
            .GetQueuedExecutionsForAdapterAsync(tenantId, Arg.Is<RtEntityId>(id => id.RtId == adapterRtEntityId.RtId),
                Arg.Any<int>())
            .Returns(_ => (IReadOnlyList<QueuedExecution>)_queues[tenantId].ToList());
    }

    /// <summary>
    ///     A queue entry. Arrival is expressed in minutes ago so "older" reads as a larger number.
    /// </summary>
    protected static QueuedExecution Entry(string executionId, int minutesAgo,
        int executionClass = QueuedExecution.BatchClass, OctoObjectId? pipelineRtId = null,
        string? inputData = null)
    {
        return new QueuedExecution(executionId, OctoObjectId.GenerateNewId(),
            DateTime.UtcNow.AddMinutes(-minutesAgo), pipelineRtId ?? OctoObjectId.GenerateNewId(),
            $"pipeline-{executionId}", executionClass, inputData);
    }

    /// <summary>Every <see cref="LeaseRequest" /> the scheduler handed to the lease service, in order.</summary>
    protected readonly List<LeaseRequest> Requests = [];

    /// <summary>
    ///     The pool entity the lender owns. Absent by default — a scheduling round must work without
    ///     it, because a pool that cannot be read must not stop work that is already queued.
    /// </summary>
    protected RtAdapterPool ArrangePool(int minReplicas = 1, int maxReplicas = 3,
        int? maxConcurrentLeasesPerTenant = null, int scaleUpQueueDepthThreshold = 5,
        int scaleUpQueueWaitSeconds = 60,
        RtPoolScaleUpPolicyEnum policy = RtPoolScaleUpPolicyEnum.QueueDepthOrWaitSeconds)
    {
        var pool = new RtAdapterPool
        {
            RtId = AdapterPoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "shared-pool",
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas,
            ScaleUpPolicy = policy,
            ScaleUpQueueDepthThreshold = scaleUpQueueDepthThreshold,
            ScaleUpQueueWaitSeconds = scaleUpQueueWaitSeconds
        };

        if (maxConcurrentLeasesPerTenant is { } cap)
        {
            pool.LendingMaxConcurrentLeasesPerTenant = cap;
        }

        CommunicationRepository.GetWorkloadByRtIdAsync(LenderTenantId, AdapterPoolRtId).Returns(pool);
        return pool;
    }

    /// <summary>
    ///     Makes <see cref="ILeaseService.GrantLeaseAsync" /> behave like the real one: reserve an
    ///     idle member, run the admission gate, and report the member on success.
    /// </summary>
    private void ArrangeLeaseServiceStub()
    {
        // Drain really marks the member locally, exactly as the real service does — the reaper's
        // contract is that the member is left drained AND lease-free, and a stub that only recorded
        // the call would let a scheduler that forgot one of the two pass.
        LeaseService.DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                ConnectionManager.MarkDraining(call.ArgAt<string>(0));
                return Task.CompletedTask;
            });

        CommunicationRepository
            .TryClaimQueuedExecutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LeaseClaim>())
            .Returns(call =>
            {
                var tenantId = call.ArgAt<string>(0);
                var executionId = call.ArgAt<string>(1);
                Claimed.Add((tenantId, executionId));

                // The real repository removes the item from the queue by moving it out of Queued.
                if (_queues.TryGetValue(tenantId, out var queue))
                {
                    queue.RemoveAll(e => e.ExecutionId == executionId);
                }

                return Task.FromResult(true);
            });

        LeaseService
            .GrantLeaseAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<LeaseRequest>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>>?>())
            .Returns(async call =>
            {
                var lenderTenantId = call.ArgAt<string>(0);
                var poolRtId = call.ArgAt<OctoObjectId>(1);
                var request = call.ArgAt<LeaseRequest>(2);
                Requests.Add(request);
                var gate = call.ArgAt<Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>>?>(4);

                var lease = new LeaseDto
                {
                    LeaseId = Guid.NewGuid().ToString("N"),
                    TenantId = request.BorrowerTenantId,
                    AdapterPoolTenantId = lenderTenantId,
                    AdapterPoolRtId = poolRtId.ToString(),
                    AdapterRtId = request.BorrowerAdapterRtId.ToString(),
                    ExecutionId = request.ExecutionId ?? string.Empty,
                    GrantedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow + (request.Ttl ?? ILeaseService.DefaultLeaseTtl)
                };

                var member = ConnectionManager.TryClaimMember(lenderTenantId, poolRtId.ToString(), lease);
                if (member is null)
                {
                    return LeaseGrantResult.Refused(LeaseRefusalReason.NoIdleMember, "No idle member.");
                }

                if (gate is not null && !await gate(lease, member, CancellationToken.None))
                {
                    ConnectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
                    return LeaseGrantResult.Refused(LeaseRefusalReason.AdmissionGateDeclined,
                        "The work item was taken by somebody else.");
                }

                return new LeaseGrantResult(true, lease.LeaseId, member.MemberId, null);
            });
    }
}
