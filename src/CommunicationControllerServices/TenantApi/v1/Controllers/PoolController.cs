using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages adapter configuration
/// </summary>
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
    public async Task<IActionResult> Get()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        var config = await _communicationRepository.GetPoolsAsync(tenantId);

        return Ok(config);
    }

    /// <summary>
    /// Returns the configuration for a specific pool
    /// </summary>
    /// <param name="poolRtId">The pool entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet]
    public async Task<IActionResult> Get([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
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
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployAllAdaptersOfPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }
        
        try
        {
            await _poolService.DeployAdaptersAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying all adapters of pool");
            return BadRequest(e.Message);
        }
    }
    
    /// <summary>
    /// Undeploys all adapters of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <returns></returns>
    [HttpPost("undeployAllAdaptersOfPool")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> UndeployAllAdaptersOfPoolAsync([Required][FromQuery] OctoObjectId poolRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }
        
        try
        {
            await _poolService.UndeployAdaptersAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploying all adapters of pool");
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// (Re-)Deploys an adapter of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <param name="adapterRtEntityId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("deployAdapter")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployAdapterAsync([Required][FromQuery] OctoObjectId poolRtId, [Required][FromQuery] string adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }
        
        try
        {
            await _poolService.DeployAdapterAsync(tenantId, poolRtId, adapterRtEntityId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error deploying adapter");
            return BadRequest(e.Message);
        }
    }
    
    /// <summary>
    /// Undeploys an adapter of a pool
    /// </summary>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <param name="adapterRtEntityId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("unDeployAdapter")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> UnDeployAdapterAsync([Required][FromQuery] OctoObjectId poolRtId, [Required][FromQuery] string adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }
        
        try
        {
            await _poolService.UndeployAdapterAsync(tenantId, poolRtId, adapterRtEntityId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            _logger.LogError(e, "Error undeploying adapter");
            return BadRequest(e.Message);
        }
    }
}