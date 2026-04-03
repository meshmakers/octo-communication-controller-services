using System.ComponentModel.DataAnnotations;
using System.Text;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages edge and mesh pipelines
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineController : ControllerBase
{
    private readonly ILogger<PipelineController> _logger;
    private readonly ITriggerManagementService _triggerManagementService;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="triggerManagementService"></param>
    /// <param name="adapterService"></param>
    public PipelineController(ILogger<PipelineController> logger, 
        ITriggerManagementService triggerManagementService, IAdapterService adapterService)
    {
        _logger = logger;
        _triggerManagementService = triggerManagementService;
        _adapterService = adapterService;
    }

    /// <summary>
    /// Retrieves the deployment state of a pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The id of the pipeline.</param>
    /// <returns></returns>
    [HttpGet("status")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDeploymentState([Required][FromQuery] RtEntityId pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            var deploymentState = await _adapterService.GetPipelineDeploymentStateAsync(tenantId, pipelineRtEntityId);
            return Ok(deploymentState);
        }
        catch (AdapterServiceException e)
        {
            _logger.LogError(e, "Pipeline deployment state retrieval failed (NotFound)");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during retrieval of pipeline deployment state");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
    
    
    /// <summary>
    /// Deploys the pipeline definition at the corresponding adapter
    /// </summary>
    /// <param name="adapterRtEntityId">The id of the adapter where the pipeline should be executed.</param>
    /// <param name="pipelineRtEntityId">The id of the pipeline.</param>
    /// <returns></returns>
    [HttpPost("deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployPipeline([Required][FromQuery] string adapterRtEntityId, [Required][FromQuery] string pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            
            var pipelineDefinition = await reader.ReadToEndAsync();
            await _adapterService.DeployPipelineAsync(tenantId, adapterRtEntityId, pipelineRtEntityId,
                pipelineDefinition);
            return NoContent();
        }
        catch (AdapterHubCallbackException e)
        {
            _logger.LogError(e, "Pipeline deployment failed (UnprocessableEntity)");
            return UnprocessableEntity(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (AdapterServiceException e)
        {
            _logger.LogError(e, "Pipeline deployment failed (NotFound)");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during deployment of pipeline");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
    
    /// <summary>
    /// Deploys the pipeline definition at the corresponding adapter
    /// </summary>
    /// <param name="pipelineRtId">The runtime id of the pipeline to execute.</param>
    /// <returns>The pipeline execution id</returns>
    [HttpPost("execute")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecutePipeline([Required][FromQuery] OctoObjectId pipelineRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);

            var pipelineInput = await reader.ReadToEndAsync();
            var pipelineExecutionId = await _triggerManagementService.StartExecutePipelineAsync(tenantId, pipelineRtId,
                pipelineInput);
            return Ok(pipelineExecutionId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during execution of pipeline");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
}