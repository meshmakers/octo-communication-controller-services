using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
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

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="poolService">Pool management service instance</param>
    public PoolController(ILogger<PoolController> logger, IPoolService poolService)
    {
        _logger = logger;
        _poolService = poolService;
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
    public async Task<IActionResult> DeployPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
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

}