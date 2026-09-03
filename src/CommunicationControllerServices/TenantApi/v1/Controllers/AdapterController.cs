using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
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

    /// <summary>
    /// Reconciles the adapter's pipeline service account against its declaration (AB#5111): ensures
    /// the configuration entity and the identity client exist, syncs the client's role edges to the
    /// declared <c>AssignedRoleNames</c>, sets/removes the on-behalf-of grant per
    /// <c>AllowDelegation</c>, and re-derives <c>TenantId</c> / the <c>IssuerUri</c> default.
    /// Idempotent; never rotates an existing secret. The configuration-bound variant lives on
    /// <see cref="ServiceAccountController"/>.
    /// </summary>
    /// <remarks>
    /// 🔴 Security gate: user-initiated trigger, so the declared roles are only materialised when
    /// the caller holds the <c>UserManagement</c> role — the same privilege needed to assign roles
    /// directly. Without it the client is still converged, roles untouched, and the response says
    /// so (<c>RoleChangesSkipped</c>). System triggers (tenant start, workload deploy) materialise
    /// the declaration as-is.
    /// </remarks>
    /// <param name="adapterRtId">The runtime id of the adapter.</param>
    /// <param name="provisioningService">The service owning both sides of the credential.</param>
    /// <param name="eventService">Audit trail.</param>
    [HttpPost("{adapterRtId}/serviceAccount/reconcile")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(ReconcileServiceAccountResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReconcileServiceAccount([Required] string adapterRtId,
        [FromServices] IPipelineServiceAccountProvisioningService provisioningService,
        [FromServices] ICommunicationEventService eventService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(adapterRtId, out var adapterObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid adapterRtId '{adapterRtId}': must be a 24-character hex ObjectId." });
        }

        // Same resolution as the rotate endpoint below: Adapter is polymorphic, so the tenant's
        // adapter list is the caller-friendly lookup.
        var adapters = await _communicationRepository.GetAdaptersAsync(tenantId);
        var adapter = adapters.FirstOrDefault(a => a.RtId == adapterObjectId);
        if (adapter == null)
        {
            return NotFound(new ErrorResponse
                { ErrorMessage = $"Adapter '{adapterRtId}' was not found in tenant '{tenantId}'." });
        }

        try
        {
            var result = await provisioningService.ReconcileAdapterAsync(tenantId, adapter,
                ServiceAccountReconcileContext.User(User.IsInRole(CommonConstants.UserManagementRole)));

            var dto = ServiceAccountController.BuildReconcileDto(result);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Pipeline service account '{result.WellKnownName}' (client '{result.ClientId}') of adapter " +
                $"'{adapter.Name ?? adapterRtId}' ({adapterRtId}) reconciled (source: User): {result.Outcome}." +
                $"{(result.RoleChangesSkipped ? " Declared roles were skipped." : string.Empty)}");

            return Ok(dto);
        }
        catch (Exception e)
        {
            // Deliberately broad, mirroring the rotate endpoint: the reconcile spans the identity
            // bus and the tenant repository, and the caller must learn that it did NOT complete.
            // The message never contains a secret.
            _logger.LogError(e, "Reconciling the pipeline service account of adapter '{AdapterRtId}' failed",
                adapterRtId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Reconciling the pipeline service account failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Read-only identity-health aggregate of the adapter's pipeline service account (AB#5112):
    /// one answer covering everything the AB#5111 reconcile converges — the
    /// <c>PipelineServiceAccount</c> association, configuration completeness, identity-client
    /// existence, role drift against the declaration, the on-behalf-of grant, tenant and issuer.
    /// The configuration-bound variant lives on <see cref="ServiceAccountController"/>.
    /// </summary>
    /// <remarks>
    /// Never returns the secret — the <c>secret</c> check reports presence only. Degrades instead
    /// of failing: an unreachable identity service turns the identity-backed checks into
    /// <c>Unknown</c> rather than a 5xx.
    /// </remarks>
    /// <param name="adapterRtId">The runtime id of the adapter.</param>
    /// <param name="healthService">The aggregate evaluator.</param>
    [HttpGet("{adapterRtId}/serviceAccount/health")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(ServiceAccountHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetServiceAccountHealth([Required] string adapterRtId,
        [FromServices] IServiceAccountHealthService healthService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(adapterRtId, out var adapterObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid adapterRtId '{adapterRtId}': must be a 24-character hex ObjectId." });
        }

        // Same resolution as the reconcile / rotate endpoints: Adapter is polymorphic, so the
        // tenant's adapter list is the caller-friendly lookup.
        var adapters = await _communicationRepository.GetAdaptersAsync(tenantId);
        var adapter = adapters.FirstOrDefault(a => a.RtId == adapterObjectId);
        if (adapter == null)
        {
            return NotFound(new ErrorResponse
                { ErrorMessage = $"Adapter '{adapterRtId}' was not found in tenant '{tenantId}'." });
        }

        try
        {
            return Ok(await healthService.GetAdapterHealthAsync(tenantId, adapter));
        }
        catch (Exception e)
        {
            // Only repository-level failures land here — identity degradation is absorbed into
            // Unknown checks by the service. The message never contains a secret.
            _logger.LogError(e, "Evaluating the pipeline service account health of adapter '{AdapterRtId}' failed",
                adapterRtId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Evaluating the pipeline service account health failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Rotates the client secret of the adapter's pipeline service account (AB#5032): a fresh secret
    /// is hashed into the identity client and written into the tenant's
    /// <c>ServiceAccountConfiguration</c> in one deliberate step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why here and not only in the CLI: the controller is the only component that owns <b>both</b>
    /// sides of the credential — it is the sole producer of the identity client (over the
    /// distribution event hub; the identity REST API is unreachable without the very
    /// client-credentials identity being rotated) and the writer of the configuration entity.
    /// A CLI-side rotation would have to reproduce that pairing over two services and could leave
    /// the two halves apart. Putting the verb here means CLI, MCP and Studio can all reach it
    /// through the existing communication service client, and the audit event is written where the
    /// change happens.
    /// </para>
    /// <para>
    /// 🔴 The response carries no secret, and the new secret only takes effect once the adapter's
    /// pipelines / data flows are redeployed — the adapter caches them at pipeline registration.
    /// </para>
    /// </remarks>
    /// <param name="adapterRtId">The runtime id of the adapter.</param>
    /// <param name="provisioningService">The service owning both sides of the credential.</param>
    /// <param name="eventService">Audit trail.</param>
    [HttpPost("{adapterRtId}/serviceAccount/rotateSecret")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(RotateServiceAccountSecretResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RotateServiceAccountSecret([Required] string adapterRtId,
        [FromServices] IPipelineServiceAccountProvisioningService provisioningService,
        [FromServices] ICommunicationEventService eventService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(adapterRtId, out var adapterObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid adapterRtId '{adapterRtId}': must be a 24-character hex ObjectId." });
        }

        // Resolved through the tenant's adapter list rather than by RtEntityId: Adapter is
        // polymorphic (RtMeshAdapter and friends), so the caller would have to know the concrete
        // CkTypeId to build an RtEntityId. A tenant has a handful of adapters.
        var adapters = await _communicationRepository.GetAdaptersAsync(tenantId);
        var adapter = adapters.FirstOrDefault(a => a.RtId == adapterObjectId);
        if (adapter == null)
        {
            return NotFound(new ErrorResponse
                { ErrorMessage = $"Adapter '{adapterRtId}' was not found in tenant '{tenantId}'." });
        }

        try
        {
            var result = await provisioningService.RotateAdapterSecretAsync(tenantId, adapter);

            var message = result.RequiresPipelineRedeploy
                ? $"The client secret of pipeline service account '{result.ClientId}' was rotated. " +
                  "🔴 Redeploy the pipelines / data flows of this adapter — the adapter caches the credentials " +
                  "in the pipeline configuration at registration time, so until then they still present the old secret."
                : $"Adapter '{adapter.Name ?? adapterRtId}' had no pipeline service account; " +
                  $"'{result.ClientId}' was provisioned instead. Nothing was invalidated.";

            await eventService.StoreInformationEventAsync(tenantId,
                $"Pipeline service account secret rotated for adapter '{adapter.Name ?? adapterRtId}' " +
                $"({adapterRtId}), client '{result.ClientId}' (source: User). {message}");

            // The shape lives in Communication.Contracts so the service and every client speak the
            // same one; a controller-local copy drifted from it the moment the SDK gained its own.
            return Ok(new RotateServiceAccountSecretResultDto(
                result.ClientId,
                result.WellKnownName,
                result.WasCreated,
                result.RequiresPipelineRedeploy,
                message));
        }
        catch (Exception e)
        {
            // Deliberately broad: the rotation spans the identity bus and the tenant repository, and
            // whatever went wrong the caller must learn that the rotation did NOT happen rather than
            // be left guessing whether the old secret is still valid. The message never contains a
            // secret — neither the service nor DistClientDto.ToString() emits one.
            _logger.LogError(e, "Rotating the pipeline service account secret of adapter '{AdapterRtId}' failed",
                adapterRtId);

            try
            {
                await eventService.StoreErrorEventAsync(tenantId,
                    $"Rotating the pipeline service account secret of adapter '{adapter.Name ?? adapterRtId}' " +
                    $"({adapterRtId}) failed: {e.Message}. The previous secret remains in effect.");
            }
            catch (Exception eventException)
            {
                _logger.LogWarning(eventException, "Could not store the rotation failure event");
            }

            return BadRequest(new ErrorResponse
            {
                ErrorMessage = $"Rotating the pipeline service account secret failed: {e.Message}. " +
                               "The previous secret remains in effect."
            });
        }
    }
}