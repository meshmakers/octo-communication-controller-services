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
    private readonly ILeaseService _leaseService;
    private readonly ILogger<AdapterPoolController> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="connectionManager">Registry of pool members connected to this instance.</param>
    /// <param name="leaseService">Grants leases.</param>
    /// <param name="logger">Logging object.</param>
    public AdapterPoolController(IAdapterPoolConnectionManager connectionManager,
        ILeaseService leaseService, ILogger<AdapterPoolController> logger)
    {
        _connectionManager = connectionManager;
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
