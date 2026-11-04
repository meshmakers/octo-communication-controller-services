using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
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
            // Get debug executions from in-memory cache (if any)
            IEnumerable<PipelineExecutionDataDto> debugExecutions;
            try
            {
                debugExecutions = await _pipelineDebugService.GetPipelineExecutionsAsync(tenantId, pipelineRtEntityId);
            }
            catch (PipelineDebugInformationNotFoundException)
            {
                debugExecutions = [];
            }

            // Get persisted executions from MongoDB
            IEnumerable<PipelineExecutionDataDto> persistedExecutions;
            try
            {
                var dbExecutions = await _communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, null, null, 0, 20);
                persistedExecutions = dbExecutions.Select(ToExecutionData).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load persisted executions from database for pipeline debug history");
                persistedExecutions = [];
            }

            // Merge: debug executions take priority over persisted ones (by ID)
            // The debug cache knows no status; keep the failure kind (AB#8156) of the persisted twin.
            var persistedKinds = persistedExecutions
                .Where(p => p.FailureKind is not null)
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().FailureKind);
            debugExecutions = debugExecutions.Select(d =>
                d.FailureKind is null && persistedKinds.TryGetValue(d.Id, out var kind)
                    ? d with { FailureKind = kind }
                    : d).ToList();

            var debugIds = new HashSet<Guid>(debugExecutions.Select(d => d.Id));
            var merged = debugExecutions
                .Concat(persistedExecutions.Where(p => !debugIds.Contains(p.Id)))
                .OrderByDescending(e => e.DateTime)
                .Take(20);

            return Ok(merged);
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
            return Ok(await WithPersistedFailureKindAsync(tenantId, execution));
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
                    return Ok(ToExecutionData(latest));
                }
                return NotFound(new ErrorResponse { ErrorMessage = "No executions found" });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load latest execution from database for debug fallback");
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

    /// <summary>
    /// Maps a persisted execution onto the REST shape. <c>FailureKind</c> (AB#8156) is set only for a failed
    /// execution that stored one.
    /// </summary>
    internal static PipelineExecutionDataDto ToExecutionData(RtPipelineExecution e)
    {
        return new PipelineExecutionDataDto
        {
            Id = Guid.TryParse(e.ExecutionId, out var id) ? id : Guid.Empty,
            // AB#4924: a Queued execution has no StartedAt; its QueuedAt is the moment it
            // became visible, which is what the history should order it by.
            DateTime = e.StartedAt ?? e.QueuedAt ?? DateTime.MinValue,
            Status = (PipelineExecutionStatus)(int)e.Status,
            DurationMs = e.DurationMs,
            ErrorMessage = e.ErrorMessage,
            FailureKind = PipelineExecutionFailureKindMapping.FromCk(e.Status, e.FailureKind),
            HasDebugData = false
        };
    }

    // The debug cache carries no status and no failure kind; look the kind up on the persisted execution.
    private async Task<PipelineExecutionDataDto> WithPersistedFailureKindAsync(string tenantId,
        PipelineExecutionDataDto execution)
    {
        if (execution.FailureKind is not null)
        {
            return execution;
        }

        try
        {
            var persisted = await _communicationRepository.GetPipelineExecutionAsync(tenantId, execution.Id.ToString());
            var kind = persisted is null
                ? null
                : PipelineExecutionFailureKindMapping.FromCk(persisted.Status, persisted.FailureKind);
            return kind is null ? execution : execution with { FailureKind = kind };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load the failure kind of execution {ExecutionId}", execution.Id);
            return execution;
        }
    }
}
