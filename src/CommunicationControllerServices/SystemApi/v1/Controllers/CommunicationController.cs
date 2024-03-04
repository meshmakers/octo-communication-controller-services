using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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