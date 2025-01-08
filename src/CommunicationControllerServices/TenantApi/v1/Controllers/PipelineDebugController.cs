using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Controller to debug the pipeline
/// </summary>
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineDebugController : ControllerBase
{
    private readonly ILogger<PipelineDebugController> _logger;
    private readonly IPipelineDebugService _pipelineDebugService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="pipelineDebugService"></param>
    public PipelineDebugController(ILogger<PipelineDebugController> logger, IPipelineDebugService pipelineDebugService)
    {
        _logger = logger;
        _pipelineDebugService = pipelineDebugService;
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}")]
    public async Task<IActionResult> GetPipelineExecutionsAsync([Required] RtEntityId pipelineRtEntityId)
    {
        _logger.LogInformation("GetPipelineExecutions");
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        try
        {
            var guids = await _pipelineDebugService.GetPipelineExecutionsAsync(tenantId, pipelineRtEntityId);

            return Ok(guids);
        }
        catch (PipelineDebugInformationNotFoundException e)
        {
            _logger.LogError(e, "Pipeline debug information not found");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(500, new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/latest")]
    public async Task<IActionResult> GetLatestPipelineExecutionAsync([Required] RtEntityId pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        try
        {
            var guids = await _pipelineDebugService.GetLatestPipelineExecutionAsync(tenantId, pipelineRtEntityId);

            return Ok(guids);
        }
        catch (PipelineDebugInformationNotFoundException e)
        {
            _logger.LogError(e, "Pipeline debug information not found");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(500, new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <param name="pipelineExecutionId">The pipeline execution id, that identifies the pipeline execution instance</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/{pipelineExecutionId}")]
    public async Task<IActionResult> GetPipelineExecutionDebugPointNodesAsync([Required] RtEntityId pipelineRtEntityId,
        [Required] Guid pipelineExecutionId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        try
        {
            var debugPointNodes = await _pipelineDebugService.GetPipelineExecutionDebugPointNodesAsync(tenantId,
                pipelineRtEntityId,
                pipelineExecutionId);

            return Ok(debugPointNodes);
        }
        catch (PipelineDebugInformationNotFoundException e)
        {
            _logger.LogError(e, "Pipeline debug information not found");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(500, new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <param name="pipelineExecutionId">The pipeline execution id, that identifies the pipeline execution instance</param>
    /// <param name="nodePath">The path of the node</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/{pipelineExecutionId}/{nodePath}")]
    public async Task<IActionResult> GetDebugPointAsync([Required] RtEntityId pipelineRtEntityId,
        [Required] Guid pipelineExecutionId, [Required] NodePath nodePath)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        try
        {
            var debugPointDto = await _pipelineDebugService.GetDebugPointAsync(tenantId, pipelineRtEntityId,
                pipelineExecutionId, nodePath);

            return Ok(debugPointDto);
        }
        catch (PipelineDebugInformationNotFoundException e)
        {
            _logger.LogError(e, "Pipeline debug information not found");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message});
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(500, new ErrorResponse { ErrorMessage = e.Message});
        }
    }
}