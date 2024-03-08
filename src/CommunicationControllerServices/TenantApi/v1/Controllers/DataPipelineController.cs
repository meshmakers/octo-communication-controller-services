using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.ApiErrors;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages adapter configuration
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DataPipelineController : ControllerBase
{
    private readonly ILogger<DataPipelineController> _logger;
    private readonly IPipelineDebugService _pipelineDebugService;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="pipelineDebugService"></param>
    /// <param name="adapterService"></param>
    public DataPipelineController(ILogger<DataPipelineController> logger, IPipelineDebugService pipelineDebugService, IAdapterService adapterService)
    {
        _logger = logger;
        _pipelineDebugService = pipelineDebugService;
        _adapterService = adapterService;
    }
    
    /// <summary>
    /// Returns the configuration for a specific adapter
    /// </summary>
    /// <param name="pipelineRtId">The pipeline entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("{pipelineRtId}/debugInfo")]
    public async Task<IActionResult> GetDebugInfo([Required] OctoObjectId pipelineRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        var config = await _pipelineDebugService.GetDebugInformation(tenantId, pipelineRtId);

        return Ok(config);
    }
    
    /// <summary>
    /// Updates the configuration at an adapter
    /// </summary>
    /// <param name="tenantId">The id of the tenant.</param>
    /// <param name="adapterRtId">The id of the adapter where the pipline should be executed.</param>
    /// <param name="pipelineRtId">The id of the pipeline.</param>
    /// <returns></returns>
    [HttpPost("{pipelineRtId}/deploy/{adapterRtId}")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployPipeline([Required] string tenantId, [Required] OctoObjectId adapterRtId, [Required] OctoObjectId pipelineRtId)
    {
        try
        {
            await _adapterService.DeployPipelineAsync(tenantId, adapterRtId, pipelineRtId);
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