using System.ComponentModel.DataAnnotations;
using System.Text;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages data flows
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DataFlowController : ControllerBase
{
    private readonly ILogger<DataFlowController> _logger;
    private readonly IAdapterService _adapterService;
    private readonly IPipelineExecutionService _pipelineExecutionService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="adapterService"></param>
    /// <param name="pipelineExecutionService"></param>
    public DataFlowController(ILogger<DataFlowController> logger, IAdapterService adapterService,
        IPipelineExecutionService pipelineExecutionService)
    {
        _logger = logger;
        _adapterService = adapterService;
        _pipelineExecutionService = pipelineExecutionService;
    }

    /// <summary>
    /// Updates the configuration at an adapter
    /// </summary>
    /// <param name="dataFlowRtId">The id of the data flow.</param>
    /// <returns></returns>
    [HttpPost("deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployDataFlow([Required][FromQuery] OctoObjectId dataFlowRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            await _adapterService.DeployDataFlowAsync(tenantId, dataFlowRtId);
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

    /// <summary>
    /// Undeploys a data flow from its adapters
    /// </summary>
    /// <param name="dataFlowRtId">The id of the data flow</param>
    /// <returns></returns>
    [HttpPost("undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployDataFlow([Required][FromQuery] OctoObjectId dataFlowRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            await _adapterService.UndeployDataFlowAsync(tenantId, dataFlowRtId);
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

    /// <summary>
    /// Gets the aggregated execution status of a data flow from all child pipeline executions
    /// </summary>
    /// <param name="dataFlowRtId">The id of the data flow</param>
    /// <returns>Aggregated data flow status with per-pipeline details</returns>
    [HttpGet("status")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(DataFlowStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DataFlowStatusDto>> GetDataFlowStatus([Required][FromQuery] OctoObjectId dataFlowRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            var status = await _pipelineExecutionService.GetDataFlowStatusAsync(tenantId, dataFlowRtId);
            return Ok(status);
        }
        catch (CommunicationRepositoryException e)
        {
            return NotFound(new ErrorResponse { ErrorMessage = e.Message });
        }
        catch (Exception e)
        {
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }
}