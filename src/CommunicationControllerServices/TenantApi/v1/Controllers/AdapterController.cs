using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages adapter configuration
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class AdapterController : ControllerBase
{
    private readonly ILogger<AdapterController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IAdapterService _adapterService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="communicationRepository"></param>
    /// <param name="adapterService">Adapter management service instance</param>
    public AdapterController(ILogger<AdapterController> logger, ICommunicationRepository communicationRepository, IAdapterService adapterService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _adapterService = adapterService;
    }
    
    /// <summary>
    /// Returns a list of all adapters for the tenant
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        var adapters = await _adapterService.GetAdapterSummariesAsync(tenantId);

        return Ok(adapters);
    }

    /// <summary>
    /// Returns the configuration for a specific adapter
    /// </summary>
    /// <param name="adapterRtEntityId">The adapter entity object id</param>
    /// <returns>Configuration object</returns>
    [HttpGet("{adapterRtEntityId}")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItem([Required][FromQuery] RtEntityId adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }

        var config = await _adapterService.GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, false);

        return Ok(config);
    }
    
    /// <summary>
    /// Returns aggregated node descriptors from all connected adapters.
    /// Used by Refinery Studio to populate the visual pipeline editor.
    /// </summary>
    /// <returns>List of node descriptors with JSON schemas</returns>
    [HttpGet("nodes")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult GetNodes()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            var nodeDescriptors = _adapterService.GetAllNodeDescriptors(tenantId);
            return Ok(nodeDescriptors);
        }
        catch (AdapterServiceException e)
        {
            _logger.LogError(e, "Error getting node descriptors");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Returns the composite pipeline JSON Schema for a specific adapter.
    /// Used by Monaco editor for YAML/JSON autocompletion and validation.
    /// </summary>
    /// <param name="adapterRtEntityId">The adapter entity object id</param>
    /// <returns>JSON Schema document</returns>
    [HttpGet("pipeline-schema")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult GetPipelineSchema([Required][FromQuery] RtEntityId adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            var schema = _adapterService.GetPipelineSchema(tenantId, adapterRtEntityId);
            if (schema == null)
            {
                return NotFound(new ErrorResponse { ErrorMessage = "Pipeline schema not available for this adapter" });
            }

            return Content(schema, "application/schema+json");
        }
        catch (AdapterServiceException e)
        {
            _logger.LogError(e, "Error getting pipeline schema");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Returns the recent CPU / memory / thread samples buffered by the controller for
    /// the given adapter. Used by the UI to render live sparklines without persisting
    /// telemetry to MongoDB / CrateDB.
    /// </summary>
    /// <param name="adapterRtEntityId">The adapter entity object id</param>
    /// <param name="since">When provided (UTC), only samples with a strictly later timestamp are returned — used for incremental polling.</param>
    [HttpGet("{adapterRtEntityId}/metrics")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<AdapterMetricsSampleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult GetMetrics([Required][FromRoute] RtEntityId adapterRtEntityId,
        [FromQuery] DateTime? since)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            var samples = _adapterService.GetMetricsSamples(tenantId, adapterRtEntityId, since);
            return Ok(samples);
        }
        catch (AdapterServiceException e)
        {
            _logger.LogDebug(e, "Adapter metrics requested for unknown tenant/adapter");
            return NotFound(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Updates the configuration at an adapter
    /// </summary>
    /// <param name="adapterRtEntityId">The id of the adapter.</param>
    /// <returns></returns>
    [HttpPost("deployUpdate")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeployUpdate([Required][FromQuery] string adapterRtEntityId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty"});
        }
        
        try
        {
            await _adapterService.DeployAdapterConfigurationAsync(tenantId, adapterRtEntityId);
            return NoContent();
        }
        catch (AdapterServiceException e)
        {
            // Includes AdapterNotLoaded (adapter pod not deployed/connected),
            // PipelineNotFound, etc. Surface the specific service message to the
            // client so the Studio can show the real cause instead of a generic 500.
            _logger.LogError(e, "Error deploying adapter configuration");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
        catch (AdapterHubCallbackException e)
        {
            _logger.LogError(e, "Error deploying adapter configuration");
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Wakes a hibernated OnDemand workload (AB#4918): scales it to 1 replica and waits until
    /// it is registered and configured (budget: <c>LifecycleWakeBudgetSeconds</c>). No-op for
    /// AlwaysOn workloads, for tenants without scale-to-zero, and for already-running
    /// workloads. Used by the Studio's "wake now" action and by apps that want to pre-warm an
    /// adapter before issuing requests.
    /// </summary>
    /// <param name="workloadRtId">The runtime id of the workload (adapter or application).</param>
    /// <param name="workloadLifecycleService">The lifecycle service owning the wake gate.</param>
    [HttpPost("{workloadRtId}/wake")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Wake([Required] string workloadRtId,
        [FromServices] IWorkloadLifecycleService workloadLifecycleService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(workloadRtId, out var workloadObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid workloadRtId '{workloadRtId}': must be a 24-character hex ObjectId." });
        }

        try
        {
            await workloadLifecycleService.EnsureWorkloadRunningAsync(tenantId, workloadObjectId);
            return NoContent();
        }
        catch (WorkloadLifecycleServiceException e)
        {
            _logger.LogWarning(e, "Wake of workload '{WorkloadRtId}' failed", workloadRtId);
            return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }
}