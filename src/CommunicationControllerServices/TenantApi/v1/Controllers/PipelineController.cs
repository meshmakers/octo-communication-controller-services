using System.ComponentModel.DataAnnotations;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages edge and mesh pipelines
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineController : ControllerBase
{
    private readonly ILogger<PipelineController> _logger;
    private readonly IPipelineDebugService _pipelineDebugService;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="pipelineDebugService"></param>
    /// <param name="adapterService"></param>
    public PipelineController(ILogger<PipelineController> logger, IPipelineDebugService pipelineDebugService, IAdapterService adapterService)
    {
        _logger = logger;
        _pipelineDebugService = pipelineDebugService;
        _adapterService = adapterService;
    }
    
    /// <summary>
    /// Returns the configuration for a specific adapter
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("debugInfo")]
    public async Task<IActionResult> GetDebugInfo([Required][FromQuery] string pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        var config = await _pipelineDebugService.GetDebugInformation(tenantId, pipelineRtEntityId);

        return Ok(config);
    }
    
    /// <summary>
    /// Deploys the pipeline definition at the corresponding adapter
    /// </summary>
    /// <param name="adapterRtEntityId">The id of the adapter where the pipline should be executed.</param>
    /// <param name="pipelineRtEntityId">The id of the pipeline.</param>
    /// <returns></returns>
    [HttpPost("deploy")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployPipeline([Required][FromQuery] string adapterRtEntityId, [Required][FromQuery] string pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
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
            return UnprocessableEntity(e.Message);
        }
        catch (AdapterServiceException e)
        {
            return NotFound(e.Message);
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}