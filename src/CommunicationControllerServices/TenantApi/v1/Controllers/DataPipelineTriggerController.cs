using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages data pipeline trigger
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DataPipelineTriggerController : ControllerBase
{
    private readonly ILogger<DataPipelineTriggerController> _logger;
    private readonly ITriggerManagementService _triggerManagementService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="triggerManagementService">Trigger management service</param>
    public DataPipelineTriggerController(ILogger<DataPipelineTriggerController> logger, ITriggerManagementService triggerManagementService)
    {
        _logger = logger;
        _triggerManagementService = triggerManagementService;
    }
    
    /// <summary>
    /// Deploy the trigger for the tenant
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <returns></returns>
    [HttpPost("deploy")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployTrigger([Required] string tenantId)
    {
        try
        {
            await _triggerManagementService.UpdateScheduleAsync(tenantId);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
    
    /// <summary>
    /// Unddeploy the trigger for the tenant
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <returns></returns>
    [HttpPost("undeploy")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> UndeployTrigger([Required] string tenantId)
    {
        try
        {
            await _triggerManagementService.RemoveScheduleAsync(tenantId);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}