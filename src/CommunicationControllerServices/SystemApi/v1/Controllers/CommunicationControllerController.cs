using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the communication controller itself
/// </summary>
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class CommunicationControllerController: ControllerBase
{
    private readonly ILogger<CommunicationControllerController> _logger;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configurationService"></param>
    internal CommunicationControllerController(ILogger<CommunicationControllerController> logger, IConfigurationService configurationService)
    {
        _logger = logger;
        _configurationService = configurationService;
    }
    
    /// <summary>
    /// Gets the status of tenants with enabled communication controllers
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var config = await _configurationService.ReadConfig();

        return Ok(config);
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
            var config = await _configurationService.ReadConfig();

            var result = config.ToList();
            var item = result.SingleOrDefault(x => x.TenantId == tenantId);
            if (item == null)
            {
                result.Add(new CommunicationControllerStatusDto{ IsEnabled = true, TenantId = tenantId});
            }
            else
            {
                item.IsEnabled = true;
            }

            await _configurationService.WriteConfig(result, tenantId);
            
            return NoContent();
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
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
            var config = await _configurationService.ReadConfig();

            var result = config.ToList();
            var item = result.SingleOrDefault(x => x.TenantId == tenantId);
            if (item != null)
            {
                result.Remove(item);
            }

            await _configurationService.WriteConfig(result, tenantId);
            
            return NoContent();
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
        }
    }
}