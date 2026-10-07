using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ILeaseService" />
internal class LeaseService : ILeaseService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IAdapterPoolConnectionManager _connectionManager;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly ICommunicationEventService _eventService;
    private readonly IWorkloadEncryptionService _encryptionService;
    private readonly IHubContext<AdapterPoolHub> _hubContext;
    private readonly ITenantLendingScopeResolver _lendingScopeResolver;
    private readonly IPipelineServiceAccountResolver _serviceAccountResolver;
    private readonly ITenantDatabaseCredentialResolver _databaseCredentialResolver;
    private readonly IAdapterService _adapterService;
    private readonly ILifecycleConfigurationService _lifecycleConfiguration;

    private readonly ILeaseSchedulerWakeSignal _wakeSignal;

    /// <summary>
    ///     AB#5864 — how often a work item may be refused by a draining member and returned to the
    ///     queue before it is failed instead. Three: one refusal is the expected grant-vs-drain race,
    ///     a second is a coincidence, a third in a row is a pattern nobody should wait out.
    /// </summary>
    internal const int MaxRefusedLeaseRequeues = 3;

    private static readonly TimeSpan RefusalCountRetention = TimeSpan.FromHours(1);

    // AB#5864 — refusal count per re-queued attempt, see RememberRefusalCount.
    private readonly ConcurrentDictionary<string, (int Count, DateTime RecordedAtUtc)> _refusalCounts =
        new(StringComparer.Ordinal);

    // 🔴 AB#5826 — leases whose member disconnected, keyed by lease id, held back for the reconnect
    // grace instead of being re-queued at once. A disconnect does not stop the member: it keeps
    // computing and reconnects within seconds, so an immediate re-queue ran the work twice and dropped
    // the original result. In memory and per controller instance, like the registry it complements;
    // a controller restart loses it, which is what the resumption on the member side covers.
    private readonly ConcurrentDictionary<string, LostLease> _lostLeases = new(StringComparer.Ordinal);

    private readonly TimeSpan _reconnectGrace;
    private readonly TimeSpan _leaseTtl;

    private sealed record LostLease(PoolMemberConnection Member, LeaseDto Lease, DateTime DeadlineUtc);

    public LeaseService(IAdapterPoolConnectionManager connectionManager,
        ICommunicationRepository communicationRepository,
        ICommunicationEventService eventService,
        IWorkloadEncryptionService encryptionService,
        IHubContext<AdapterPoolHub> hubContext,
        ITenantLendingScopeResolver lendingScopeResolver,
        IPipelineServiceAccountResolver serviceAccountResolver,
        ITenantDatabaseCredentialResolver databaseCredentialResolver,
        IAdapterService adapterService,
        ILifecycleConfigurationService lifecycleConfiguration,
        ILeaseSchedulerWakeSignal wakeSignal,
        IOptions<CommunicationControllerOptions>? options = null)
    {
        // AB#5826: without options (a unit-test composition) there is no reconnect grace — the
        // behaviour every test written before it was arranged against. Production always has them.
        _reconnectGrace = TimeSpan.FromSeconds(Math.Max(0, options?.Value.LeaseMemberReconnectGraceSeconds ?? 0));
        _leaseTtl = options is null
            ? ILeaseService.DefaultLeaseTtl
            : TimeSpan.FromMinutes(Math.Max(1, options.Value.LeaseTtlMinutes));

        _connectionManager = connectionManager;
        _communicationRepository = communicationRepository;
        _eventService = eventService;
        _encryptionService = encryptionService;
        _hubContext = hubContext;
        _lendingScopeResolver = lendingScopeResolver;
        _serviceAccountResolver = serviceAccountResolver;
        _databaseCredentialResolver = databaseCredentialResolver;
        _adapterService = adapterService;
        _lifecycleConfiguration = lifecycleConfiguration;
        _wakeSignal = wakeSignal;
    }

    /// <inheritdoc />
    public async Task<LeaseGrantResult> GrantLeaseAsync(string lenderTenantId, OctoObjectId adapterPoolRtId,
        LeaseRequest request, CancellationToken cancellationToken = default,
        Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>>? admissionGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lenderTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BorrowerTenantId);

        // 🔴 AB#4924 §14 — the per-tenant kill switch, BOTH halves, and this is the choke point that
        // makes "off" mean off. Every grant goes through here: the scheduler's and the hand-driven
        // POST {tenantId}/v1/adapterPool/{id}/lease alike. Gating only the enqueue would leave a full
        // queue draining for as long as it takes after somebody turned leasing off, which is not what
        // an operator means by the word.
        //
        // Lending is the lender's capability and borrowing is the borrower's, and neither tenant can
        // assert the other's - so both flags are required and the refusal names which one is missing.
        var leasingRefusal = await CheckLeasingEnabledAsync(lenderTenantId, request.BorrowerTenantId);
        if (leasingRefusal != null)
        {
            return Refuse(lenderTenantId, adapterPoolRtId, request, leasingRefusal.Value.Reason,
                leasingRefusal.Value.Message);
        }

        // 🔴 The order of the three checks below is the order of least trust. The borrowing
        // relationship is checked BEFORE the credential is read, so a request naming an adapter it
        // has no business naming never reaches the code that decrypts a client secret.
        var borrower = await ReadBorrowerAdapterAsync(request.BorrowerTenantId, request.BorrowerAdapterRtId);
        if (borrower is null)
        {
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.BorrowerAdapterUnknown,
                $"Tenant '{request.BorrowerTenantId}' has no adapter with rtId {request.BorrowerAdapterRtId}.");
        }

        // AB#5271: what the borrower declares now hangs off its LentFrom edge to a borrower-local
        // mirror, so reading it is a repository round trip rather than two attribute reads. Still on
        // the pre-credential side of the order above: it is resolved here, before anything decrypts.
        var borrowerLentFrom = LentFromReference.FromMirror(
            await _communicationRepository.GetLentAdapterPoolForAdapterAsync(request.BorrowerTenantId,
                request.BorrowerAdapterRtId));

        var declarationRefusal =
            CheckBorrowerDeclaration(borrower, borrowerLentFrom, lenderTenantId, adapterPoolRtId, request);
        if (declarationRefusal != null)
        {
            return Refuse(lenderTenantId, adapterPoolRtId, request, declarationRefusal.Value.Reason,
                declarationRefusal.Value.Message);
        }

        // The lender's half. Both halves are required and neither implies the other: the borrower
        // says "I borrow from that pool", the pool says "I lend into that part of the tree". A pool
        // that has been deleted or re-scoped since the borrower was deployed lands here (concept §6,
        // "parent tenant deleted while lending") rather than at deploy time.
        var lendingScope = await _communicationRepository
            .TryGetAdapterPoolLendingScopeAsync(lenderTenantId, adapterPoolRtId.ToString());
        if (lendingScope is null)
        {
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.PoolUnknown,
                $"Tenant '{lenderTenantId}' has no adapter pool with rtId {adapterPoolRtId}, or it cannot be read.");
        }

        if (!await _lendingScopeResolver.MayLendAsync(lenderTenantId, request.BorrowerTenantId,
                lendingScope.Value, cancellationToken))
        {
            // Audited, not only logged: a lease attempt across a boundary the tenant tree does not
            // allow is the shape a cross-tenant incident would take, and it must be visible in the
            // borrower's own event log rather than only in a controller pod's stdout.
            await _eventService.StoreErrorEventAsync(request.BorrowerTenantId,
                $"Refused a lease of adapter pool {adapterPoolRtId} in tenant '{lenderTenantId}': that pool does not " +
                "lend to this tenant. Check the pool's SharingMode and LendingAllowedTenantIds.");
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.LendingScopeDenied,
                $"Adapter pool {adapterPoolRtId} in tenant '{lenderTenantId}' does not lend to tenant " +
                $"'{request.BorrowerTenantId}'.");
        }

        var credential = await ResolveBorrowerCredentialAsync(request.BorrowerTenantId, borrower);
        if (credential is null)
        {
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.BorrowerCredentialMissing,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' has no usable pipeline " +
                "service account; a pool member cannot act as a borrower without one.");
        }

        // 🔴 AB#4924 — the borrower's DATA access, and the reason the operator's refusal of the
        // cluster's shared data-store credentials to an adapter pool is not merely a gesture. A pool
        // member opens MongoDB directly (AddMongoDbRuntimeRepository in its DI) and has no standing
        // credential to any tenant database, so without this the member would execute the borrower's
        // pipeline against whatever credentials its own process happened to inherit — which on a
        // developer's machine is everything and in a cluster is nothing. Resolved here, next to the
        // identity credential and under the same order-of-least-trust rule: both halves of "become the
        // borrower" are produced only after the borrowing relationship has been proven real.
        var databaseCredential = await _databaseCredentialResolver
            .TryResolveAsync(request.BorrowerTenantId, cancellationToken);
        if (databaseCredential is null)
        {
            // Audited on the borrower: its work is not going to run, and a controller log line would
            // leave the tenant with a queue entry that never moves and no explanation it can see.
            await _eventService.StoreErrorEventAsync(request.BorrowerTenantId,
                "Refused a lease: the database credential of this tenant could not be resolved, so a pool " +
                "member could not be given access to its data. Check that the tenant has a database record " +
                "and that the controller is configured with the datasource credentials.");
            return Refuse(lenderTenantId, adapterPoolRtId, request,
                LeaseRefusalReason.BorrowerDatabaseCredentialUnresolvable,
                $"The database credential of tenant '{request.BorrowerTenantId}' could not be resolved; a pool " +
                "member cannot reach the borrower's data without it.");
        }

        // 🔴 AB#4924 §9.9 / D4 — a lease must carry the work. Resolved BEFORE a member is reserved:
        // a pipeline that cannot be projected is a refusal, and refusing after a member was claimed
        // would cost the pool a member per round for as long as the pipeline stays broken.
        PipelineConfigurationDto? pipelineConfiguration = null;
        if (request.PipelineRtId is { } pipelineRtId)
        {
            pipelineConfiguration = await _adapterService.GetLeasedPipelineConfigurationAsync(
                request.BorrowerTenantId,
                // Same fallback as AdapterCkTypeId below: Adapter is polymorphic, and an entity read
                // without its discriminator must stay usable rather than become a null reference.
                new RtEntityId(borrower.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId, borrower.RtId),
                pipelineRtId);
            if (pipelineConfiguration is null)
            {
                // Audited on the borrower, because this is its pipeline and its work item that is not
                // going to run; a controller log line would leave the tenant staring at a queue entry
                // that never moves with no explanation anywhere it can see.
                await _eventService.StoreErrorEventAsync(request.BorrowerTenantId,
                    $"Refused a lease for pipeline '{pipelineRtId}': it is not deployed to adapter " +
                    $"'{borrower.Name}', is disabled, or carries no definition. The work item stays queued.");
                return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.PipelineProjectionFailed,
                    $"Pipeline '{pipelineRtId}' of tenant '{request.BorrowerTenantId}' could not be projected for " +
                    $"adapter '{borrower.Name}'; it is not deployed there, disabled, or has no definition.");
            }
        }

        var grantedAt = DateTime.UtcNow;
        var lease = new LeaseDto
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            TenantId = request.BorrowerTenantId,
            AdapterPoolTenantId = lenderTenantId,
            AdapterPoolRtId = adapterPoolRtId.ToString(),
            AdapterRtId = borrower.RtId.ToString(),
            // The concrete CK type, not the base Adapter id: Adapter is polymorphic and the member
            // has to rebuild the exact RtEntityId to register under. A null here would mean an
            // entity read without its discriminator, which the fallback keeps usable rather than
            // turning into a NullReferenceException inside a hub method.
            AdapterCkTypeId = (borrower.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId).ToString(),
            ExecutionId = request.ExecutionId ?? string.Empty,
            PipelineRtId = request.PipelineRtId?.ToString() ?? string.Empty,
            PipelineInput = request.PipelineInput,
            Pipeline = pipelineConfiguration,
            // AB#5279: the invoker the item was queued for, and its token when the controller could
            // keep it and it is still usable. Resolved before the member is reserved, like the
            // projection above - a lease is built once and pushed as-is.
            Caller = request.Caller,
            CallerAccessToken = ResolveCallerAccessToken(request),
            ClientId = credential.Value.ClientId,
            ClientSecret = credential.Value.ClientSecret,
            // Three fields, filled here and nowhere else. The member formats no user name and knows
            // no naming rule — it applies what it is given, to the one database it is named.
            DatabaseName = databaseCredential.Value.DatabaseName,
            DatabaseUser = databaseCredential.Value.User,
            DatabasePassword = databaseCredential.Value.Password,
            GrantedAtUtc = grantedAt,
            ExpiresAtUtc = grantedAt + (request.Ttl ?? ILeaseService.DefaultLeaseTtl)
        };

        var member = _connectionManager.TryClaimMember(lenderTenantId, adapterPoolRtId.ToString(), lease);
        if (member is null)
        {
            // Nothing is parked here. A caller driving this by hand is told plainly; the scheduler
            // (increment 7) leaves the work item Queued, which is where the queue actually lives.
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.NoIdleMember,
                $"No idle member of adapter pool {adapterPoolRtId} in tenant '{lenderTenantId}' is connected to this " +
                "controller instance.");
        }

        if (admissionGate is not null)
        {
            bool admitted;
            try
            {
                admitted = await admissionGate(lease, member, cancellationToken);
            }
            catch (Exception e)
            {
                // A gate that threw decided nothing, so the member must go back — otherwise the
                // pool quietly loses a member per failed round until it has none left.
                _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
                Logger.Warn(e,
                    "The admission gate for lease '{LeaseId}' of tenant '{BorrowerTenantId}' threw; the member " +
                    "reservation was undone",
                    lease.LeaseId, lease.TenantId);
                return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.AdmissionGateFailed,
                    $"The lease admission gate failed: {e.Message}");
            }

            if (!admitted)
            {
                _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
                return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.AdmissionGateDeclined,
                    $"The work item '{lease.ExecutionId}' of tenant '{lease.TenantId}' was no longer available " +
                    "when the member was reserved; it was taken by another controller instance or cancelled.");
            }
        }

        try
        {
            await _hubContext.Clients.Client(member.ConnectionId)
                .SendAsync(nameof(IAdapterPoolHubCallbacks.LeaseAsync), lease, cancellationToken);
        }
        catch (Exception e)
        {
            // 🔴 Undo the claim. A member left marked busy for a lease it never received is a member
            // that never takes work again — the failure mode is a pool that silently shrinks to zero
            // usable members while every entity still says Deployed.
            _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
            Logger.Warn(e,
                "Failed to push lease '{LeaseId}' to pool member '{MemberId}' (connection '{ConnectionId}'); " +
                "the claim was undone and the member stays available",
                lease.LeaseId, member.MemberId, member.ConnectionId);
            return Refuse(lenderTenantId, adapterPoolRtId, request, LeaseRefusalReason.MemberDispatchFailed,
                $"Pool member '{member.MemberId}' could not be handed the lease: {e.Message}");
        }

        // 🔴 The lease object is never logged as a whole and NEITHER secret is ever named — the
        // borrower's client secret nor its database password. LeaseDto overrides ToString for the same
        // reason, but a log statement that interpolated a secret explicitly would defeat that, so the
        // fields are listed one by one, on purpose. The database it opens and the user it opens it as
        // are identities and are named: without them a cross-tenant read could not be recognised from
        // a lease log line at all.
        Logger.Info(
            "Granted lease '{LeaseId}' of pool {AdapterPoolRtId} (tenant '{AdapterPoolTenantId}') to tenant '{BorrowerTenantId}' " +
            "on member '{MemberId}', database '{DatabaseName}' as '{DatabaseUser}', expires {ExpiresAtUtc:O}",
            lease.LeaseId, lease.AdapterPoolRtId, lease.AdapterPoolTenantId, lease.TenantId, member.MemberId,
            lease.DatabaseName, lease.DatabaseUser, lease.ExpiresAtUtc);

        AdapterLeasingMetrics.RecordGranted(lease.TenantId, lenderTenantId, adapterPoolRtId.ToString());

        return new LeaseGrantResult(true, lease.LeaseId, member.MemberId, null);
    }

    /// <summary>
    ///     Counts a refusal and returns it (AB#4924 increment 9, plan §11).
    /// </summary>
    /// <remarks>
    ///     Every refusal path goes through here so that a reason added later cannot be forgotten on
    ///     the metric — the enum argument is what makes leaving it out a compile error rather than a
    ///     silently missing series.
    /// </remarks>
    private static LeaseGrantResult Refuse(string lenderTenantId, OctoObjectId adapterPoolRtId, LeaseRequest request,
        LeaseRefusalReason reason, string message)
    {
        AdapterLeasingMetrics.RecordRefused(request.BorrowerTenantId, lenderTenantId, adapterPoolRtId.ToString(),
            LeaseStage.Grant, reason);
        return LeaseGrantResult.Refused(reason, message);
    }

    /// <inheritdoc />
    public async Task ReleaseLeaseAsync(string connectionId, LeaseResultDto result,
        string? connectionTenantId = null, bool requireConnectionTenant = false)
    {
        // 🔴 AB#5864 — a Drained release is the member saying "I take no further lease" (the DTO's
        // contract). Before this, the controller logged the reason and nothing else: the registry
        // kept the member available, the next round granted it the next lease, the member refused
        // it in under a second, and that refusal FAILED the borrower's execution — for every grant,
        // for as long as the process lived. Recorded first and independently of the release below:
        // the member's drain flag never resets, so even a stale Drained release is true about the
        // process, and marking it before the lease is freed means there is no instant in which it
        // is both idle and not draining.
        if (result.Reason == LeaseReleaseReasonDto.Drained)
        {
            RecordMemberSelfDrain(connectionId, result);
        }

        var released = _connectionManager.ReleaseLease(connectionId, result.LeaseId);
        if (released is null)
        {
            // 🔴 AB#5826 — a lease this connection does not hold used to end here, silently: after a
            // controller restart (or a reconnect) the member's release named a lease nobody knew, the
            // execution stayed Running, and the stuck reaper failed it half an hour later with the
            // result lost. It is now attributed if it provably belongs to the member, and logged
            // either way. A release naming a DIFFERENT lease than the one this connection holds is
            // still never applied to that lease (the connection manager warned about it).
            released = await ResolveUnheldReleaseAsync(connectionId, result, connectionTenantId,
                requireConnectionTenant);
            if (released is null)
            {
                return;
            }
        }
        else
        {
            Logger.Info(
                "Lease '{LeaseId}' of tenant '{BorrowerTenantId}' released by its member: {Reason}, success={Success}",
                released.LeaseId, released.TenantId, result.Reason, result.Success);
        }

        // 🔴 AB#4924 increment 9 — the amortisation triple (plan §11). Held comes from this
        // controller's clock, work from the member's; the difference is the per-lease warm-up
        // concept §2.3 exists to make measurable. Recorded BEFORE the outcome is applied, so a
        // repository failure below cannot cost the sample.
        AdapterLeasingMetrics.RecordReleased(released.TenantId, released.AdapterPoolTenantId, released.AdapterPoolRtId,
            ReleaseReasonTag(result.Reason), result.Success,
            DateTime.UtcNow - released.GrantedAtUtc,
            result.WorkDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

        string? requeuedExecutionId = null;
        if (IsRefusedWithoutRunning(result))
        {
            // AB#5864 — the member never ran the work item, so there is no outcome to apply: the
            // work goes back to the queue, at the position it held, instead of failing.
            requeuedExecutionId = await ReturnRefusedWorkToQueueAsync(released, result.StatusMessage);
        }
        else
        {
            await ApplyLeaseOutcomeAsync(released, result.Success, result.StatusMessage, result.OutputData);

            // A Drained release that DID run the work item reports that work item's own outcome
            // (AB#5864, member side), so a failure here is the pipeline's and belongs in the
            // borrower's event log exactly like a Failed release.
            if (!result.Success)
            {
                await _eventService.StoreErrorEventAsync(released.TenantId,
                    $"A leased execution on adapter pool {released.AdapterPoolRtId} of tenant '{released.AdapterPoolTenantId}' " +
                    $"failed: {result.StatusMessage ?? "no detail reported"}");
            }
        }

        // 🔴 AB#4924 §9.6 — a member just became available. Without this the next grant waits for the
        // scheduler's tick however short the work was, which is what capped a member near twelve
        // executions a minute. Last in the method on purpose: the member is only really free once the
        // outcome above has been applied, and a round that started earlier could otherwise grant it
        // a second lease while the first one's execution was still being written.
        //
        // Requested for a member that is now idle, NOT for a drain: a draining member takes no
        // further work, so a round on its account would read every borrower's queue for nothing —
        // UNLESS the drain handed work back to the queue (AB#5864). Then another member may well be
        // idle, and the returned item must not wait a tick for it.
        if (result.Reason != LeaseReleaseReasonDto.Drained || requeuedExecutionId is not null)
        {
            _wakeSignal.RequestRound(requeuedExecutionId is null
                ? $"lease '{released.LeaseId}' released by its member"
                : $"lease '{released.LeaseId}' refused by a draining member; work re-queued as '{requeuedExecutionId}'");
        }
    }

    /// <summary>
    ///     AB#5864 — whether a release hands back a lease whose work item never ran.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Drained, unsuccessful and no work span: the member was already draining when the lease
    ///         arrived and refused it up front (<c>AdapterPoolClient.LeaseAsync</c>), or a participant
    ///         failed while entering the lease and the unwind drained the member. Either way the
    ///         pipeline did not run, so re-queuing it cannot run anything twice.
    ///     </para>
    ///     <para>
    ///         🔴 <c>WorkDurationMs</c> is the discriminator because the member stamps it around the
    ///         work item and nowhere else (increment 9). A Drained release <b>with</b> a work span ran
    ///         the pipeline and reports its outcome; re-queuing that would be a second run of work that
    ///         already happened. A <c>Failed</c> release without a work span is deliberately NOT
    ///         treated as a refusal — that is a borrower whose lease could not be entered (credential,
    ///         CK model), which fails the same way every time and must surface rather than loop.
    ///     </para>
    /// </remarks>
    private static bool IsRefusedWithoutRunning(LeaseResultDto result) =>
        result is { Reason: LeaseReleaseReasonDto.Drained, Success: false, WorkDurationMs: null };

    /// <summary>
    ///     AB#5864 — marks the member that reported a drain as draining in the registry, so no
    ///     further lease is granted to it, and records the transition once.
    /// </summary>
    private void RecordMemberSelfDrain(string connectionId, LeaseResultDto result)
    {
        if (!_connectionManager.MarkDraining(connectionId))
        {
            return;
        }

        var member = _connectionManager.TryGetMember(connectionId);
        if (member is null)
        {
            return;
        }

        AdapterLeasingMetrics.RecordMemberDrained(member.AdapterPoolTenantId, member.AdapterPoolRtId,
            LeaseDrainReason.MemberReported);

        Logger.Warn(
            "Pool member '{MemberId}' of pool {AdapterPoolRtId} (tenant '{AdapterPoolTenantId}') reported that it is " +
            "draining (release of lease '{LeaseId}': {StatusMessage}). It is offered no further lease; the member " +
            "exits once idle and the pool workload restarts it as a fresh process",
            member.MemberId, member.AdapterPoolRtId, member.AdapterPoolTenantId, result.LeaseId,
            result.StatusMessage ?? "no detail reported");
    }

    /// <summary>
    ///     AB#5864 — puts the work item of a refused lease back into the queue at the position it held,
    ///     or fails it once it has been refused <see cref="MaxRefusedLeaseRequeues" /> times in a row.
    /// </summary>
    /// <returns>The execution id of the re-queued attempt, or null when nothing was re-queued.</returns>
    private async Task<string?> ReturnRefusedWorkToQueueAsync(LeaseDto lease, string? statusMessage)
    {
        if (string.IsNullOrWhiteSpace(lease.ExecutionId))
        {
            // A hand-driven lease with no work item behind it: nothing to return.
            return null;
        }

        var refusals = TakeRefusalCount(lease.ExecutionId) + 1;
        var detail = statusMessage ?? "no detail reported";

        if (refusals > MaxRefusedLeaseRequeues)
        {
            // 🔴 The loop guard. With the registry fix above a draining member is never granted
            // again, so a refusal needs a grant that raced a drain — but "needs a race" is not a
            // bound. Past the cap the item fails loudly instead of circling.
            var message =
                $"The work item was refused by {refusals} adapter pool members in a row without running " +
                $"(last: {detail}); it is not re-queued again.";
            await ApplyLeaseOutcomeAsync(lease, false, message);
            await _eventService.StoreErrorEventAsync(lease.TenantId,
                $"Leased execution '{lease.ExecutionId}' on adapter pool {lease.AdapterPoolRtId} of tenant " +
                $"'{lease.AdapterPoolTenantId}' failed: {message}");
            return null;
        }

        var requeuedExecutionId = await InterruptAndEnqueueAsync(lease, LeaseInterruptReason.MemberRefused,
            $"The adapter pool member refused this lease without running it ({detail}); the work item was " +
            "returned to its place in the queue.",
            keepQueuePosition: true);

        if (requeuedExecutionId is null)
        {
            return null;
        }

        RememberRefusalCount(requeuedExecutionId, refusals);

        await _eventService.StoreInformationEventAsync(lease.TenantId,
            $"An adapter pool member of pool {lease.AdapterPoolRtId} (tenant '{lease.AdapterPoolTenantId}') refused " +
            $"the lease for execution '{lease.ExecutionId}' without running it because it is draining; the work " +
            $"was re-queued at its original position as execution '{requeuedExecutionId}'.");

        return requeuedExecutionId;
    }

    /// <summary>Removes and returns how often the chain behind <paramref name="executionId" /> was refused.</summary>
    private int TakeRefusalCount(string executionId)
    {
        return _refusalCounts.TryRemove(executionId, out var entry) ? entry.Count : 0;
    }

    /// <summary>
    ///     Carries the refusal count of a chain onto its newest attempt. In memory and per controller
    ///     process on purpose: the count only has to stop a loop, a restart that forgets it costs at
    ///     most another <see cref="MaxRefusedLeaseRequeues" /> refusals, and a CK attribute for it
    ///     would be a System.Communication bump for a guard.
    /// </summary>
    private void RememberRefusalCount(string executionId, int count)
    {
        var now = DateTime.UtcNow;
        _refusalCounts[executionId] = (count, now);

        // Pruned by age on the write path: an attempt that was granted and ran normally never comes
        // back through here, so without this its entry would live for the life of the process.
        foreach (var (key, entry) in _refusalCounts)
        {
            if (now - entry.RecordedAtUtc > RefusalCountRetention)
            {
                _refusalCounts.TryRemove(key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async Task HandleMemberDisconnectedAsync(PoolMemberConnection member)
    {
        if (member.ActiveLease is null)
        {
            return;
        }

        var lease = member.ActiveLease;

        if (_reconnectGrace > TimeSpan.Zero)
        {
            // 🔴 AB#5826 — held back, not re-queued. The member is very likely still running the work
            // item and back within seconds; re-queuing now is what ran the same work twice. It either
            // resumes the lease, reports its release, registers again without it (then it is
            // re-queued at once), or stays away past the grace (SweepLostLeasesAsync re-queues it).
            _lostLeases[lease.LeaseId] = new LostLease(member, lease, DateTime.UtcNow + _reconnectGrace);
            Logger.Warn(
                "Pool member '{MemberId}' disconnected while holding lease '{LeaseId}' of tenant '{BorrowerTenantId}' " +
                "(execution '{ExecutionId}'); holding the work for up to {GraceSeconds}s for the member to resume the " +
                "lease or report its outcome before it is interrupted and re-queued",
                member.MemberId, lease.LeaseId, lease.TenantId, lease.ExecutionId, _reconnectGrace.TotalSeconds);
            return;
        }

        await InterruptLostLeaseAsync(member, lease,
            $"The adapter pool member '{member.MemberId}' disconnected while holding this execution's lease.");
    }

    /// <inheritdoc />
    public async Task<int> SweepLostLeasesAsync(DateTime nowUtc)
    {
        var interrupted = 0;

        foreach (var entry in _lostLeases)
        {
            if (entry.Value.DeadlineUtc > nowUtc)
            {
                continue;
            }

            // Conditional on the exact entry: a resumption or a late release that took it in the
            // meantime wins, and the work is not re-queued behind its back.
            if (!_lostLeases.TryRemove(entry))
            {
                continue;
            }

            await InterruptLostLeaseAsync(entry.Value.Member, entry.Value.Lease,
                $"The adapter pool member '{entry.Value.Member.MemberId}' disconnected while holding this " +
                $"execution's lease and did not come back within {_reconnectGrace.TotalSeconds:0}s.");
            interrupted++;
        }

        return interrupted;
    }

    /// <inheritdoc />
    public async Task<int> ReleaseLostLeasesOfMemberAsync(string memberId, string adapterPoolTenantId,
        string adapterPoolRtId, string? exceptLeaseId = null)
    {
        var interrupted = 0;

        foreach (var entry in _lostLeases)
        {
            var lost = entry.Value;
            if (!string.Equals(lost.Member.MemberId, memberId, StringComparison.Ordinal) ||
                !string.Equals(lost.Member.AdapterPoolTenantId, adapterPoolTenantId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(lost.Member.AdapterPoolRtId, adapterPoolRtId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lost.Lease.LeaseId, exceptLeaseId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_lostLeases.TryRemove(entry))
            {
                continue;
            }

            // The member is back and says it does not run this lease (any outcome it had was
            // reported before it registered), so there is nothing left to wait for.
            await InterruptLostLeaseAsync(lost.Member, lost.Lease,
                $"The adapter pool member '{memberId}' disconnected while holding this execution's lease and " +
                "registered again without it.");
            interrupted++;
        }

        return interrupted;
    }

    /// <inheritdoc />
    public async Task<LeaseResumption> ResumeLeaseAsync(string memberId, string adapterPoolTenantId,
        string adapterPoolRtId, PoolMemberActiveLeaseDto activeLease, LeaseDto? transferredLease = null)
    {
        // 1. The registry still had it under the member's previous connection.
        if (transferredLease is not null &&
            string.Equals(transferredLease.LeaseId, activeLease.LeaseId, StringComparison.Ordinal))
        {
            Logger.Info(
                "Pool member '{MemberId}' resumed lease '{LeaseId}' of tenant '{BorrowerTenantId}' on a new connection",
                memberId, activeLease.LeaseId, activeLease.TenantId);
            return new LeaseResumption(transferredLease, true, null);
        }

        // 2. It was held back after the member disconnected (this instance noticed the disconnect).
        if (_lostLeases.TryGetValue(activeLease.LeaseId, out var lost) &&
            string.Equals(lost.Member.MemberId, memberId, StringComparison.Ordinal) &&
            _lostLeases.TryRemove(new KeyValuePair<string, LostLease>(activeLease.LeaseId, lost)))
        {
            Logger.Info(
                "Pool member '{MemberId}' came back within the reconnect grace and resumed lease '{LeaseId}' of " +
                "tenant '{BorrowerTenantId}'; nothing is re-queued",
                memberId, activeLease.LeaseId, activeLease.TenantId);
            return new LeaseResumption(lost.Lease, true, null);
        }

        // 3. This instance never knew the lease (it restarted, or the member came from another
        //    instance): the persisted execution decides.
        if (string.IsNullOrWhiteSpace(activeLease.ExecutionId))
        {
            // A hand-driven lease carries no work item, so there is nothing to prove or to complete;
            // the member is simply busy.
            return new LeaseResumption(BuildLease(activeLease.LeaseId, activeLease.TenantId, adapterPoolTenantId,
                adapterPoolRtId, activeLease.AdapterRtId, activeLease.AdapterCkTypeId, string.Empty,
                activeLease.GrantedAtUtc, activeLease.ExpiresAtUtc), true, null);
        }

        var (execution, refusal) = await VerifyLeasedExecutionAsync(activeLease.TenantId, activeLease.ExecutionId,
            memberId, adapterPoolTenantId, adapterPoolRtId);
        if (execution is not null)
        {
            Logger.Warn(
                "Adopted lease '{LeaseId}' of tenant '{BorrowerTenantId}' (execution '{ExecutionId}') from pool member " +
                "'{MemberId}': this controller instance did not hold it (it restarted, or the member moved here), and " +
                "the execution proves it was leased to this member and is still running",
                activeLease.LeaseId, activeLease.TenantId, activeLease.ExecutionId, memberId);
            return new LeaseResumption(BuildLease(activeLease.LeaseId, activeLease.TenantId, adapterPoolTenantId,
                adapterPoolRtId, activeLease.AdapterRtId, activeLease.AdapterCkTypeId, activeLease.ExecutionId,
                execution.LeaseGrantedAt ?? activeLease.GrantedAtUtc, activeLease.ExpiresAtUtc), true, null);
        }

        // Not adopted. The member is still busy with it, so it is recorded with a lease that names no
        // execution: no second lease lands on a process that has to refuse it, and the release frees
        // the member without touching an execution that has moved on.
        var message =
            $"Lease '{activeLease.LeaseId}' was not adopted: execution '{activeLease.ExecutionId}' of tenant " +
            $"'{activeLease.TenantId}' {refusal}. Its outcome will not be applied.";
        Logger.Warn("Pool member '{MemberId}' resumed a lease that cannot be adopted. {Message}", memberId, message);
        return new LeaseResumption(BuildLease(activeLease.LeaseId, activeLease.TenantId, adapterPoolTenantId,
            adapterPoolRtId, activeLease.AdapterRtId, activeLease.AdapterCkTypeId, string.Empty,
            activeLease.GrantedAtUtc, activeLease.ExpiresAtUtc), false, message);
    }

    /// <summary>
    ///     AB#5826 — the lease a release names when the connection it arrived on does not hold it, or
    ///     null when it cannot be attributed (logged).
    /// </summary>
    /// <remarks>
    ///     Three places, in order of how strong the proof is:
    ///     <list type="number">
    ///         <item><description>held back after the member disconnected — matched by lease id;</description></item>
    ///         <item><description>still registered under the member's previous connection — matched by lease id;</description></item>
    ///         <item><description>
    ///             nowhere in this instance (it restarted) — matched against the persisted execution,
    ///             which must be running and leased to this member of this pool.
    ///         </description></item>
    ///     </list>
    ///     A lease id is a random 128-bit value sent to one member over its own connection, so the
    ///     first two are a capability proof. The third has only the member id to go on, which is why it
    ///     also requires the execution to be still running with no release stamped: an outcome is never
    ///     written over an execution that has moved on.
    /// </remarks>
    private async Task<LeaseDto?> ResolveUnheldReleaseAsync(string connectionId, LeaseResultDto result,
        string? connectionTenantId, bool requireConnectionTenant)
    {
        var registered = _connectionManager.TryGetMember(connectionId);
        if (registered is not null && !string.IsNullOrWhiteSpace(result.MemberId) &&
            !string.Equals(registered.MemberId, result.MemberId, StringComparison.Ordinal))
        {
            Logger.Warn(
                "Ignoring a release of lease '{LeaseId}' on connection '{ConnectionId}': it names member '{ReleasingMemberId}' " +
                "but the connection is registered as member '{MemberId}'",
                result.LeaseId, connectionId, result.MemberId, registered.MemberId);
            return null;
        }

        var memberId = registered?.MemberId ?? result.MemberId;

        if (_lostLeases.TryGetValue(result.LeaseId, out var lost) &&
            (memberId is null || string.Equals(lost.Member.MemberId, memberId, StringComparison.Ordinal)) &&
            IsLateReleaseBindingAcceptable(result, lost.Lease.AdapterPoolTenantId, connectionTenantId,
                requireConnectionTenant) &&
            _lostLeases.TryRemove(new KeyValuePair<string, LostLease>(result.LeaseId, lost)))
        {
            Logger.Warn(
                "Lease '{LeaseId}' of tenant '{BorrowerTenantId}' released by its member '{MemberId}' after it had " +
                "disconnected: {Reason}, success={Success}. The work was held back for the reconnect grace and is not re-queued",
                lost.Lease.LeaseId, lost.Lease.TenantId, lost.Member.MemberId, result.Reason, result.Success);
            return lost.Lease;
        }

        var holder = _connectionManager.FindMemberHoldingLease(result.LeaseId);
        if (holder is not null && holder.ConnectionId != connectionId &&
            (memberId is null || string.Equals(holder.MemberId, memberId, StringComparison.Ordinal)) &&
            IsLateReleaseBindingAcceptable(result, holder.AdapterPoolTenantId, connectionTenantId,
                requireConnectionTenant))
        {
            var taken = _connectionManager.ReleaseLease(holder.ConnectionId, result.LeaseId);
            if (taken is not null)
            {
                Logger.Warn(
                    "Lease '{LeaseId}' of tenant '{BorrowerTenantId}' released by member '{MemberId}' on connection " +
                    "'{ConnectionId}', while it was still registered under its previous connection '{PreviousConnectionId}': " +
                    "{Reason}, success={Success}",
                    taken.LeaseId, taken.TenantId, holder.MemberId, connectionId, holder.ConnectionId, result.Reason,
                    result.Success);
                return taken;
            }
        }

        if (string.IsNullOrWhiteSpace(result.ExecutionId) || string.IsNullOrWhiteSpace(result.TenantId) ||
            string.IsNullOrWhiteSpace(memberId))
        {
            Logger.Warn(
                "Ignoring a release of lease '{LeaseId}' on connection '{ConnectionId}' ({Reason}, success={Success}): " +
                "this controller instance holds no such lease, and the release names no execution to check it against " +
                "(the member predates AB#5826). If the controller restarted while the lease ran, the execution stays " +
                "Running until the stuck-execution reaper fails it",
                result.LeaseId, connectionId, result.Reason, result.Success);
            return null;
        }

        var (execution, refusal) = await VerifyLeasedExecutionAsync(result.TenantId, result.ExecutionId, memberId,
            registered?.AdapterPoolTenantId, registered?.AdapterPoolRtId);
        if (execution is null)
        {
            Logger.Warn(
                "Ignoring a late release of lease '{LeaseId}' for execution '{ExecutionId}' of tenant '{BorrowerTenantId}' " +
                "from member '{MemberId}' ({Reason}, success={Success}): the execution {Refusal}",
                result.LeaseId, result.ExecutionId, result.TenantId, memberId, result.Reason, result.Success, refusal);
            return null;
        }

        if (!IsLateReleaseBindingAcceptable(result, execution.LeasedFromTenantId, connectionTenantId,
                requireConnectionTenant))
        {
            return null;
        }

        Logger.Warn(
            "Applying a late release of lease '{LeaseId}' to execution '{ExecutionId}' of tenant '{BorrowerTenantId}' " +
            "({Reason}, success={Success}): this controller instance did not hold the lease (it restarted, or the " +
            "member reconnected to it), and the execution proves it was leased to member '{MemberId}' and is still running",
            result.LeaseId, result.ExecutionId, result.TenantId, result.Reason, result.Success, memberId);

        return BuildLease(result.LeaseId, result.TenantId, execution.LeasedFromTenantId ?? string.Empty,
            execution.LeasedFromAdapterPoolRtId ?? string.Empty, string.Empty, string.Empty, result.ExecutionId,
            execution.LeaseGrantedAt ?? execution.StartedAt ?? DateTime.UtcNow, default);
    }

    /// <summary>
    ///     AB#5826 — whether the persisted execution proves that a lease of this member is still in
    ///     flight on it, or why not.
    /// </summary>
    private async Task<(RtPipelineExecution? Execution, string? Refusal)> VerifyLeasedExecutionAsync(
        string tenantId, string executionId, string memberId, string? adapterPoolTenantId, string? adapterPoolRtId)
    {
        RtPipelineExecution? execution;
        try
        {
            execution = await _communicationRepository.GetPipelineExecutionAsync(tenantId, executionId);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{BorrowerTenantId}] Could not read execution '{ExecutionId}' to attribute a lease",
                tenantId, executionId);
            return (null, $"could not be read ({e.Message})");
        }

        if (execution is null)
        {
            return (null, "does not exist");
        }

        if (execution.Status != RtPipelineExecutionStatusEnum.Running)
        {
            return (null, $"is {execution.Status} already (re-queued after the member was lost, expired, or completed)");
        }

        if (execution.LeaseReleasedAt is not null)
        {
            return (null, "has its lease released already");
        }

        if (!string.Equals(execution.LeasedOnMemberId, memberId, StringComparison.Ordinal))
        {
            return (null, $"was leased to member '{execution.LeasedOnMemberId ?? "<none>"}', not to '{memberId}'");
        }

        if (adapterPoolTenantId is not null &&
            !string.Equals(execution.LeasedFromTenantId, adapterPoolTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"was leased from tenant '{execution.LeasedFromTenantId}', not from '{adapterPoolTenantId}'");
        }

        if (adapterPoolRtId is not null &&
            !string.Equals(execution.LeasedFromAdapterPoolRtId, adapterPoolRtId, StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"was leased from pool {execution.LeasedFromAdapterPoolRtId}, not from {adapterPoolRtId}");
        }

        return (execution, null);
    }

    /// <summary>
    ///     AB#5826 — the pool hub's tenant binding, applied to a release that is not covered by a
    ///     registration on its connection. Same staging as the registration gate: a mismatch is refused
    ///     only when the gate enforces, and logged otherwise.
    /// </summary>
    private static bool IsLateReleaseBindingAcceptable(LeaseResultDto result, string? lenderTenantId,
        string? connectionTenantId, bool requireConnectionTenant)
    {
        if (string.IsNullOrEmpty(connectionTenantId))
        {
            if (requireConnectionTenant)
            {
                Logger.Warn(
                    "Refusing a late release of lease '{LeaseId}': the connection presents no tenant-bound token",
                    result.LeaseId);
                return false;
            }

            return true;
        }

        if (string.Equals(connectionTenantId, lenderTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Logger.Warn(
            "A late release of lease '{LeaseId}' arrives on a connection with a token of tenant '{ConnectionTenantId}', " +
            "but the lease belongs to a pool of tenant '{LenderTenantId}'{Outcome}",
            result.LeaseId, connectionTenantId, lenderTenantId,
            requireConnectionTenant ? "; refused" : " and would be refused when AdapterPoolHubAuthorization:Mode is Enforce");
        return !requireConnectionTenant;
    }

    /// <summary>
    ///     AB#5826 — a lease rebuilt from identifiers. It carries no secret and no work: it only has to
    ///     record that the member is busy and let the release complete the execution.
    /// </summary>
    private LeaseDto BuildLease(string leaseId, string tenantId, string adapterPoolTenantId, string adapterPoolRtId,
        string adapterRtId, string adapterCkTypeId, string executionId, DateTime grantedAtUtc, DateTime expiresAtUtc)
    {
        var granted = grantedAtUtc == default ? DateTime.UtcNow : grantedAtUtc;
        return new LeaseDto
        {
            LeaseId = leaseId,
            TenantId = tenantId,
            AdapterPoolTenantId = adapterPoolTenantId,
            AdapterPoolRtId = adapterPoolRtId,
            AdapterRtId = adapterRtId,
            AdapterCkTypeId = adapterCkTypeId,
            ExecutionId = executionId,
            GrantedAtUtc = granted,
            ExpiresAtUtc = expiresAtUtc > granted ? expiresAtUtc : granted + _leaseTtl
        };
    }

    /// <summary>
    ///     Concept §6: at-least-once. The attempt is marked Interrupted with its lease span closed, and
    ///     a fresh attempt takes its place in the queue.
    /// </summary>
    private async Task InterruptLostLeaseAsync(PoolMemberConnection member, LeaseDto lease, string reason)
    {
        Logger.Warn(
            "Pool member '{MemberId}' lost lease '{LeaseId}' of tenant '{BorrowerTenantId}'; the work item is interrupted",
            member.MemberId, lease.LeaseId, lease.TenantId);

        var requeuedExecutionId = await InterruptAndRequeueAsync(lease, LeaseInterruptReason.MemberLost, reason);

        await _eventService.StoreErrorEventAsync(lease.TenantId,
            $"The adapter pool member '{member.MemberId}' holding this tenant's lease " +
            $"(pool {lease.AdapterPoolRtId} of tenant '{lease.AdapterPoolTenantId}') disconnected before releasing it. " +
            "Any pipeline execution it was running is interrupted" +
            (requeuedExecutionId is null
                ? "."
                : $" and was re-queued as execution '{requeuedExecutionId}'."));
    }

    /// <inheritdoc />
    public async Task ApplyLeaseOutcomeAsync(LeaseDto lease, bool success, string? statusMessage,
        string? outputData = null)
    {
        if (string.IsNullOrWhiteSpace(lease.ExecutionId))
        {
            // A hand-driven lease with no work item behind it. Nothing to stamp, and inventing an
            // execution to stamp it on would be worse than doing nothing.
            return;
        }

        var releasedAt = DateTime.UtcNow;

        try
        {
            await _communicationRepository.StampLeaseReleasedAsync(lease.TenantId, lease.ExecutionId, releasedAt);

            // 🔴 The lease release is the controller's LAST resort for completing the execution, not
            // its first. An execution the member already reported through the normal adapter path is
            // terminal by now and is left exactly as it is; one that is still Running when the
            // member hands the process back would otherwise stay Running forever and be reaped as
            // stuck fifteen minutes later, with a message about an adapter restart that never
            // happened.
            var execution = await _communicationRepository.GetPipelineExecutionAsync(lease.TenantId,
                lease.ExecutionId);
            if (execution is null || execution.Status != RtPipelineExecutionStatusEnum.Running)
            {
                return;
            }

            var completedAt = DateTime.UtcNow;
            var durationMs = execution.StartedAt is { } startedAt
                ? (int)Math.Max(0, Math.Round((completedAt - startedAt).TotalMilliseconds))
                : 0;

            var outcome = success ? RtPipelineExecutionStatusEnum.Completed : RtPipelineExecutionStatusEnum.Failed;
            await _communicationRepository.UpdatePipelineExecutionAsync(lease.TenantId, lease.ExecutionId,
                outcome,
                completedAt, durationMs,
                success ? null : statusMessage ?? "The leased execution failed without a reported reason.",
                // 🔴 The release is the ONLY route a leased execution's output has. A dedicated adapter
                // reports it on IAdapterHub.ReportExecutionEndAsync, whose tenant and adapter come from
                // the connection - a pool member holds a tenant-free management channel and has no such
                // connection to report on (AB#4924 §9.9 / D4).
                outputData);

            // AB#5425 on the 0.2 lane: a dedicated adapter's outcome is counted where it reports it
            // (PipelineExecutionService). A leased execution never takes that path — the release is its
            // only completion route — so without this it would be missing from
            // octo.pipeline.execution.count entirely. Counted after the write, like the adapter path.
            PipelineExecutionMetrics.RecordExecutionOutcome(lease.TenantId, outcome);
        }
        catch (Exception e)
        {
            // Never throws out of a hub method or a background sweep: a release that could not be
            // recorded must not also cost the member its connection.
            Logger.Warn(e,
                "[{BorrowerTenantId}] Could not apply the outcome of lease '{LeaseId}' to execution '{ExecutionId}'",
                lease.TenantId, lease.LeaseId, lease.ExecutionId);
        }
    }

    /// <inheritdoc />
    /// <summary>
    ///     AB#5279 — the invoker's token for the lease, or null. Stored encrypted on the queued
    ///     execution; decrypted here and handed to the member only while it is still valid. An
    ///     expired token would not make the run fail loudly at once — it would make every delegated
    ///     call inside it fail with 401 — so it is dropped up front and the run proceeds as the
    ///     caller without delegation, which is the documented shape of a lease without a token.
    /// </summary>
    private string? ResolveCallerAccessToken(LeaseRequest request)
    {
        if (string.IsNullOrEmpty(request.CallerAccessToken))
        {
            return null;
        }

        string token;
        try
        {
            token = _encryptionService.Decrypt(request.CallerAccessToken);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{BorrowerTenantId}] The invoker's token of queued execution '{ExecutionId}' could not be decrypted; " +
                "the lease carries the caller without delegation",
                request.BorrowerTenantId, request.ExecutionId);
            return null;
        }

        if (JwtExpiry.IsExpired(token, DateTime.UtcNow))
        {
            Logger.Info(
                "[{BorrowerTenantId}] The invoker's token of queued execution '{ExecutionId}' expired while the item " +
                "was waiting for a lease; the lease carries the caller without delegation",
                request.BorrowerTenantId, request.ExecutionId);
            return null;
        }

        return token;
    }

    public async Task<string?> InterruptAndRequeueAsync(LeaseDto lease, LeaseInterruptReason interruptReason,
        string reason)
    {
        // Counted even when there is no execution behind the lease: a member that died holding a
        // hand-driven lease is the same fault, and a counter that silently skipped those would
        // under-report exactly the mid-lease failures it exists to surface.
        AdapterLeasingMetrics.RecordInterrupted(lease.TenantId, lease.AdapterPoolTenantId, lease.AdapterPoolRtId,
            interruptReason, DateTime.UtcNow - lease.GrantedAtUtc);

        return await InterruptAndEnqueueAsync(lease, interruptReason, reason, keepQueuePosition: false);
    }

    /// <summary>
    ///     Marks the attempt <c>Interrupted</c> and enqueues a fresh one in its place.
    /// </summary>
    /// <param name="lease">The lease whose work item is re-queued.</param>
    /// <param name="interruptReason">The re-queue counter's label.</param>
    /// <param name="reason">Human-readable explanation, stored on the interrupted attempt.</param>
    /// <param name="keepQueuePosition">
    ///     🔴 AB#5864 — true for a lease the member refused without running it: the retry keeps the
    ///     original <c>QueuedAt</c>, so it is served in the order (and inside its tenant, with the
    ///     class) it had before. The work never left the queue in any sense a borrower can see, and
    ///     a refusal must not send it to the back. False for an attempt that ran (TTL, member lost),
    ///     whose retry gets its own <c>QueuedAt</c> and therefore its own honest <c>LeaseWaitMs</c>.
    /// </param>
    private async Task<string?> InterruptAndEnqueueAsync(LeaseDto lease, LeaseInterruptReason interruptReason,
        string reason, bool keepQueuePosition)
    {
        if (string.IsNullOrWhiteSpace(lease.ExecutionId))
        {
            return null;
        }

        try
        {
            var interrupted = await _communicationRepository.TryInterruptLeasedExecutionAsync(lease.TenantId,
                lease.ExecutionId, DateTime.UtcNow, reason);
            if (interrupted is null)
            {
                return null;
            }

            var retryExecutionId = Guid.NewGuid().ToString();
            var retry = new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = retryExecutionId,
                TriggerType = interrupted.TriggerType,
                InputData = interrupted.InputData
            };
            // AB#5279: the retry is the same work item - same input, same invoker. The token is
            // copied as stored (still encrypted); the grant decides whether it is still usable.
            QueuedCaller.Apply(retry, interrupted.Caller, interrupted.CallerAccessToken);

            var queuedAt = keepQueuePosition && interrupted.QueuedAt is { } originalQueuedAt
                ? originalQueuedAt
                : DateTime.UtcNow;
            await _communicationRepository.EnqueueExecutionAsync(lease.TenantId, retry,
                interrupted.PipelineRtEntityId, interrupted.AdapterRtEntityId, queuedAt);

            AdapterLeasingMetrics.RecordRequeued(lease.TenantId, lease.AdapterPoolTenantId, lease.AdapterPoolRtId,
                interruptReason);

            Logger.Info(
                "[{BorrowerTenantId}] Interrupted leased execution '{ExecutionId}' and re-queued it as " +
                "'{RetryExecutionId}': {Reason}",
                lease.TenantId, lease.ExecutionId, retryExecutionId, reason);

            return retryExecutionId;
        }
        catch (Exception e)
        {
            Logger.Error(e,
                "[{BorrowerTenantId}] Could not interrupt and re-queue execution '{ExecutionId}' of lease '{LeaseId}'",
                lease.TenantId, lease.ExecutionId, lease.LeaseId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DrainMemberAsync(string connectionId, string reason)
    {
        // Local first, push second. A member that cannot be reached is exactly the member that must
        // not be handed another tenant, so the bookkeeping must not depend on the push succeeding.
        _connectionManager.MarkDraining(connectionId);

        try
        {
            await _hubContext.Clients.Client(connectionId)
                .SendAsync(nameof(IAdapterPoolHubCallbacks.DrainAsync));
            Logger.Info("Told pool member on connection '{ConnectionId}' to drain: {Reason}", connectionId, reason);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "Could not tell the pool member on connection '{ConnectionId}' to drain ({Reason}); it stays " +
                "marked draining on this controller and takes no further lease",
                connectionId, reason);
        }
    }

    /// <summary>
    ///     The metric label for a release reason, written out rather than taken from
    ///     <c>ToString</c>: renaming the DTO enum member for readability must not silently rename a
    ///     label every dashboard is built on.
    /// </summary>
    private static string ReleaseReasonTag(LeaseReleaseReasonDto reason) => reason switch
    {
        LeaseReleaseReasonDto.Completed => "completed",
        LeaseReleaseReasonDto.Failed => "failed",
        LeaseReleaseReasonDto.Drained => "drained",
        _ => "unknown"
    };

    /// <summary>
    ///     Whether adapter-pool leasing is switched on for <b>both</b> tenants, or the reason it is
    ///     not (AB#4924 §14).
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Whose switch it is.</b> Both tenants', and the flag means a different thing on each:
    ///     on the lender it says "this tenant's pools hand their members out", on the borrower "this
    ///     tenant's <c>Leased</c> adapters get scheduled". Lending is the lender's capability and
    ///     borrowing is the borrower's; neither tenant can assert the other's, so one <c>true</c> is
    ///     never enough. The read goes through <c>ILifecycleConfigurationService</c>, which caches for
    ///     30 s — the same window scale-to-zero has lived with since AB#4916, and the same trade: a
    ///     switch flipped off stops granting within half a minute rather than instantly, and the
    ///     already-granted lease was always going to run to its end anyway.
    /// </remarks>
    private async Task<(LeaseRefusalReason Reason, string Message)?> CheckLeasingEnabledAsync(string lenderTenantId,
        string borrowerTenantId)
    {
        if (!await _lifecycleConfiguration.IsLeasingEnabledAsync(lenderTenantId))
        {
            return (LeaseRefusalReason.LeasingDisabledLender,
                $"Adapter pool leasing is disabled for the lending tenant '{lenderTenantId}'. " +
                "Enable it with octo-cli SetCommunicationLifecycle -le true.");
        }

        if (!await _lifecycleConfiguration.IsLeasingEnabledAsync(borrowerTenantId))
        {
            return (LeaseRefusalReason.LeasingDisabledBorrower,
                $"Adapter pool leasing is disabled for the borrowing tenant '{borrowerTenantId}'. " +
                "Enable it with octo-cli SetCommunicationLifecycle -le true.");
        }

        return null;
    }

    /// <summary>
    ///     Reads the borrowing tenant's adapter, or null when it does not exist or is not an adapter.
    /// </summary>
    private async Task<RtAdapter?> ReadBorrowerAdapterAsync(string borrowerTenantId, OctoObjectId adapterRtId)
    {
        try
        {
            return await _communicationRepository.GetWorkloadByRtIdAsync(borrowerTenantId, adapterRtId)
                as RtAdapter;
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{BorrowerTenantId}] Could not read adapter {AdapterRtId} for a lease request",
                borrowerTenantId, adapterRtId);
            return null;
        }
    }

    /// <summary>
    ///     Whether the borrower itself declares that it borrows from exactly this pool, or the reason
    ///     it does not.
    /// </summary>
    /// <remarks>
    ///     🔴 This is the half that makes lending consensual. The lender's <c>SharingMode</c> says who
    ///     <i>may</i> borrow; without this check a lender could push its members' executions into any
    ///     descendant that never asked for it, and the borrower's own pipelines would run under an
    ///     identity it did not intend to hand out.
    /// </remarks>
    private static (LeaseRefusalReason Reason, string Message)? CheckBorrowerDeclaration(RtAdapter borrower,
        LentFromReference? borrowerLentFrom, string lenderTenantId, OctoObjectId adapterPoolRtId,
        LeaseRequest request)
    {
        if (borrower.LifecycleMode != RtLifecycleModeEnum.Leased)
        {
            return (LeaseRefusalReason.BorrowerNotLeased,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' is not Leased " +
                $"(LifecycleMode={borrower.LifecycleMode}); only a Leased adapter borrows a process.");
        }

        // 🔴 Read from the borrower's OWN mirror, never from the pool being asked for. This is the
        // half that makes lending consensual, so it has to come from the borrower's side of the
        // relationship — and an adapter that names no pool at all has declared nothing, which is a
        // refusal rather than a match against whatever pool happened to ask.
        if (!string.Equals(borrowerLentFrom?.LenderTenantId, lenderTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return (LeaseRefusalReason.BorrowerNamesAnotherLender,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from tenant " +
                $"'{borrowerLentFrom?.LenderTenantId ?? "<unset>"}', not from '{lenderTenantId}'.");
        }

        if (!string.Equals(borrowerLentFrom?.AdapterPoolRtId, adapterPoolRtId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return (LeaseRefusalReason.BorrowerNamesAnotherPool,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from pool " +
                $"{borrowerLentFrom?.AdapterPoolRtId ?? "<unset>"}, not from {adapterPoolRtId}.");
        }

        return null;
    }

    /// <summary>
    ///     The borrower's own <c>PipelineServiceAccount</c> credential (AB#5027), decrypted, or null
    ///     when the adapter has none usable.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Concept §8 Q6: the lease carries the <b>borrower's</b> credential, not the pool's. The
    ///         member then acts exactly as the borrower's own adapter would, so no standing grant is
    ///         created anywhere and the AB#5027 identity chain is reused unchanged. The alternative —
    ///         mirroring the pool's account into every borrower — would give the pool a permanent
    ///         credential in every borrower tenant even with no lease active, which is the opposite of
    ///         what leasing is for.
    ///     </para>
    ///     <para>
    ///         🔴 Read through <c>GetAttributeValueOrDefault</c> rather than the generated properties,
    ///         for the same reason <c>DeploymentSiteService.AppendPipelineServiceAccountOverridesAsync</c> does:
    ///         both attributes are mandatory on the CK type, so a generated getter throws on a
    ///         half-written entity. Here that must degrade to a refused lease with a named reason, not
    ///         to an exception out of a hub method.
    ///     </para>
    /// </remarks>
    private async Task<(string ClientId, string ClientSecret)?> ResolveBorrowerCredentialAsync(
        string borrowerTenantId, RtAdapter borrower)
    {
        RtServiceAccountConfiguration? serviceAccount;
        try
        {
            serviceAccount = await _serviceAccountResolver.GetAdapterDefaultAsync(borrowerTenantId, borrower.RtId);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{BorrowerTenantId}] Could not read the pipeline service account of adapter '{AdapterName}'",
                borrowerTenantId, borrower.Name);
            return null;
        }

        if (serviceAccount is null)
        {
            return null;
        }

        var clientId =
            serviceAccount.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientId)) as string;
        var clientSecret =
            serviceAccount.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientSecret)) as string;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            // Names the adapter and the fact, never the value. Same rule as the deploy path.
            Logger.Warn(
                "[{BorrowerTenantId}] The pipeline service account of adapter '{AdapterName}' is missing its " +
                "client id or secret; no lease can be granted for it",
                borrowerTenantId, borrower.Name);
            return null;
        }

        // Same lane as every other secret leaving this service: Decrypt is a no-op without the
        // `enc:v1:` sentinel, and the wire must carry plaintext either way.
        return (clientId!, _encryptionService.Decrypt(clientSecret!));
    }
}
