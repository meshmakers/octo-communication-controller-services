using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
///     Hand-driven operations on an adapter pool (AB#4924, increment 6).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The lease endpoint is a test and diagnostic surface, not the production path.</b>
///         Production leasing is driven by the scheduler of increment 7 from a queue this increment
///         deliberately does not build. What this endpoint buys is that the whole mechanism below it
///         — the borrower's declaration, the lender's sharing scope, the credential projection, the
///         hub routing and the release — can be exercised end to end one increment before the
///         scheduler exists, on a real two-tenant pair.
///     </para>
///     <para>
///         The route tenant is the <b>lending</b> tenant: the pool is its entity and the caller must
///         be able to write in it. The borrower travels in the body and is authorized by the lending
///         scope, never by the caller's token — a lender may lend into its subtree without holding a
///         credential for anything in it.
///     </para>
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class AdapterPoolController : ControllerBase
{
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IAdapterPoolConnectionManager _connectionManager;
    private readonly ILeaseSchedulerService _leaseScheduler;
    private readonly ILeaseService _leaseService;
    private readonly ITenantLendingScopeResolver _lendingScopeResolver;
    private readonly IAdapterPoolMirrorProvisioningService _mirrorProvisioningService;
    private readonly ILogger<AdapterPoolController> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="connectionManager">Registry of pool members connected to this instance.</param>
    /// <param name="leaseScheduler">Owns the pool queue and its rotation.</param>
    /// <param name="leaseService">Grants leases.</param>
    /// <param name="mirrorProvisioningService">Reconciles the borrower-local LentAdapterPool mirrors (AB#5271).</param>
    /// <param name="communicationRepository">Reads the lending tenant's own pool (AB#5271).</param>
    /// <param name="lendingScopeResolver">Decides whether a pool lends to the asking tenant (AB#5271).</param>
    /// <param name="logger">Logging object.</param>
    public AdapterPoolController(IAdapterPoolConnectionManager connectionManager,
        ILeaseSchedulerService leaseScheduler,
        ILeaseService leaseService,
        IAdapterPoolMirrorProvisioningService mirrorProvisioningService,
        ICommunicationRepository communicationRepository,
        ITenantLendingScopeResolver lendingScopeResolver,
        ILogger<AdapterPoolController> logger)
    {
        _connectionManager = connectionManager;
        _leaseScheduler = leaseScheduler;
        _leaseService = leaseService;
        _mirrorProvisioningService = mirrorProvisioningService;
        _communicationRepository = communicationRepository;
        _lendingScopeResolver = lendingScopeResolver;
        _logger = logger;
    }

    /// <summary>
    ///     Reconciles the route tenant's own <c>LentAdapterPool</c> mirrors against what its
    ///     ancestors and siblings currently lend it (AB#5271).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The route tenant is the <b>borrower</b>. This is the manual counterpart of the
    ///         reconcile that runs on every tenant load — it exists because the events that make a
    ///         mirror necessary happen in another tenant's database, where this service has no hook:
    ///         a pool renamed or re-scoped through the asset repository reaches the borrowers on the
    ///         next tenant load, or here, whichever comes first.
    ///     </para>
    ///     <para>
    ///         Read-write, not read-only: it writes entities in the route tenant. Idempotent, so it
    ///         is safe to call repeatedly.
    ///     </para>
    /// </remarks>
    [HttpPost("mirrors/refresh")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(AdapterPoolMirrorSyncResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefreshMirrorsAsync()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var result = await _mirrorProvisioningService.ProvisionForBorrowerAsync(tenantId,
            HttpContext.RequestAborted);
        _logger.LogInformation(
            "[{TenantId}] Lent adapter pool mirrors refreshed on request: {Created} present, {Removed} removed, " +
            "{Relinked} leased adapter(s) re-linked",
            tenantId, result.MirrorsCreatedOrUpdated, result.MirrorsRemoved, result.AdaptersRelinked);
        return Ok(result);
    }

    /// <summary>
    ///     Pushes the route tenant's adapter pools out to every tenant that may borrow from them
    ///     (AB#5271).
    /// </summary>
    /// <remarks>
    ///     The route tenant is the <b>lender</b>. Use it after changing a pool's name, sharing mode
    ///     or allow-list, which are written through the asset repository and therefore reach no hook
    ///     in this service. Each borrower is reconciled in full, so a borrower that also lost a pool
    ///     loses its mirror in the same pass.
    /// </remarks>
    [HttpPost("mirrors/publish")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(AdapterPoolMirrorSyncResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PublishMirrorsAsync()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var result = await _mirrorProvisioningService.ProvisionForLenderAsync(tenantId,
            HttpContext.RequestAborted);
        _logger.LogInformation(
            "[{TenantId}] Adapter pool mirrors published on request: {Tenants} borrower(s), {Created} present, " +
            "{Removed} removed, {Relinked} leased adapter(s) re-linked",
            tenantId, result.TenantsReconciled, result.MirrorsCreatedOrUpdated, result.MirrorsRemoved,
            result.AdaptersRelinked);
        return Ok(result);
    }

    /// <summary>
    ///     Everything the route tenant may know about one adapter pool lent TO it (AB#5271).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>The route tenant is the BORROWER here</b>, unlike every other verb on this
    ///         controller, where it is the lender. The pool is named by the pair that identifies it
    ///         across the tenant boundary — lending tenant plus its RtId inside that tenant — which
    ///         is exactly what the borrower's <c>LentAdapterPool</c> mirror carries in its
    ///         <c>Lender</c> record.
    ///     </para>
    ///     <para>
    ///         🔴 <b>The mirror is not consulted and is not trusted.</b> A tenant can edit entities in
    ///         its own database, so a mirror pointed at an arbitrary pool must not become a way to
    ///         read it: the pool is resolved from the route values and <c>MayLendAsync</c> is run
    ///         against the LENDER's own sharing mode and allow-list, the same check the lease path
    ///         makes. A pool that does not lend here answers 404 — the same answer as a pool that
    ///         does not exist, so the endpoint cannot be used to probe for pools.
    ///     </para>
    ///     <para>
    ///         Read-only and live. Chart, sizing and scale-up policy are deliberately absent from the
    ///         mirror (see <c>LendableAdapterPool</c>) because they are the lender's deployment detail
    ///         and copying them would persist one tenant's configuration inside another; a borrower
    ///         that wants them reads the lender's current values through here instead.
    ///     </para>
    ///     <para>
    ///         🔴 The queue is filtered to the caller's own entries plus counts for the rest — see
    ///         <see cref="LentAdapterPoolQueueDto" />. Serving the lender's whole queue would hand one
    ///         borrower the pipeline names and tenant ids of the lender's other customers.
    ///     </para>
    /// </remarks>
    /// <param name="lenderTenantId">The tenant that owns the pool.</param>
    /// <param name="adapterPoolRtId">RtId of the <c>AdapterPool</c> inside that tenant.</param>
    [HttpGet("lent/{lenderTenantId}/{adapterPoolRtId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(LentAdapterPoolDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLentPoolDetailsAsync([Required] string lenderTenantId,
        [Required] string adapterPoolRtId)
    {
        var borrowerTenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(borrowerTenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var details = await _communicationRepository.TryGetAdapterPoolDetailsAsync(lenderTenantId, adapterPoolRtId);
        if (details is null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorMessage = $"No adapter pool '{adapterPoolRtId}' of tenant '{lenderTenantId}' lends to " +
                               $"'{borrowerTenantId}'."
            });
        }

        if (!await _lendingScopeResolver.MayLendAsync(lenderTenantId, borrowerTenantId, details.Scope,
                HttpContext.RequestAborted))
        {
            // Deliberately the SAME message and status as an unresolvable pool: distinguishing
            // "exists but not for you" from "does not exist" would turn this into a way to
            // enumerate another tenant's pools.
            _logger.LogInformation(
                "Tenant '{BorrowerTenantId}' asked for adapter pool {AdapterPoolRtId} of '{LenderTenantId}', " +
                "which does not lend to it",
                borrowerTenantId, adapterPoolRtId, lenderTenantId);
            return NotFound(new ErrorResponse
            {
                ErrorMessage = $"No adapter pool '{adapterPoolRtId}' of tenant '{lenderTenantId}' lends to " +
                               $"'{borrowerTenantId}'."
            });
        }

        var members = _connectionManager.GetMembers(lenderTenantId, details.AdapterPoolRtId).ToList();
        var queue = await _leaseScheduler.GetQueueAsync(lenderTenantId, OctoObjectId.Parse(details.AdapterPoolRtId),
            HttpContext.RequestAborted);

        return Ok(new LentAdapterPoolDetailsDto
        {
            LenderTenantId = details.LenderTenantId,
            AdapterPoolRtId = details.AdapterPoolRtId,
            Name = details.Name,
            Description = details.Description,
            ChartName = details.ChartName,
            ChartVersion = details.ChartVersion,
            DeploymentState = details.DeploymentState,
            StatusMessage = details.StatusMessage,
            MinReplicas = details.MinReplicas,
            MaxReplicas = details.MaxReplicas,
            PoolMemberCpuRequest = details.PoolMemberCpuRequest,
            PoolMemberCpuLimit = details.PoolMemberCpuLimit,
            PoolMemberMemoryRequest = details.PoolMemberMemoryRequest,
            PoolMemberMemoryLimit = details.PoolMemberMemoryLimit,
            ScaleUpPolicy = details.ScaleUpPolicy,
            ScaleUpQueueDepthThreshold = details.ScaleUpQueueDepthThreshold,
            ScaleUpQueueWaitSeconds = details.ScaleUpQueueWaitSeconds,
            SharingMode = details.Scope.Mode,
            MaxConcurrentLeasesPerTenant = details.MaxConcurrentLeasesPerTenant,
            Members = new LentAdapterPoolMembersDto
            {
                Connected = members.Count,
                Busy = members.Count(m => m.ActiveLease is not null),
                Draining = members.Count(m => m.IsDraining)
            },
            Queue = BuildBorrowerQueueView(queue, borrowerTenantId)
        });
    }

    /// <summary>
    ///     Splits a pool's queue into the asking tenant's own entries and counts for everyone else.
    /// </summary>
    /// <remarks>
    ///     🔴 The split is the privacy boundary of <see cref="GetLentPoolDetailsAsync" />, so it is one
    ///     function over the whole queue rather than a filter applied at two call sites: every entry
    ///     either lands in <c>MyEntries</c> or contributes only to a count, and there is no third path
    ///     for a later field to slip through.
    /// </remarks>
    internal static LentAdapterPoolQueueDto BuildBorrowerQueueView(IEnumerable<AdapterPoolQueueEntry> entries,
        string borrowerTenantId)
    {
        var view = new LentAdapterPoolQueueDto();
        var otherTenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var isLeased = !string.IsNullOrEmpty(entry.LeasedOnMemberId);

            if (!string.Equals(entry.BorrowerTenantId, borrowerTenantId, StringComparison.OrdinalIgnoreCase))
            {
                otherTenants.Add(entry.BorrowerTenantId);
                if (isLeased)
                {
                    view.OtherTenantsLeasedCount++;
                }
                else
                {
                    view.OtherTenantsWaitingCount++;
                }

                continue;
            }

            view.MyEntries.Add(new AdapterPoolQueueEntryDto
            {
                ExecutionId = entry.ExecutionId,
                BorrowerTenantId = entry.BorrowerTenantId,
                PipelineRtId = entry.PipelineRtId,
                PipelineName = entry.PipelineName,
                ExecutionClass = entry.ExecutionClass,
                QueuedAtUtc = entry.QueuedAtUtc,
                PositionInTenant = entry.PositionInTenant,
                TenantsAheadInRotation = entry.TenantsAheadInRotation,
                LeasedOnMemberId = entry.LeasedOnMemberId,
                LeaseExpiresAtUtc = entry.LeaseExpiresAtUtc
            });

            if (isLeased)
            {
                view.MyLeasedCount++;
            }
            else
            {
                view.MyWaitingCount++;
                if (view.MyOldestQueuedAtUtc is null || entry.QueuedAtUtc < view.MyOldestQueuedAtUtc)
                {
                    view.MyOldestQueuedAtUtc = entry.QueuedAtUtc;
                }
            }
        }

        view.OtherTenantsInRotation = otherTenants.Count;
        return view;
    }

    /// <summary>
    ///     Grants a lease on a member of this pool to a borrowing tenant.
    /// </summary>
    /// <param name="adapterPoolRtId">RtId of the <c>AdapterPool</c> in the route tenant.</param>
    /// <param name="body">Which borrower, which adapter, and for how long.</param>
    [HttpPost("{adapterPoolRtId}/lease")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(AdapterPoolLeaseResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GrantLeaseAsync([Required] OctoObjectId adapterPoolRtId,
        [Required] [FromBody] GrantAdapterPoolLeaseRequestDto body)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(body.AdapterRtId, out var adapterRtId))
        {
            return BadRequest(new ErrorResponse
            {
                ErrorMessage = $"AdapterRtId '{body.AdapterRtId}' must be a 24-character hex ObjectId."
            });
        }

        var result = await _leaseService.GrantLeaseAsync(tenantId, adapterPoolRtId,
            new LeaseRequest(body.BorrowerTenantId, adapterRtId, body.ExecutionId,
                body.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
            HttpContext.RequestAborted);

        if (!result.Granted)
        {
            // 200 with Granted=false rather than a 4xx: "no idle member right now" is a normal
            // outcome of asking for a lease, not a malformed request, and a caller driving this by
            // hand needs the reason string more than it needs a status code to branch on.
            _logger.LogInformation(
                "Lease of adapter pool {AdapterPoolRtId} in tenant '{TenantId}' to '{BorrowerTenantId}' was not " +
                "granted: {Reason}",
                adapterPoolRtId, tenantId, body.BorrowerTenantId, result.StatusMessage);
        }

        return Ok(new AdapterPoolLeaseResultDto
        {
            Granted = result.Granted,
            LeaseId = result.LeaseId,
            MemberId = result.MemberId,
            StatusMessage = result.StatusMessage
        });
    }

    /// <summary>
    ///     The queue of this adapter pool: everything waiting for a lease, plus everything this
    ///     controller instance currently has leased out of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>The shared server contract of increment 8, built here in increment 7 because all
    ///         three surfaces consume this one endpoint</b> — Refinery Studio, <c>octo-cli</c> and the
    ///         MCP server, the same view in all three rather than a Studio-only one (concept §5). It
    ///         is an endpoint and not a GraphQL query for two independent reasons: the rotation
    ///         position is scheduler state rather than entity state, and the entries span tenant
    ///         databases — the pool belongs to the lender and every execution to a borrower.
    ///     </para>
    ///     <para>
    ///         The entry shape is <c>Communication.Contracts</c>'s <see cref="AdapterPoolQueueEntryDto" />
    ///         — increment 7 declared a controller-local copy because no client existed yet, and
    ///         increment 8 replaced it with the shared one, the way <c>DeploymentSiteSummaryDto</c> and the rest
    ///         of the communication contract already work. One wire shape, one declaration.
    ///     </para>
    ///     <para>
    ///         🔴 <b>Position is reported per tenant plus tenants-ahead, never as one global rank.</b>
    ///         Round-robin has no global rank to report, and a single number would contradict the
    ///         order work actually runs in.
    ///     </para>
    ///     <para>
    ///         A <b>manual</b> adapter has no queue at all and therefore no equivalent of this
    ///         endpoint. That asymmetry is intended (concept §5).
    ///     </para>
    /// </remarks>
    /// <param name="adapterPoolRtId">RtId of the <c>AdapterPool</c> in the route tenant.</param>
    [HttpGet("{adapterPoolRtId}/queue")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<AdapterPoolQueueEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQueueAsync([Required] OctoObjectId adapterPoolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var entries = await _leaseScheduler.GetQueueAsync(tenantId, adapterPoolRtId, HttpContext.RequestAborted);

        return Ok(entries.Select(e => new AdapterPoolQueueEntryDto
        {
            ExecutionId = e.ExecutionId,
            BorrowerTenantId = e.BorrowerTenantId,
            PipelineRtId = e.PipelineRtId,
            PipelineName = e.PipelineName,
            ExecutionClass = e.ExecutionClass,
            QueuedAtUtc = e.QueuedAtUtc,
            PositionInTenant = e.PositionInTenant,
            TenantsAheadInRotation = e.TenantsAheadInRotation,
            LeasedOnMemberId = e.LeasedOnMemberId,
            LeaseExpiresAtUtc = e.LeaseExpiresAtUtc
        }).ToList());
    }

    /// <summary>
    ///     Cancels one entry that is still waiting for a lease.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>This cancels a QUEUE entry, not a running pipeline.</b> An execution that already
    ///     holds a lease answers <c>409 Conflict</c> here on purpose: interrupting a running pipeline
    ///     is a different operation with different consequences, and collapsing the two into one verb
    ///     would hide from the operator which of them they just performed (concept §5,
    ///     "Cancellation"). The surfaces must make that difference visible rather than retry.
    /// </remarks>
    /// <param name="adapterPoolRtId">RtId of the <c>AdapterPool</c> in the route tenant.</param>
    /// <param name="executionId">The queued execution to cancel.</param>
    [HttpDelete("{adapterPoolRtId}/queue/{executionId}")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelQueuedExecutionAsync([Required] OctoObjectId adapterPoolRtId,
        [Required] string executionId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var result = await _leaseScheduler.CancelQueuedExecutionAsync(tenantId, adapterPoolRtId, executionId,
            HttpContext.RequestAborted);

        switch (result)
        {
            case QueueCancellationResult.Cancelled:
                _logger.LogInformation(
                    "Cancelled queued execution '{ExecutionId}' of adapter pool {AdapterPoolRtId} in tenant " +
                    "'{TenantId}'",
                    executionId, adapterPoolRtId, tenantId);
                return NoContent();

            case QueueCancellationResult.AlreadyLeased:
                return Conflict(new ErrorResponse
                {
                    ErrorMessage =
                        $"Execution '{executionId}' already holds a lease and is no longer queued. Cancelling it " +
                        "means interrupting a running pipeline, which is a different operation."
                });

            default:
                return NotFound(new ErrorResponse
                {
                    ErrorMessage =
                        $"No queued execution '{executionId}' belongs to adapter pool {adapterPoolRtId} of tenant " +
                        $"'{tenantId}'."
                });
        }
    }

    /// <summary>
    ///     Lists the members of this pool that are connected to this controller instance, and what
    ///     each is holding.
    /// </summary>
    /// <param name="adapterPoolRtId">RtId of the <c>AdapterPool</c> in the route tenant.</param>
    [HttpGet("{adapterPoolRtId}/members")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<AdapterPoolMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult GetMembers([Required] OctoObjectId adapterPoolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var members = _connectionManager.GetMembers(tenantId, adapterPoolRtId.ToString())
            .Select(m => new AdapterPoolMemberDto
            {
                MemberId = m.MemberId,
                ActiveLeaseId = m.ActiveLease?.LeaseId,
                ActiveLeaseTenantId = m.ActiveLease?.TenantId,
                IsDraining = m.IsDraining,
                LastSeenUtc = m.LastSeenUtc
            })
            .OrderBy(m => m.MemberId, StringComparer.Ordinal)
            .ToList();

        return Ok(members);
    }
}
