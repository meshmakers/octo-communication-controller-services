using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
    private readonly ICommunicationRepository _communicationRepository;
    private readonly ICommunicationEventService _eventService;

    /// <summary>
    /// Constructor
    /// </summary>
    public PipelineController(ILogger<PipelineController> logger,
        ITriggerManagementService triggerManagementService,
        IAdapterService adapterService,
        ICommunicationRepository communicationRepository,
        ICommunicationEventService eventService)
    {
        _logger = logger;
        _triggerManagementService = triggerManagementService;
        _adapterService = adapterService;
        _communicationRepository = communicationRepository;
        _eventService = eventService;
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
    /// <param name="isDryRun">When true (M4-B.2), the adapter executes the pipeline with every
    /// dry-run-honouring Load node suppressing its real side effect; would-be payloads land on
    /// the debug stream instead. Useful for verifying a freshly-authored pipeline against a real
    /// adapter without committing any writes. Default false preserves classic behaviour.</param>
    /// <returns>The pipeline execution id</returns>
    [HttpPost("execute")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse),StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecutePipeline([Required][FromQuery] OctoObjectId pipelineRtId,
        [FromQuery] bool isDryRun = false)
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

            // AB#5126: this endpoint is invoked by an authenticated user, so carry them through as
            // the caller of the FromExecutePipelineCommand pipeline. The route requires a bearer
            // (see [Authorize] above), so a resolved invoker is strongly authenticated (trust=2).
            var caller = BuildExecutePipelineCaller();
            var callerAccessToken = ExtractBearerToken();

            var pipelineExecutionId = await _triggerManagementService.StartExecutePipelineAsync(tenantId, pipelineRtId,
                pipelineInput, isDryRun, caller, callerAccessToken);
            return Ok(pipelineExecutionId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error during execution of pipeline");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message});
        }
    }

    /// <summary>
    ///     Projects the authenticated invoker into a token-free <see cref="ExecutePipelineCaller" />
    ///     (AB#5126). Mirrors the claim mapping the HTTP trigger uses for its verified principal
    ///     (AB#4975). Returns null when the request is unauthenticated (never expected here — the
    ///     route requires a bearer — but kept defensive).
    /// </summary>
    private ExecutePipelineCaller? BuildExecutePipelineCaller()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return new ExecutePipelineCaller
        {
            SubjectId = User.FindFirstValue(JwtClaimTypes.Subject) ?? User.FindFirstValue("client_id"),
            TenantId = User.FindFirstValue(HubConnectionPrincipal.TenantIdClaimType),
            Email = User.FindFirstValue(JwtClaimTypes.Email),
            Name = User.FindFirstValue(JwtClaimTypes.Name) ?? User.FindFirstValue(JwtClaimTypes.PreferredUserName),
            Roles = User.FindAll(JwtClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            // Bearer-authenticated invocation → strongly trusted (mirrors CallerTrustLevel.Strong=2).
            TrustLevel = 2
        };
    }

    /// <summary>
    ///     The raw bearer token of the current request, for delegation on the adapter side (AB#5031),
    ///     or null when the Authorization header is absent or not a Bearer credential. Never logged.
    /// </summary>
    private string? ExtractBearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    /// <summary>
    ///     Reassigns one or more pipelines from their current adapter to a new
    ///     target adapter (bulk). Each pipeline is moved atomically in its own
    ///     repository call; failures on one pipeline do not abort the rest of
    ///     the batch. When <c>Redeploy</c> is set, the controller re-deploys
    ///     every successfully moved pipeline onto the target adapter — that
    ///     deploy may still fail (target adapter offline, definition rejected,
    ///     …), in which case the move stays committed and the failure is
    ///     reported via <c>ErrorMessage</c> as a warning. Audit events are
    ///     written per pipeline regardless of redeploy outcome.
    /// </summary>
    [HttpPatch("move-to-adapter")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(MovePipelinesToAdapterResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MovePipelinesToAdapter(
        [Required] [FromBody] MovePipelinesToAdapterRequestDto body)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (body.PipelineRtIds == null || body.PipelineRtIds.Count == 0)
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = "PipelineRtIds must contain at least one entry" });
        }

        if (!OctoObjectId.TryParse(body.TargetAdapterRtId, out var targetAdapterRtId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"TargetAdapterRtId '{body.TargetAdapterRtId}' is not a valid id" });
        }

        var results = new List<MovePipelineResultDto>(body.PipelineRtIds.Count);

        foreach (var pipelineRtIdStr in body.PipelineRtIds)
        {
            if (!OctoObjectId.TryParse(pipelineRtIdStr, out var pipelineRtId))
            {
                results.Add(new MovePipelineResultDto(pipelineRtIdStr, false, null, null,
                    $"PipelineRtId '{pipelineRtIdStr}' is not a valid id"));
                continue;
            }

            try
            {
                var moveResult = await _communicationRepository.MovePipelineToAdapterAsync(
                    tenantId, pipelineRtId, targetAdapterRtId);

                // Redeploy is best-effort: a failure here does NOT roll the
                // move back. The pipeline already points at the new adapter
                // — the operator can hit "Deploy" manually once the adapter
                // is reachable again.
                string? warnMessage = null;
                var movedToDifferentAdapter =
                    !moveResult.OldAdapterRtEntityId.Equals(moveResult.NewAdapterRtEntityId);
                if (body.Redeploy && movedToDifferentAdapter)
                {
                    try
                    {
                        var pipelineRtEntityId =
                            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
                        await _adapterService.DeployPipelineAsync(tenantId,
                            moveResult.NewAdapterRtEntityId, pipelineRtEntityId);
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning(e,
                            "Pipeline {PipelineRtId} moved to {Adapter} but redeploy failed",
                            pipelineRtId, moveResult.NewAdapterRtEntityId);
                        warnMessage = $"Move OK; redeploy on new adapter failed: {e.Message}";
                    }
                }

                var auditMessage = movedToDifferentAdapter
                    ? $"Pipeline {pipelineRtId} moved from adapter {moveResult.OldAdapterRtEntityId.RtId} to {moveResult.NewAdapterRtEntityId.RtId} (source: User)."
                    : $"Pipeline {pipelineRtId} move requested but already pointed at adapter {moveResult.NewAdapterRtEntityId.RtId} (source: User, no-op).";
                await _eventService.StoreInformationEventAsync(tenantId, auditMessage);

                results.Add(new MovePipelineResultDto(
                    pipelineRtIdStr,
                    true,
                    moveResult.OldAdapterRtEntityId.RtId.ToString(),
                    moveResult.NewAdapterRtEntityId.RtId.ToString(),
                    warnMessage));
            }
            catch (CommunicationRepositoryException e)
            {
                _logger.LogWarning(e,
                    "Failed to move pipeline {PipelineRtId} to adapter {TargetAdapterRtId} in tenant {TenantId}",
                    pipelineRtId, targetAdapterRtId, tenantId);
                results.Add(new MovePipelineResultDto(pipelineRtIdStr, false, null, null, e.Message));
            }
        }

        return Ok(new MovePipelinesToAdapterResponseDto(results));
    }

    /// <summary>
    /// Enables or disables debug capture for a pipeline. Persists the flag and, when the owning
    /// adapter is online, re-pushes its configuration so the change is effective immediately.
    /// </summary>
    /// <param name="pipelineRtId">The runtime id of the pipeline.</param>
    /// <param name="body">The desired debug state.</param>
    [HttpPatch("{pipelineRtId}/debug")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(SetPipelineDebugResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetPipelineDebugging(
        [Required] OctoObjectId pipelineRtId,
        [Required] [FromBody] SetPipelineDebugRequestDto body)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);

        try
        {
            var state = body.Enabled ? "enabled" : "disabled";
            var appliedToRunningAdapter =
                await _adapterService.SetPipelineDebuggingAsync(tenantId, pipelineRtEntityId, body.Enabled);

            var auditMessage = appliedToRunningAdapter
                ? $"Pipeline {pipelineRtId} debugging {state} and applied to the running adapter (source: User)."
                : $"Pipeline {pipelineRtId} debugging {state}; adapter offline, will apply on next deploy (source: User).";
            await _eventService.StoreInformationEventAsync(tenantId, auditMessage);

            return Ok(new SetPipelineDebugResultDto(body.Enabled, appliedToRunningAdapter));
        }
        catch (AdapterHubCallbackException e)
        {
            _logger.LogError(e, "Pipeline debug toggle failed (UnprocessableEntity) for pipeline {PipelineRtId} in tenant {TenantId}",
                pipelineRtId, tenantId);
            return UnprocessableEntity(new ErrorResponse { ErrorMessage = e.Message });
        }
        catch (AdapterServiceException e)
        {
            _logger.LogError(e, "Failed to set debugging for pipeline {PipelineRtId} in tenant {TenantId}",
                pipelineRtId, tenantId);
            return NotFound(new ErrorResponse { ErrorMessage = e.Message });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error setting debugging for pipeline {PipelineRtId} in tenant {TenantId}",
                pipelineRtId, tenantId);
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Gets the persisted debug state of a pipeline.
    /// </summary>
    /// <param name="pipelineRtId">The runtime id of the pipeline.</param>
    [HttpGet("{pipelineRtId}/debug")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(PipelineDebugStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPipelineDebugging([Required] OctoObjectId pipelineRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
        var pipeline = await _communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
        if (pipeline == null)
        {
            return NotFound(new ErrorResponse { ErrorMessage = $"Pipeline '{pipelineRtId}' not found" });
        }

        return Ok(new PipelineDebugStateDto(pipeline.IsDebuggingEnabled ?? false));
    }
}
