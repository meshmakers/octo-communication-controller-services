using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationRestFacade;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages pipeline trigger
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineTriggerController : ControllerBase
{
    private readonly ILogger<PipelineTriggerController> _logger;
    private readonly ITriggerManagementService _triggerManagementService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="triggerManagementService">Trigger management service</param>
    public PipelineTriggerController(ILogger<PipelineTriggerController> logger, ITriggerManagementService triggerManagementService)
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

        // AB#9050: facade of the static PipelineTrigger.DeployAll-1 (docs/rest-method-facades.md). A failure answers 400 as
        // before, with the method's caller-safe text.
        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(PipelineTriggerCkTypeId, SystemCommunicationCkIds.PipelineTriggerDeployAllMethodId,
                nameof(DeployTrigger)) { Parameters = new PipelineTriggerDeployAllParameters() },
            User, BearerOf(HttpContext),
            response => response.Success
                ? NoContent()
                : Failure(response, error => BadRequest(new ErrorResponse
                {
                    ErrorMessage = MessageOf(error) ?? "The pipeline triggers could not be deployed."
                })),
            async () =>
            {
                try
                {
                    await _triggerManagementService.UpdateScheduleAsync(tenantId);
                    return NoContent();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to deploy triggers for tenant");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
                }
            }, FailureOf);
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

        // AB#9050: facade of the static PipelineTrigger.UndeployAll-1 (docs/rest-method-facades.md).
        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(PipelineTriggerCkTypeId,
                SystemCommunicationCkIds.PipelineTriggerUndeployAllMethodId, nameof(UndeployTrigger))
            {
                Parameters = new PipelineTriggerUndeployAllParameters()
            },
            User, BearerOf(HttpContext),
            response => response.Success
                ? NoContent()
                : Failure(response, error => BadRequest(new ErrorResponse
                {
                    ErrorMessage = MessageOf(error) ?? "The pipeline triggers could not be undeployed."
                })),
            async () =>
            {
                try
                {
                    await _triggerManagementService.RemoveScheduleAsync(tenantId);
                    return NoContent();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to undeploy triggers for tenant");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
                }
            }, FailureOf);
    }
}