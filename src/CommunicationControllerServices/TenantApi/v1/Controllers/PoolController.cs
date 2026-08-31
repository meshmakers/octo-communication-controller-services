using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages pool deployments and lifecycle.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PoolController : ControllerBase
{
    private readonly ILogger<PoolController> _logger;
    private readonly IPoolService _poolService;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="poolService">Pool management service instance</param>
    /// <param name="configurationService">Enabled state of Communication per tenant</param>
    public PoolController(ILogger<PoolController> logger, IPoolService poolService,
        IConfigurationService configurationService)
    {
        _logger = logger;
        _poolService = poolService;
        _configurationService = configurationService;
    }

    /// <summary>
    /// AB#4255: deploying creates operator-managed cluster resources, which must not happen on a
    /// tenant whose Communication is disabled — the tenant delete guard only sees the flag, and a
    /// pool deployed afterwards would be orphaned. This service does not run the platform's tenant
    /// enabled-gate middleware (it would also gate the adapter hub), so the two resource-creating
    /// endpoints check the flag themselves. Undeploy stays open so remediation always works.
    /// </summary>
    private async Task<IActionResult?> RefuseWhileDisabledAsync(string tenantId, string operation)
    {
        if (await _configurationService.IsEnabledAsync(tenantId))
        {
            return null;
        }

        _logger.LogWarning("Rejected {Operation} for tenant '{TenantId}': Communication is disabled", operation,
            tenantId);
        return Conflict(new OperationFailedErrorDto(
            $"Communication is disabled for tenant '{tenantId}'. Enable it first (EnableCommunication) before deploying pools or workloads."));
    }
    
    /// <summary>
    /// Returns a list of all pools for the tenant
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        var pools = await _poolService.GetPoolSummariesAsync(tenantId);

        return Ok(pools);
    }

    /// <summary>
    /// Deploys a pool. For Cloud-environment pools, this triggers the central
    /// Communication Operator to provision the corresponding CommunicationPool
    /// CR and broker secret. Edge-environment pools transition state without
    /// any operator notification.
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    [HttpPost("deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeployPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (await RefuseWhileDisabledAsync(tenantId, "pool deploy") is { } refusal)
        {
            return refusal;
        }

        try
        {
            await _poolService.DeployPoolAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying pool");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Undeploys a pool. For Cloud-environment pools, this notifies the
    /// central Communication Operator to remove the CommunicationPool CR and
    /// broker secret.
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    [HttpPost("undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            await _poolService.UndeployPoolAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploying pool");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Deploys a single workload (Adapter or Application). Independent of
    /// pool deploy — the workload's parent pool must already be deployed,
    /// but this call only triggers the operator's helm-install for the
    /// one workload.
    /// </summary>
    /// <param name="workloadRtId">The runtime id of the workload.</param>
    [HttpPost("workloads/deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeployWorkloadAsync([Required][FromQuery] OctoObjectId workloadRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (await RefuseWhileDisabledAsync(tenantId, "workload deploy") is { } refusal)
        {
            return refusal;
        }

        try
        {
            await _poolService.DeployWorkloadAsync(tenantId, workloadRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying workload");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Undeploys a single workload (Adapter or Application).
    /// </summary>
    /// <param name="workloadRtId">The runtime id of the workload.</param>
    [HttpPost("workloads/undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployWorkloadAsync([Required][FromQuery] OctoObjectId workloadRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            await _poolService.UndeployWorkloadAsync(tenantId, workloadRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploying workload");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

}