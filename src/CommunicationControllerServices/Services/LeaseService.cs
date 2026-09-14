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

    public LeaseService(IAdapterPoolConnectionManager connectionManager,
        ICommunicationRepository communicationRepository,
        ICommunicationEventService eventService,
        IWorkloadEncryptionService encryptionService,
        IHubContext<AdapterPoolHub> hubContext,
        ITenantLendingScopeResolver lendingScopeResolver,
        IPipelineServiceAccountResolver serviceAccountResolver)
    {
        _connectionManager = connectionManager;
        _communicationRepository = communicationRepository;
        _eventService = eventService;
        _encryptionService = encryptionService;
        _hubContext = hubContext;
        _lendingScopeResolver = lendingScopeResolver;
        _serviceAccountResolver = serviceAccountResolver;
    }

    /// <inheritdoc />
    public async Task<LeaseGrantResult> GrantLeaseAsync(string lenderTenantId, OctoObjectId poolRtId,
        LeaseRequest request, CancellationToken cancellationToken = default,
        Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>>? admissionGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lenderTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BorrowerTenantId);

        // 🔴 The order of the three checks below is the order of least trust. The borrowing
        // relationship is checked BEFORE the credential is read, so a request naming an adapter it
        // has no business naming never reaches the code that decrypts a client secret.
        var borrower = await ReadBorrowerAdapterAsync(request.BorrowerTenantId, request.BorrowerAdapterRtId);
        if (borrower is null)
        {
            return LeaseGrantResult.Refused(
                $"Tenant '{request.BorrowerTenantId}' has no adapter with rtId {request.BorrowerAdapterRtId}.");
        }

        var declarationRefusal = CheckBorrowerDeclaration(borrower, lenderTenantId, poolRtId, request);
        if (declarationRefusal != null)
        {
            return LeaseGrantResult.Refused(declarationRefusal);
        }

        // The lender's half. Both halves are required and neither implies the other: the borrower
        // says "I borrow from that pool", the pool says "I lend into that part of the tree". A pool
        // that has been deleted or re-scoped since the borrower was deployed lands here (concept §6,
        // "parent tenant deleted while lending") rather than at deploy time.
        var lendingScope = await _communicationRepository
            .TryGetAdapterPoolLendingScopeAsync(lenderTenantId, poolRtId.ToString());
        if (lendingScope is null)
        {
            return LeaseGrantResult.Refused(
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
            return LeaseGrantResult.Refused(
                $"Adapter pool {poolRtId} in tenant '{lenderTenantId}' does not lend to tenant " +
                $"'{request.BorrowerTenantId}'.");
        }

        var credential = await ResolveBorrowerCredentialAsync(request.BorrowerTenantId, borrower);
        if (credential is null)
        {
            return LeaseGrantResult.Refused(
                $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' has no usable pipeline " +
                "service account; a pool member cannot act as a borrower without one.");
        }

        var grantedAt = DateTime.UtcNow;
        var lease = new LeaseDto
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            TenantId = request.BorrowerTenantId,
            PoolTenantId = lenderTenantId,
            PoolRtId = poolRtId.ToString(),
            AdapterRtId = borrower.RtId.ToString(),
            // The concrete CK type, not the base Adapter id: Adapter is polymorphic and the member
            // has to rebuild the exact RtEntityId to register under. A null here would mean an
            // entity read without its discriminator, which the fallback keeps usable rather than
            // turning into a NullReferenceException inside a hub method.
            AdapterCkTypeId = (borrower.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId).ToString(),
            ExecutionId = request.ExecutionId ?? string.Empty,
            ClientId = credential.Value.ClientId,
            ClientSecret = credential.Value.ClientSecret,
            GrantedAtUtc = grantedAt,
            ExpiresAtUtc = grantedAt + (request.Ttl ?? ILeaseService.DefaultLeaseTtl)
        };

        var member = _connectionManager.TryClaimMember(lenderTenantId, poolRtId.ToString(), lease);
        if (member is null)
        {
            // Nothing is parked here. A caller driving this by hand is told plainly; the scheduler
            // (increment 7) leaves the work item Queued, which is where the queue actually lives.
            return LeaseGrantResult.Refused(
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
                return LeaseGrantResult.Refused($"The lease admission gate failed: {e.Message}");
            }

            if (!admitted)
            {
                _connectionManager.ReleaseLease(member.ConnectionId, lease.LeaseId);
                return LeaseGrantResult.Refused(
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
            return LeaseGrantResult.Refused(
                $"Pool member '{member.MemberId}' could not be handed the lease: {e.Message}");
        }

        // 🔴 The lease object is never logged as a whole and the secret is never named. LeaseDto
        // overrides ToString for the same reason, but a log statement that interpolated the secret
        // explicitly would defeat that — so the fields are listed one by one, on purpose.
        Logger.Info(
            "Granted lease '{LeaseId}' of pool {PoolRtId} (tenant '{PoolTenantId}') to tenant '{BorrowerTenantId}' " +
            "on member '{MemberId}', expires {ExpiresAtUtc:O}",
            lease.LeaseId, lease.PoolRtId, lease.PoolTenantId, lease.TenantId, member.MemberId,
            lease.ExpiresAtUtc);

        return new LeaseGrantResult(true, lease.LeaseId, member.MemberId, null);
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

        await ApplyLeaseOutcomeAsync(released, result.Success, result.StatusMessage);

        if (result is { Success: false, Reason: not LeaseReleaseReasonDto.Drained })
        {
            await _eventService.StoreErrorEventAsync(released.TenantId,
                $"A leased execution on adapter pool {released.PoolRtId} of tenant '{released.PoolTenantId}' " +
                $"failed: {result.StatusMessage ?? "no detail reported"}");
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
        var requeuedExecutionId = await InterruptAndRequeueAsync(lease,
            $"The adapter pool member '{member.MemberId}' disconnected while holding this execution's lease.");

        await _eventService.StoreErrorEventAsync(lease.TenantId,
            $"The adapter pool member '{member.MemberId}' holding this tenant's lease " +
            $"(pool {lease.PoolRtId} of tenant '{lease.PoolTenantId}') disconnected before releasing it. " +
            "Any pipeline execution it was running is interrupted" +
            (requeuedExecutionId is null
                ? "."
                : $" and was re-queued as execution '{requeuedExecutionId}'."));
    }

    /// <inheritdoc />
    public async Task ApplyLeaseOutcomeAsync(LeaseDto lease, bool success, string? statusMessage)
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
                success ? null : statusMessage ?? "The leased execution failed without a reported reason.");
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
    public async Task<string?> InterruptAndRequeueAsync(LeaseDto lease, string reason)
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

            await _communicationRepository.EnqueueExecutionAsync(lease.TenantId, retry,
                interrupted.PipelineRtEntityId, interrupted.AdapterRtEntityId, DateTime.UtcNow);

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
    private static string? CheckBorrowerDeclaration(RtAdapter borrower, string lenderTenantId,
        OctoObjectId poolRtId, LeaseRequest request)
    {
        if (borrower.LifecycleMode != RtLifecycleModeEnum.Leased)
        {
            return $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' is not Leased " +
                   $"(LifecycleMode={borrower.LifecycleMode}); only a Leased adapter borrows a process.";
        }

        if (!string.Equals(borrower.LentFromTenantId, lenderTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from tenant " +
                   $"'{borrower.LentFromTenantId ?? "<unset>"}', not from '{lenderTenantId}'.";
        }

        if (!string.Equals(borrower.LentFromPoolRtId, poolRtId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return $"Adapter '{borrower.Name}' in tenant '{request.BorrowerTenantId}' borrows from pool " +
                   $"{borrower.LentFromPoolRtId ?? "<unset>"}, not from {poolRtId}.";
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
