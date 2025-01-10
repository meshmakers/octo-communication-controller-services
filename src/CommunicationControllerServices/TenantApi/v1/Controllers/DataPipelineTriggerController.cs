using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages data pipeline trigger
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
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
    /// <returns></returns>
    [HttpPost("deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployTrigger()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _triggerManagementService.UpdateScheduleAsync(tenantId);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
    
    /// <summary>
    /// Undeploy the trigger for the tenant
    /// </summary>
    /// <returns></returns>
    [HttpPost("undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployTrigger()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _triggerManagementService.RemoveScheduleAsync(tenantId);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
}