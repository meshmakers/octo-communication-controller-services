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
    /// Updates the configuration at a pool
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <param name="poolRtId">The id of the pool.</param>
    /// <returns></returns>
    [HttpPost("deployUpdate")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployUpdate([Required] string tenantId, [Required][FromQuery] OctoObjectId poolRtId)
    {
        try
        {
            await _poolService.DeployAdaptersAsync(tenantId, poolRtId);
            return NoContent();
        }
        catch (PoolServiceException e)
        {
            return BadRequest(e.Message);
        }
    }
}