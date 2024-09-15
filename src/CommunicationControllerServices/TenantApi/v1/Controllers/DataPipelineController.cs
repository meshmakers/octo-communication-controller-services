using System.ComponentModel.DataAnnotations;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages data pipelines
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DataPipelineController : ControllerBase
{
    private readonly ILogger<DataPipelineController> _logger;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="adapterService"></param>
    public DataPipelineController(ILogger<DataPipelineController> logger, IAdapterService adapterService)
    {
        _logger = logger;
        _adapterService = adapterService;
    }
    
    /// <summary>
    /// Updates the configuration at an adapter
    /// </summary>
    /// <param name="dataPipelineRtId">The id of the data pipeline.</param>
    /// <returns></returns>
    [HttpPost("deploy")]
    //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> DeployDataPipeline([Required][FromQuery] OctoObjectId dataPipelineRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }
        
        try
        {
            await _adapterService.DeployDataPipelineAsync(tenantId, dataPipelineRtId);
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