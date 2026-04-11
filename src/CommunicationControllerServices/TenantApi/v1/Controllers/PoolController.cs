using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages adapter configuration
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PoolController : ControllerBase
{
    private readonly ILogger<PoolController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPoolService _poolService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="communicationRepository"></param>
    /// <param name="poolService">Pool management service instance</param>
    public PoolController(ILogger<PoolController> logger, ICommunicationRepository communicationRepository, IPoolService poolService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
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
    /// Returns the configuration for a specific pool
    /// </summary>
    /// <param name="poolRtId">The pool entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("{poolRtId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItem([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        var config = await _poolService.GetPoolConfigurationAsync(tenantId, poolRtId);

        return Ok(config);
    }
    
    /// <summary>
    /// Deploys all adapters of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <returns></returns>
    [HttpPost("deployAllAdaptersOfPool")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployAllAdaptersOfPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _poolService.DeployAdaptersAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying all adapters of pool");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
    
    /// <summary>
    /// Undeploys all adapters of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <returns></returns>
    [HttpPost("undeployAllAdaptersOfPool")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployAllAdaptersOfPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _poolService.UndeployAdaptersAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploy all adapters of pool");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// (Re-)Deploys an adapter of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <param name="adapterRtEntityId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("deployAdapter")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployAdapterAsync([Required][FromQuery] OctoObjectId poolRtId, [Required][FromQuery] string adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _poolService.DeployAdapterAsync(tenantId, poolRtId, adapterRtEntityId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying adapter");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
    
    /// <summary>
    /// Undeploys an adapter of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <param name="adapterRtEntityId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("unDeployAdapter")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UnDeployAdapterAsync([Required][FromQuery] OctoObjectId poolRtId, [Required][FromQuery] string adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _poolService.UndeployAdapterAsync(tenantId, poolRtId, adapterRtEntityId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploy adapter");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
}