using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
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
/// Configuration-bound management of pipeline service accounts (AB#5111): reconcile a
/// <c>ServiceAccountConfiguration</c> against its declaration and rotate its secret — addressed by
/// the configuration's rtId, for callers (Studio's configuration view, blueprint tooling) that hold
/// the configuration rather than an adapter. The adapter-scoped variants live on
/// <see cref="AdapterController"/>.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class ServiceAccountController : ControllerBase
{
    private readonly ILogger<ServiceAccountController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPipelineServiceAccountProvisioningService _provisioningService;
    private readonly ICommunicationEventService _eventService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="communicationRepository">Tenant repository access</param>
    /// <param name="provisioningService">The service owning both sides of the credential</param>
    /// <param name="eventService">Audit trail</param>
    public ServiceAccountController(ILogger<ServiceAccountController> logger,
        ICommunicationRepository communicationRepository,
        IPipelineServiceAccountProvisioningService provisioningService,
        ICommunicationEventService eventService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _provisioningService = provisioningService;
        _eventService = eventService;
    }

    /// <summary>
    /// Reconciles a <c>ServiceAccountConfiguration</c> against its declaration (AB#5111): ensures
    /// the identity client exists with the hashed secret, syncs its role edges to the declared
    /// <c>AssignedRoleNames</c>, sets/removes the on-behalf-of grant per <c>AllowDelegation</c>,
    /// and re-derives <c>TenantId</c> / the <c>IssuerUri</c> default. Idempotent; never rotates an
    /// existing secret.
    /// </summary>
    /// <remarks>
    /// 🔴 Security gate: this is a user-initiated trigger, so the declared roles are only
    /// materialised when the caller holds the <c>UserManagement</c> role — the privilege needed to
    /// assign roles directly. Without it the client is still converged, roles untouched, and the
    /// response says so (<c>RoleChangesSkipped</c>).
    /// </remarks>
    /// <param name="configurationRtId">The runtime id of the <c>ServiceAccountConfiguration</c>.</param>
    [HttpPost("reconcile")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(ReconcileServiceAccountResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reconcile([Required][FromQuery] string configurationRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(configurationRtId, out var configurationObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid configurationRtId '{configurationRtId}': must be a 24-character hex ObjectId." });
        }

        var configuration = await _communicationRepository.GetServiceAccountByRtIdAsync(tenantId,
            configurationObjectId);
        if (configuration == null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorMessage =
                    $"ServiceAccountConfiguration '{configurationRtId}' was not found in tenant '{tenantId}'."
            });
        }

        try
        {
            var result = await _provisioningService.ReconcileConfigurationAsync(tenantId, configuration,
                ServiceAccountReconcileContext.User(User.HasRole(CommonConstants.UserManagementRole)));

            var dto = BuildReconcileDto(result);

            await _eventService.StoreInformationEventAsync(tenantId,
                $"Pipeline service account '{result.WellKnownName}' (client '{result.ClientId}') reconciled " +
                $"(source: User): {result.Outcome}.{(result.RoleChangesSkipped ? " Declared roles were skipped." : string.Empty)}");

            return Ok(dto);
        }
        catch (Exception e)
        {
            // Deliberately broad, mirroring the rotate endpoint: the reconcile spans the identity
            // bus and the tenant repository, and the caller must learn that it did NOT complete.
            // The message never contains a secret.
            _logger.LogError(e, "Reconciling service account configuration '{ConfigurationRtId}' failed",
                configurationRtId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Reconciling the pipeline service account failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Read-only identity-health aggregate of a <c>ServiceAccountConfiguration</c> (AB#5112): one
    /// answer covering everything the AB#5111 reconcile converges — configuration completeness,
    /// identity-client existence, role drift against the declaration, the on-behalf-of grant,
    /// tenant and issuer. The adapter-scoped variant (which additionally checks the association)
    /// lives on <see cref="AdapterController"/>.
    /// </summary>
    /// <remarks>
    /// Never returns the secret — the <c>secret</c> check reports presence only. Degrades instead
    /// of failing: an unreachable identity service turns the identity-backed checks into
    /// <c>Unknown</c> rather than a 5xx.
    /// </remarks>
    /// <param name="configurationRtId">The runtime id of the <c>ServiceAccountConfiguration</c>.</param>
    /// <param name="healthService">The aggregate evaluator.</param>
    [HttpGet("{configurationRtId}/health")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(ServiceAccountHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetHealth([Required] string configurationRtId,
        [FromServices] IServiceAccountHealthService healthService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(configurationRtId, out var configurationObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid configurationRtId '{configurationRtId}': must be a 24-character hex ObjectId." });
        }

        var configuration = await _communicationRepository.GetServiceAccountByRtIdAsync(tenantId,
            configurationObjectId);
        if (configuration == null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorMessage =
                    $"ServiceAccountConfiguration '{configurationRtId}' was not found in tenant '{tenantId}'."
            });
        }

        try
        {
            return Ok(await healthService.GetConfigurationHealthAsync(tenantId, configuration));
        }
        catch (Exception e)
        {
            // Only repository-level failures land here — identity degradation is absorbed into
            // Unknown checks by the service. The message never contains a secret.
            _logger.LogError(e, "Evaluating the health of service account configuration '{ConfigurationRtId}' failed",
                configurationRtId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Evaluating the pipeline service account health failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Read-only rights analysis of a <c>ServiceAccountConfiguration</c> (AB#5113): joins the CK
    /// types touched by every pipeline whose effective account is this configuration (its
    /// adapter's default pipelines minus those overriding away, plus pipelines overriding to it)
    /// with the tenant's data policies/permissions, and computes the role delta against the
    /// AB#5111 declaration. The adapter-scoped variant lives on <see cref="AdapterController"/>.
    /// </summary>
    /// <remarks>
    /// Side-effect free and robust by contract: an unparsable pipeline definition becomes a
    /// warning entry, dynamic type references are reported as not analyzable, and an empty
    /// pipeline set returns an empty-but-valid result.
    /// </remarks>
    /// <param name="configurationRtId">The runtime id of the <c>ServiceAccountConfiguration</c>.</param>
    /// <param name="rightsAnalysisService">The analysis evaluator.</param>
    [HttpGet("{configurationRtId}/rightsAnalysis")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(ServiceAccountRightsAnalysisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetRightsAnalysis([Required] string configurationRtId,
        [FromServices] IServiceAccountRightsAnalysisService rightsAnalysisService)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(configurationRtId, out var configurationObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid configurationRtId '{configurationRtId}': must be a 24-character hex ObjectId." });
        }

        var configuration = await _communicationRepository.GetServiceAccountByRtIdAsync(tenantId,
            configurationObjectId);
        if (configuration == null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorMessage =
                    $"ServiceAccountConfiguration '{configurationRtId}' was not found in tenant '{tenantId}'."
            });
        }

        try
        {
            return Ok(await rightsAnalysisService.AnalyzeConfigurationAsync(tenantId, configuration));
        }
        catch (Exception e)
        {
            // Only repository-level failures land here — pipeline parsing problems are absorbed
            // into warning entries by the service. The message never contains a secret.
            _logger.LogError(e,
                "Rights analysis of service account configuration '{ConfigurationRtId}' failed",
                configurationRtId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"The pipeline service account rights analysis failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Rotates the client secret of a <c>ServiceAccountConfiguration</c> (AB#5111) — the
    /// configuration-bound variant of the adapter-scoped
    /// <c>POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rotateSecret</c> (AB#5032), sharing
    /// the same core logic and consistency ordering. Roles are never touched by a rotation.
    /// </summary>
    /// <remarks>
    /// 🔴 The response carries no secret, and the new secret only takes effect once the pipelines /
    /// data flows using this account are redeployed — the adapter caches the credentials at
    /// pipeline registration.
    /// </remarks>
    /// <param name="configurationRtId">The runtime id of the <c>ServiceAccountConfiguration</c>.</param>
    [HttpPost("{configurationRtId}/rotateSecret")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(RotateServiceAccountSecretResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RotateSecret([Required] string configurationRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(configurationRtId, out var configurationObjectId))
        {
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Invalid configurationRtId '{configurationRtId}': must be a 24-character hex ObjectId." });
        }

        var configuration = await _communicationRepository.GetServiceAccountByRtIdAsync(tenantId,
            configurationObjectId);
        if (configuration == null)
        {
            return NotFound(new ErrorResponse
            {
                ErrorMessage =
                    $"ServiceAccountConfiguration '{configurationRtId}' was not found in tenant '{tenantId}'."
            });
        }

        try
        {
            var result = await _provisioningService.RotateConfigurationSecretAsync(tenantId, configuration);

            var message = result.RequiresPipelineRedeploy
                ? $"The client secret of pipeline service account '{result.ClientId}' was rotated. " +
                  "🔴 Redeploy the pipelines / data flows using this account — the adapter caches the credentials " +
                  "in the pipeline configuration at registration time, so until then they still present the old secret."
                : $"Configuration '{result.WellKnownName}' had no pipeline service account; " +
                  $"'{result.ClientId}' was provisioned instead. Nothing was invalidated.";

            await _eventService.StoreInformationEventAsync(tenantId,
                $"Pipeline service account secret rotated for configuration '{result.WellKnownName}' " +
                $"({configurationRtId}), client '{result.ClientId}' (source: User). {message}");

            return Ok(new RotateServiceAccountSecretResultDto(
                result.ClientId,
                result.WellKnownName,
                result.WasCreated,
                result.RequiresPipelineRedeploy,
                message));
        }
        catch (Exception e)
        {
            // Same contract as the adapter-scoped rotation: whatever went wrong, the caller must
            // learn that the rotation did NOT happen. The message never contains a secret.
            _logger.LogError(e,
                "Rotating the secret of service account configuration '{ConfigurationRtId}' failed",
                configurationRtId);

            try
            {
                await _eventService.StoreErrorEventAsync(tenantId,
                    $"Rotating the pipeline service account secret of configuration '{configurationRtId}' " +
                    $"failed: {e.Message}. The previous secret remains in effect.");
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

    /// <summary>
    /// The response shape both reconcile endpoints (this one and the adapter-scoped one) produce —
    /// kept in one place so they cannot drift.
    /// </summary>
    internal static ReconcileServiceAccountResultDto BuildReconcileDto(ServiceAccountReconcileResult result)
    {
        var message = result.Outcome switch
        {
            PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned =>
                $"Service account '{result.WellKnownName}' already matched its declaration; the identity client " +
                "was re-converged.",
            PipelineServiceAccountProvisioningOutcome.Provisioned =>
                $"Service account '{result.WellKnownName}' (client '{result.ClientId}') was provisioned with the " +
                "declaration defaults.",
            _ =>
                $"Service account '{result.WellKnownName}' (client '{result.ClientId}') was repaired to match its " +
                "declaration."
        };

        if (result.RoleChangesSkipped)
        {
            message += $" 🔴 The declared roles were NOT materialised: the caller lacks the " +
                       $"'{CommonConstants.UserManagementRole}' role. The next system-initiated reconcile " +
                       "(tenant start, workload deploy) materialises them.";
        }

        return new ReconcileServiceAccountResultDto(
            result.Outcome.ToString(),
            result.ClientId,
            result.WellKnownName,
            result.RoleChangesSkipped,
            message);
    }
}
