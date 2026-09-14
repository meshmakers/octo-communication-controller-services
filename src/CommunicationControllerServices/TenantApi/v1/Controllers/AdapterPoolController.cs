using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
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
    private readonly IAdapterPoolConnectionManager _connectionManager;
    private readonly ILeaseSchedulerService _leaseScheduler;
    private readonly ILeaseService _leaseService;
    private readonly ILogger<AdapterPoolController> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="connectionManager">Registry of pool members connected to this instance.</param>
    /// <param name="leaseScheduler">Owns the pool queue and its rotation.</param>
    /// <param name="leaseService">Grants leases.</param>
    /// <param name="logger">Logging object.</param>
    public AdapterPoolController(IAdapterPoolConnectionManager connectionManager,
        ILeaseSchedulerService leaseScheduler,
        ILeaseService leaseService, ILogger<AdapterPoolController> logger)
    {
        _connectionManager = connectionManager;
        _leaseScheduler = leaseScheduler;
        _leaseService = leaseService;
        _logger = logger;
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
