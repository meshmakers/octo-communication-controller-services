using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.Shared;
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
    private readonly IPlugRepository _plugRepository;
    private readonly IPlugService _plugService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="plugRepository"></param>
    /// <param name="plugService">Plug-In Management service instance</param>
    public PlugController(ILogger<PlugController> logger, IPlugRepository plugRepository, IPlugService plugService)
    {
        _logger = logger;
        _plugRepository = plugRepository;
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

        var config = await _plugRepository.GetPlugsAsync(tenantId);

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