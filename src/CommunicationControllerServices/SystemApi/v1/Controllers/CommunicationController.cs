using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the communication controller itself
/// </summary>
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class CommunicationController: ControllerBase
{
    private readonly ILogger<CommunicationController> _logger;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configurationService"></param>
    public CommunicationController(ILogger<CommunicationController> logger, IConfigurationService configurationService)
    {
        _logger = logger;
        _configurationService = configurationService;
    }
    
    
    /// <summary>
    /// Pings the communication controller
    /// </summary>
    /// <returns></returns>
    [HttpGet("ping")]
    public IActionResult Get()
    {
        _logger.LogTrace("Ping TRACE");
        _logger.LogDebug("Ping DEBUG");
        _logger.LogInformation("Ping INFORMATION");
        _logger.LogError("Ping ERROR");
        _logger.LogCritical("Ping CRITICAL");
        return Ok("Pong");
    }

    /// <summary>
    /// Enables the communication controller for a tenant
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <returns></returns>
    [HttpPost("enable")]
  //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Enable([Required] string tenantId)
    {
        try
        {
            await _configurationService.EnableAsync(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }
    
    /// <summary>
    /// Disables the communication controller for a tenant
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <returns></returns>
    [HttpPost("disable")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> Disable([Required] string tenantId)
    {
        try
        {
            await _configurationService.DisableAsync(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }
}