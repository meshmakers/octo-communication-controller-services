using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Controller to debug the pipeline
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineDebugController : ControllerBase
{
    private readonly ILogger<PipelineDebugController> _logger;
    private readonly IPipelineDebugService _pipelineDebugService;
    private readonly ICommunicationRepository _communicationRepository;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="pipelineDebugService"></param>
    /// <param name="communicationRepository"></param>
    public PipelineDebugController(ILogger<PipelineDebugController> logger, IPipelineDebugService pipelineDebugService,
        ICommunicationRepository communicationRepository)
    {
        _logger = logger;
        _pipelineDebugService = pipelineDebugService;
        _communicationRepository = communicationRepository;
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPipelineExecutionsAsync([Required] RtEntityId pipelineRtEntityId)
    {
        _logger.LogInformation("GetPipelineExecutions");
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            var executions = await _pipelineDebugService.GetPipelineExecutionsAsync(tenantId, pipelineRtEntityId);
            return Ok(executions);
        }
        catch (PipelineDebugInformationNotFoundException)
        {
            // Fallback: query persisted execution history from MongoDB
            try
            {
                var persistedExecutions = await _communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, null, null, 0, 20);
                var result = persistedExecutions.Select(e => new PipelineExecutionDataDto
                {
                    Id = Guid.TryParse(e.ExecutionId, out var id) ? id : Guid.Empty,
                    DateTime = e.StartedAt
                });
                return Ok(result);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogDebug(fallbackEx, "No execution history found for pipeline");
                return Ok(Array.Empty<PipelineExecutionDataDto>());
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/latest")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLatestPipelineExecutionAsync([Required] RtEntityId pipelineRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            var execution = await _pipelineDebugService.GetLatestPipelineExecutionAsync(tenantId, pipelineRtEntityId);
            return Ok(execution);
        }
        catch (PipelineDebugInformationNotFoundException)
        {
            // Fallback: get latest execution from MongoDB
            try
            {
                var persistedExecutions = await _communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, null, null, 0, 1);
                var latest = persistedExecutions.FirstOrDefault();
                if (latest != null)
                {
                    return Ok(new PipelineExecutionDataDto
                    {
                        Id = Guid.TryParse(latest.ExecutionId, out var id) ? id : Guid.Empty,
                        DateTime = latest.StartedAt
                    });
                }
                return NotFound(new ErrorResponse { ErrorMessage = "No executions found" });
            }
            catch
            {
                return NotFound(new ErrorResponse { ErrorMessage = "No executions found" });
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while getting pipeline executions");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    /// Returns cached pipeline executions ids of the given pipeline
    /// </summary>
    /// <param name="pipelineRtEntityId">The pipeline entity object id</param>
    /// <param name="pipelineExecutionId">The pipeline execution id, that identifies the pipeline execution instance</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/{pipelineExecutionId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPipelineExecutionDebugPointNodesAsync([Required] RtEntityId pipelineRtEntityId,
        [Required] Guid pipelineExecutionId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            var debugPointNodes = await _pipelineDebugService.GetPipelineExecutionDebugPointNodesAsync(tenantId,
                pipelineRtEntityId,
                pipelineExecutionId);

            return Ok(debugPointNodes);
        }
        catch (PipelineDebugInformationNotFoundException)
        {
            // No debug points cached for this execution — return empty list
            // This happens when execution was recorded in MongoDB but had no debug mode enabled
            return Ok(Array.Empty<DebugPointNode>());
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
    /// <param name="nodeId">ID of the node</param>
    /// <returns>List of Guids that represent executions</returns>
    [HttpGet("{pipelineRtEntityId}/{pipelineExecutionId}/{nodeId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDebugPointAsync([Required] RtEntityId pipelineRtEntityId,
        [Required] Guid pipelineExecutionId, [Required] NodePath nodeId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        try
        {
            var debugPointDto = await _pipelineDebugService.GetDebugPointDataAsync(tenantId, pipelineRtEntityId,
                pipelineExecutionId, nodeId);

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