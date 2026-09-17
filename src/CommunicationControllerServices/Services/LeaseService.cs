using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
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
        ILeaseSchedulerWakeSignal wakeSignal)
    {
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
    public async Task<LeaseGrantResult> GrantLeaseAsync(string lenderTenantId, OctoObjectId poolRtId,
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
            return Refuse(lenderTenantId, poolRtId, request, leasingRefusal.Value.Reason,
                leasingRefusal.Value.Message);
        }

        // 🔴 The order of the three checks below is the order of least trust. The borrowing
        // relationship is checked BEFORE the credential is read, so a request naming an adapter it
        // has no business naming never reaches the code that decrypts a client secret.
        var borrower = await ReadBorrowerAdapterAsync(request.BorrowerTenantId, request.BorrowerAdapterRtId);
        if (borrower is null)
        {
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.BorrowerAdapterUnknown,
                $"Tenant '{request.BorrowerTenantId}' has no adapter with rtId {request.BorrowerAdapterRtId}.");
        }

        var declarationRefusal = CheckBorrowerDeclaration(borrower, lenderTenantId, poolRtId, request);
        if (declarationRefusal != null)
        {
            return Refuse(lenderTenantId, poolRtId, request, declarationRefusal.Value.Reason,
                declarationRefusal.Value.Message);
        }

        // The lender's half. Both halves are required and neither implies the other: the borrower
        // says "I borrow from that pool", the pool says "I lend into that part of the tree". A pool
        // that has been deleted or re-scoped since the borrower was deployed lands here (concept §6,
        // "parent tenant deleted while lending") rather than at deploy time.
        var lendingScope = await _communicationRepository
            .TryGetAdapterPoolLendingScopeAsync(lenderTenantId, poolRtId.ToString());
        if (lendingScope is null)
        {
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.PoolUnknown,
                $"Tenant '{lenderTenantId}' has no adapter pool with rtId {poolRtId}, or it cannot be read.");
        }

        if (!await _lendingScopeResolver.MayLendAsync(lenderTenantId, request.BorrowerTenantId,
                lendingScope.Value, cancellationToken))
        {
            // Audited, not only logged: a lease attempt across a boundary the tenant tree does not
            // allow is the shape a cross-tenant incident would take, and it must be visible in the
            // borrower's own event log rather than only in a controller pod's stdout.
            await _eventService.StoreErrorEventAsync(request.BorrowerTenantId,
                $"Refused a lease of adapter pool {poolRtId} in tenant '{lenderTenantId}': that pool does not " +
                "lend to this tenant. Check the pool's SharingMode and LendingAllowedTenantIds.");
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.LendingScopeDenied,
                $"Adapter pool {poolRtId} in tenant '{lenderTenantId}' does not lend to tenant " +
                $"'{request.BorrowerTenantId}'.");
        }

        var credential = await ResolveBorrowerCredentialAsync(request.BorrowerTenantId, borrower);
        if (credential is null)
        {
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.BorrowerCredentialMissing,
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
            return Refuse(lenderTenantId, poolRtId, request,
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
                return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.PipelineProjectionFailed,
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
            AdapterPoolRtId = poolRtId.ToString(),
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

        var member = _connectionManager.TryClaimMember(lenderTenantId, poolRtId.ToString(), lease);
        if (member is null)
        {
            // Nothing is parked here. A caller driving this by hand is told plainly; the scheduler
            // (increment 7) leaves the work item Queued, which is where the queue actually lives.
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.NoIdleMember,
                $"No idle member of adapter pool {poolRtId} in tenant '{lenderTenantId}' is connected to this " +
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
                return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.AdmissionGateFailed,
                    $"The lease admission gate failed: {e.Message}");
            }

            if (!admitted)
            {
                _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
                return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.AdmissionGateDeclined,
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
            return Refuse(lenderTenantId, poolRtId, request, LeaseRefusalReason.MemberDispatchFailed,
                $"Pool member '{member.MemberId}' could not be handed the lease: {e.Message}");
        }

        // 🔴 The lease object is never logged as a whole and NEITHER secret is ever named — the
        // borrower's client secret nor its database password. LeaseDto overrides ToString for the same
        // reason, but a log statement that interpolated a secret explicitly would defeat that, so the
        // fields are listed one by one, on purpose. The database it opens and the user it opens it as
        // are identities and are named: without them a cross-tenant read could not be recognised from
        // a lease log line at all.
        Logger.Info(
            "Granted lease '{LeaseId}' of pool {PoolRtId} (tenant '{PoolTenantId}') to tenant '{BorrowerTenantId}' " +
            "on member '{MemberId}', database '{DatabaseName}' as '{DatabaseUser}', expires {ExpiresAtUtc:O}",
            lease.LeaseId, lease.AdapterPoolRtId, lease.AdapterPoolTenantId, lease.TenantId, member.MemberId,
            lease.DatabaseName, lease.DatabaseUser, lease.ExpiresAtUtc);

        AdapterLeasingMetrics.RecordGranted(lease.TenantId, lenderTenantId, poolRtId.ToString());

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
    private static LeaseGrantResult Refuse(string lenderTenantId, OctoObjectId poolRtId, LeaseRequest request,
        LeaseRefusalReason reason, string message)
    {
        AdapterLeasingMetrics.RecordRefused(request.BorrowerTenantId, lenderTenantId, poolRtId.ToString(),
            LeaseStage.Grant, reason);
        return LeaseGrantResult.Refused(reason, message);
    }

    /// <inheritdoc />
    public async Task ReleaseLeaseAsync(string connectionId, LeaseResultDto result)
    {
        var released = _connectionManager.ReleaseLease(connectionId, result.LeaseId);
        if (released is null)
        {
            // Already covered by a warning inside the connection manager, which is the only place
            // that can tell "stale" from "unknown connection" apart.
            return;
        }

        Logger.Info(
            "Lease '{LeaseId}' of tenant '{BorrowerTenantId}' released by its member: {Reason}, success={Success}",
            released.LeaseId, released.TenantId, result.Reason, result.Success);

        // 🔴 AB#4924 increment 9 — the amortisation triple (plan §11). Held comes from this
        // controller's clock, work from the member's; the difference is the per-lease warm-up
        // concept §2.3 exists to make measurable. Recorded BEFORE the outcome is applied, so a
        // repository failure below cannot cost the sample.
        AdapterLeasingMetrics.RecordReleased(released.TenantId, released.AdapterPoolTenantId, released.AdapterPoolRtId,
            ReleaseReasonTag(result.Reason), result.Success,
            DateTime.UtcNow - released.GrantedAtUtc,
            result.WorkDurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

        await ApplyLeaseOutcomeAsync(released, result.Success, result.StatusMessage, result.OutputData);

        if (result is { Success: false, Reason: not LeaseReleaseReasonDto.Drained })
        {
            await _eventService.StoreErrorEventAsync(released.TenantId,
                $"A leased execution on adapter pool {released.AdapterPoolRtId} of tenant '{released.AdapterPoolTenantId}' " +
                $"failed: {result.StatusMessage ?? "no detail reported"}");
        }

        // 🔴 AB#4924 §9.6 — a member just became available. Without this the next grant waits for the
        // scheduler's tick however short the work was, which is what capped a member near twelve
        // executions a minute. Last in the method on purpose: the member is only really free once the
        // outcome above has been applied, and a round that started earlier could otherwise grant it
        // a second lease while the first one's execution was still being written.
        //
        // Requested for a member that is now idle, NOT for a drain: a draining member takes no
        // further work, so a round on its account would read every borrower's queue for nothing.
        if (result.Reason != LeaseReleaseReasonDto.Drained)
        {
            _wakeSignal.RequestRound($"lease '{released.LeaseId}' released by its member");
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
        Logger.Warn(
            "Pool member '{MemberId}' disconnected while holding lease '{LeaseId}' of tenant '{BorrowerTenantId}'; " +
            "the work item is interrupted",
            member.MemberId, lease.LeaseId, lease.TenantId);

        // Concept §6: at-least-once. The attempt is marked Interrupted with its lease span closed,
        // and a fresh attempt takes its place in the queue.
        var requeuedExecutionId = await InterruptAndRequeueAsync(lease, LeaseInterruptReason.MemberLost,
            $"The adapter pool member '{member.MemberId}' disconnected while holding this execution's lease.");

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

            await _communicationRepository.UpdatePipelineExecutionAsync(lease.TenantId, lease.ExecutionId,
                success ? RtPipelineExecutionStatusEnum.Completed : RtPipelineExecutionStatusEnum.Failed,
                completedAt, durationMs,
                success ? null : statusMessage ?? "The leased execution failed without a reported reason.",
                // 🔴 The release is the ONLY route a leased execution's output has. A dedicated adapter
                // reports it on IAdapterHub.ReportExecutionEndAsync, whose tenant and adapter come from
                // the connection - a pool member holds a tenant-free management channel and has no such
                // connection to report on (AB#4924 §9.9 / D4).
                outputData);
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
    public async Task<string?> InterruptAndRequeueAsync(LeaseDto lease, LeaseInterruptReason interruptReason,
        string reason)
    {
        // Counted even when there is no execution behind the lease: a member that died holding a
        // hand-driven lease is the same fault, and a counter that silently skipped those would
        // under-report exactly the mid-lease failures it exists to surface.
        AdapterLeasingMetrics.RecordInterrupted(lease.TenantId, lease.AdapterPoolTenantId, lease.AdapterPoolRtId,
            interruptReason, DateTime.UtcNow - lease.GrantedAtUtc);

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

            await _communicationRepository.EnqueueExecutionAsync(lease.TenantId, retry,
                interrupted.PipelineRtEntityId, interrupted.AdapterRtEntityId, DateTime.UtcNow);

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
        string lenderTenantId, OctoObjectId poolRtId, LeaseRequest request)
    {
        if (borrower.LifecycleMode != RtLifecycleModeEnum.Leased)
        {
            return (LeaseRefusalReason.BorrowerNotLeased,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' is not Leased " +
                $"(LifecycleMode={borrower.LifecycleMode}); only a Leased adapter borrows a process.");
        }

        if (!string.Equals(borrower.LentFromTenantId, lenderTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return (LeaseRefusalReason.BorrowerNamesAnotherLender,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from tenant " +
                $"'{borrower.LentFromTenantId ?? "<unset>"}', not from '{lenderTenantId}'.");
        }

        if (!string.Equals(borrower.LentFromPoolRtId, poolRtId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (LeaseRefusalReason.BorrowerNamesAnotherPool,
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from pool " +
                $"{borrower.LentFromPoolRtId ?? "<unset>"}, not from {poolRtId}.");
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
    ///         for the same reason <c>PoolService.AppendPipelineServiceAccountOverridesAsync</c> does:
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
