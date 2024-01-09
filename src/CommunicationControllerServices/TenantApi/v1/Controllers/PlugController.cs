using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages plug configuration
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PlugController : ControllerBase
{
    private readonly ILogger<PlugController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPlugService _plugService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="communicationRepository"></param>
    /// <param name="plugService">Plug-In Management service instance</param>
    internal PlugController(ILogger<PlugController> logger, ICommunicationRepository communicationRepository, IPlugService plugService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _plugService = plugService;
    }
    
    /// <summary>
    /// Returns a list of all plugs for the tenant
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

        var config = await _communicationRepository.GetPlugsAsync(tenantId);

        return Ok(config);
    }

    /// <summary>
    /// Returns the configuration for a specific plug
    /// </summary>
    /// <param name="plugId">The plug entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("{plugId}")]
    public async Task<IActionResult> Get([Required] OctoObjectId plugId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        var config = await _plugService.GetPlugConfigurationAsync(tenantId, plugId);

        return Ok(config);
    }
}