using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.PlugControllerServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the plug controller itself
/// </summary>
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PlugControllerController: ControllerBase
{
    private readonly ILogger<PlugControllerController> _logger;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configurationService"></param>
    internal PlugControllerController(ILogger<PlugControllerController> logger, IConfigurationService configurationService)
    {
        _logger = logger;
        _configurationService = configurationService;
    }
    
    /// <summary>
    /// Gets the status of tenants with enabled plug controllers
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var config = await _configurationService.ReadConfig();

        return Ok(config);
    }

    /// <summary>
    /// Enables the plug controller for a tenant
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
                result.Add(new PlugControllerStatusDto{ IsEnabled = true, TenantId = tenantId});
            }
            else
            {
                item.IsEnabled = true;
            }

            await _configurationService.WriteConfig(result, tenantId);
            
            return NoContent();
        }
        catch (DatabaseException e)
        {
            return Conflict(e.Message);
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
        }
    }
    
    /// <summary>
    /// Disables the plug controller for a tenant
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
        catch (DatabaseException e)
        {
            return Conflict(e.Message);
        }
        catch (TenantException e)
        {
            return Conflict(e.Message);
        }
    }
}