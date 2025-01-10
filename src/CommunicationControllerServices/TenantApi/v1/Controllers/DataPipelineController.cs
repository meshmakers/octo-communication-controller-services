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
/// Manages data pipelines
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
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
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployDataPipeline([Required][FromQuery] OctoObjectId dataPipelineRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _adapterService.DeployDataPipelineAsync(tenantId, dataPipelineRtId);
            return NoContent();
        }
        catch (AdapterHubCallbackException e)
        {
            return UnprocessableEntity(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (AdapterServiceException e)
        {
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }
}