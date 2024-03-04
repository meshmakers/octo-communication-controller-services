using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages adapter configuration
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class AdapterController : ControllerBase
{
    private readonly ILogger<AdapterController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="communicationRepository"></param>
    /// <param name="adapterService">Adapter management service instance</param>
    internal AdapterController(ILogger<AdapterController> logger, ICommunicationRepository communicationRepository, IAdapterService adapterService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _adapterService = adapterService;
    }
    
    /// <summary>
    /// Returns a list of all adapters for the tenant
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

        var config = await _communicationRepository.GetAdaptersAsync(tenantId);

        return Ok(config);
    }

    /// <summary>
    /// Returns the configuration for a specific adapter
    /// </summary>
    /// <param name="adapterRtId">The adapter entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("{adapterRtId}")]
    public async Task<IActionResult> Get([Required] OctoObjectId adapterRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        var config = await _adapterService.GetAdapterConfigurationAsync(tenantId, adapterRtId);

        return Ok(config);
    }
    
    /// <summary>
    /// Enables the communication controller for a tenant
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <param name="adapterRtId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("update")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Update([Required] string tenantId, [Required] OctoObjectId adapterRtId)
    {
        try
        {
            await _adapterService.UpdateAdapterConfigurationAsync(tenantId, adapterRtId);
            return NoContent();
        }
        catch (AdapterServiceException e)
        {
            return BadRequest(e.Message);
        }
    }
}