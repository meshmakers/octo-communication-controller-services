using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
///     Manual reconcile of the platform-owned SecretDefaults.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class SecretDefaultsController : ControllerBase
{
    private readonly ISecretDefaultProvisioningService _secretDefaultProvisioningService;
    private readonly ILogger<SecretDefaultsController> _logger;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="secretDefaultProvisioningService">Reconciles the SecretDefaults of a tenant.</param>
    /// <param name="logger">Logging object.</param>
    public SecretDefaultsController(ISecretDefaultProvisioningService secretDefaultProvisioningService,
        ILogger<SecretDefaultsController> logger)
    {
        _secretDefaultProvisioningService = secretDefaultProvisioningService;
        _logger = logger;
    }

    /// <summary>
    ///     Reconciles the SecretDefaults of the route tenant and of every tenant in its subtree: each receives what
    ///     its own tenant and its ancestors provide and loses what nobody provides any more.
    /// </summary>
    /// <remarks>
    ///     The manual counterpart of the reconcile on every tenant load. Answers with counts per tenant, never a
    ///     value. Idempotent.
    /// </remarks>
    [HttpPost("reconcile")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<SecretDefaultTargetResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReconcileAsync()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var perTenantResults = await _secretDefaultProvisioningService.ReconcileTenantAndSubtreeAsync(tenantId,
            HttpContext.RequestAborted);
        _logger.LogInformation(
            "[{TenantId}] Secret defaults reconciled on request: {TenantCount} tenant(s), {Created} created, " +
            "{Removed} removed, {Failed} failed", tenantId, perTenantResults.Count,
            perTenantResults.Sum(result => result.DefaultsCreated),
            perTenantResults.Sum(result => result.DefaultsRemoved),
            perTenantResults.Sum(result => result.WritesFailed));
        return Ok(perTenantResults);
    }
}
