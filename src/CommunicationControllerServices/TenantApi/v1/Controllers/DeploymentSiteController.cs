using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.AspNetCore.Mvc;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationRestFacade;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages deploymentSite deployments and lifecycle.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DeploymentSiteController : ControllerBase
{
    private readonly ILogger<DeploymentSiteController> _logger;
    private readonly IDeploymentSiteService _deploymentSiteService;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="poolService">DeploymentSite management service instance</param>
    /// <param name="configurationService">Enabled state of Communication per tenant</param>
    public DeploymentSiteController(ILogger<DeploymentSiteController> logger, IDeploymentSiteService poolService,
        IConfigurationService configurationService)
    {
        _logger = logger;
        _deploymentSiteService = poolService;
        _configurationService = configurationService;
    }

    /// <summary>
    /// AB#4255: deploying creates operator-managed cluster resources, which must not happen on a
    /// tenant whose Communication is disabled — the tenant delete guard only sees the flag, and a
    /// deploymentSite deployed afterwards would be orphaned. This service does not run the platform's tenant
    /// enabled-gate middleware (it would also gate the adapter hub), so the resource-creating
    /// endpoints check the flag themselves (<see cref="CommunicationEnabledGate" />, shared with the methods of
    /// AB#9050). Undeploy stays open so remediation always works.
    /// </summary>
    private async Task<IActionResult?> RefuseWhileDisabledAsync(string tenantId, string operation) =>
        await CommunicationEnabledGate.RefusalAsync(_configurationService, _logger, tenantId, operation) is { } refusal
            ? Conflict(new OperationFailedErrorDto(refusal))
            : null;

    /// <summary>
    /// The REST answer of a failed deployment method (AB#9050): <c>COMMUNICATION_DISABLED</c> is the 409 REST always answered,
    /// a declared failure of the service the 400 with its message, a platform error (<c>METHOD_*</c>) its documented status.
    /// </summary>
    private IActionResult DeploymentFailure(CkMethodResponse response) =>
        response.Error?.Code == CommunicationMethodErrors.CommunicationDisabled
            ? Conflict(new OperationFailedErrorDto(MessageOf(response.Error) ??
                                                   CommunicationEnabledGate.DisabledMessage(HttpContext.GetTenantId() ?? string.Empty)))
            : Failure(response, error => BadRequest(new ErrorResponse
            {
                ErrorMessage = MessageOf(error) ?? "The deployment operation failed."
            }));

    /// <summary>
    /// Returns a list of all deploymentSites for the tenant
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

        var deploymentSites = await _deploymentSiteService.GetDeploymentSiteSummariesAsync(tenantId);

        return Ok(deploymentSites);
    }

    /// <summary>
    /// Deploys a deploymentSite. For Cloud-environment deploymentSites, this triggers the central
    /// Communication Operator to provision the corresponding DeploymentSite
    /// CR and broker secret. Edge-environment deploymentSites transition state without
    /// any operator notification.
    /// </summary>
    /// <remarks>AB#9050: facade of <c>DeploymentSite.Deploy-1</c> (docs/rest-method-facades.md).</remarks>
    /// <param name="deploymentSiteRtId">The id of the deploymentSite.</param>
    [HttpPost("deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    // 🔴 AB#5303 — the AB#4924 rename renamed the route, this class, the SDK method and the CLI
    // verb, but NOT this query parameter. The SDK sends `deploymentSiteRtId`, model binding found
    // no `poolRtId`, and both endpoints answered every call with HTTP 400 "The poolRtId field is
    // required" - unreachable, and the CLI verb along with them. Found while trying to bring a
    // tenant's deployment site back up on a local kind cluster.
    public async Task<IActionResult> DeployDeploymentSiteAsync([Required][FromQuery] OctoObjectId deploymentSiteRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(DeploymentSiteCkTypeId, SystemCommunicationCkIds.DeploymentSiteDeployMethodId,
                nameof(DeployDeploymentSiteAsync))
            {
                TargetRtId = deploymentSiteRtId.ToString(),
                Parameters = new DeploymentSiteDeployParameters()
            }, User, BearerOf(HttpContext),
            response => response.Success ? NoContent() : DeploymentFailure(response),
            async () =>
            {
                if (await RefuseWhileDisabledAsync(tenantId, "deploymentSite deploy") is { } refusal)
                {
                    return refusal;
                }

                try
                {
                    await _deploymentSiteService.DeployDeploymentSiteAsync(tenantId, deploymentSiteRtId);
                    return NoContent();
                }
                catch (DeploymentSiteServiceException e)
                {
                    _logger.LogError(e, "Error deploying deploymentSite");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
                }
            }, FailureOf);
    }

    /// <summary>
    /// Undeploys a deploymentSite. For Cloud-environment deploymentSites, this notifies the
    /// central Communication Operator to remove the DeploymentSite CR and
    /// broker secret.
    /// </summary>
    /// <remarks>AB#9050: facade of <c>DeploymentSite.Undeploy-1</c> (docs/rest-method-facades.md).</remarks>
    /// <param name="deploymentSiteRtId">The id of the deploymentSite.</param>
    [HttpPost("undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    // 🔴 AB#5303 — the AB#4924 rename renamed the route, this class, the SDK method and the CLI
    // verb, but NOT this query parameter. The SDK sends `deploymentSiteRtId`, model binding found
    // no `poolRtId`, and both endpoints answered every call with HTTP 400 "The poolRtId field is
    // required" - unreachable, and the CLI verb along with them. Found while trying to bring a
    // tenant's deployment site back up on a local kind cluster.
    public async Task<IActionResult> UndeployDeploymentSiteAsync([Required][FromQuery] OctoObjectId deploymentSiteRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(DeploymentSiteCkTypeId, SystemCommunicationCkIds.DeploymentSiteUndeployMethodId,
                nameof(UndeployDeploymentSiteAsync))
            {
                TargetRtId = deploymentSiteRtId.ToString(),
                Parameters = new DeploymentSiteUndeployParameters()
            }, User, BearerOf(HttpContext),
            response => response.Success ? NoContent() : DeploymentFailure(response),
            async () =>
            {
                try
                {
                    await _deploymentSiteService.UndeployDeploymentSiteAsync(tenantId, deploymentSiteRtId);
                    return NoContent();
                }
                catch (DeploymentSiteServiceException e)
                {
                    _logger.LogError(e, "Error undeploying deploymentSite");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
                }
            }, FailureOf);
    }

    /// <summary>
    /// Deploys a single workload (Adapter or Application). Independent of
    /// deploymentSite deploy — the workload's parent deploymentSite must already be deployed,
    /// but this call only triggers the operator's helm-install for the
    /// one workload.
    /// </summary>
    /// <remarks>AB#9050: facade of <c>DeployableWorkload.Deploy-1</c> (docs/rest-method-facades.md).</remarks>
    /// <param name="workloadRtId">The runtime id of the workload.</param>
    [HttpPost("workloads/deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(DeployWorkloadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeployWorkloadAsync([Required][FromQuery] OctoObjectId workloadRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(DeployableWorkloadCkTypeId,
                SystemCommunicationCkIds.DeployableWorkloadDeployMethodId, nameof(DeployWorkloadAsync))
            {
                TargetRtId = workloadRtId.ToString(),
                Parameters = new DeployableWorkloadDeployParameters()
            }, User, BearerOf(HttpContext),
            response =>
            {
                if (!response.Success)
                {
                    return DeploymentFailure(response);
                }

                // AB#7428: 204 as before; 200 with the warning only when an explicit Helm value overrode a typed setting.
                var warning = ValueOf<RtWorkloadDeployResultRecord>(response)?.Warning;
                return string.IsNullOrEmpty(warning) ? NoContent() : Ok(new DeployWorkloadResultDto(warning));
            },
            async () =>
            {
                if (await RefuseWhileDisabledAsync(tenantId, "workload deploy") is { } refusal)
                {
                    return refusal;
                }

                try
                {
                    var warning = await _deploymentSiteService.DeployWorkloadAsync(tenantId, workloadRtId);

                    // AB#7428: 204 as before; 200 with the warning only when an explicit Helm value overrode a typed
                    // CPU/memory setting. Clients that accept any success status keep working.
                    return string.IsNullOrEmpty(warning) ? NoContent() : Ok(new DeployWorkloadResultDto(warning));
                }
                catch (DeploymentSiteServiceException e)
                {
                    _logger.LogError(e, "Error deploying workload");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
                }
            }, FailureOf);
    }

    /// <summary>
    /// Undeploys a single workload (Adapter or Application).
    /// </summary>
    /// <remarks>AB#9050: facade of <c>DeployableWorkload.Undeploy-1</c> (docs/rest-method-facades.md).</remarks>
    /// <param name="workloadRtId">The runtime id of the workload.</param>
    [HttpPost("workloads/undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UndeployWorkloadAsync([Required][FromQuery] OctoObjectId workloadRtId)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(DeployableWorkloadCkTypeId,
                SystemCommunicationCkIds.DeployableWorkloadUndeployMethodId, nameof(UndeployWorkloadAsync))
            {
                TargetRtId = workloadRtId.ToString(),
                Parameters = new DeployableWorkloadUndeployParameters()
            }, User, BearerOf(HttpContext),
            response => response.Success ? NoContent() : DeploymentFailure(response),
            async () =>
            {
                try
                {
                    await _deploymentSiteService.UndeployWorkloadAsync(tenantId, workloadRtId);
                    return NoContent();
                }
                catch (DeploymentSiteServiceException e)
                {
                    _logger.LogError(e, "Error undeploying workload");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
                }
            }, FailureOf);
    }

    /// <summary>
    /// Scales a deployed <c>AdapterPool</c> to the requested number of members (AB#4924).
    /// The request is held inside the deploymentSite's declared <c>MinReplicas..MaxReplicas</c> range; the
    /// response body carries the member count actually requested from the operator, which differs
    /// from the input exactly when the input was outside that range.
    /// </summary>
    /// <remarks>AB#9050: facade of <c>AdapterPool.Scale-1</c> (docs/rest-method-facades.md).</remarks>
    /// <param name="workloadRtId">The runtime id of the AdapterPool workload.</param>
    /// <param name="replicas">Desired member count.</param>
    [HttpPost("workloads/adapter-pool/scale")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(AdapterPoolScaleResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ScaleAdapterPoolAsync([Required][FromQuery] OctoObjectId workloadRtId,
        [Required][FromQuery] int replicas)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        return await HttpContext.CommunicationFacade().ServeAsync<IActionResult>(tenantId,
            new CommunicationMethodCall(AdapterPoolCkTypeId, SystemCommunicationCkIds.AdapterPoolScaleMethodId,
                nameof(ScaleAdapterPoolAsync))
            {
                TargetRtId = workloadRtId.ToString(),
                Parameters = new AdapterPoolScaleParameters { Replicas = replicas }
            }, User, BearerOf(HttpContext),
            response =>
            {
                if (response.Success)
                {
                    // a success without the result record breaks the handler contract: never report a count of 0
                    return ValueOf<RtAdapterPoolScaleResultRecord>(response) is { } scaled
                        ? Ok(new AdapterPoolScaleResultDto(scaled.Replicas))
                        : PlatformError(CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed));
                }

                // the handler's REST check of a negative count answers the REST text; any other validation failure
                // (e.g. a parameter the host could not bind) keeps its own text
                return IsNegativeReplicas(response.Error)
                    ? BadRequest(new ErrorResponse { ErrorMessage = NegativeReplicasMessage })
                    : DeploymentFailure(response);
            },
            async () =>
            {
                if (replicas < 0)
                {
                    return BadRequest(new ErrorResponse { ErrorMessage = NegativeReplicasMessage });
                }

                if (await RefuseWhileDisabledAsync(tenantId, "adapter deploymentSite scale") is { } refusal)
                {
                    return refusal;
                }

                try
                {
                    var effective = await _deploymentSiteService.ScaleAdapterPoolAsync(tenantId, workloadRtId, replicas);
                    return Ok(new AdapterPoolScaleResultDto(effective));
                }
                catch (DeploymentSiteServiceException e)
                {
                    _logger.LogError(e, "Error scaling adapter deploymentSite");
                    return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
                }
            }, FailureOf);
    }

    /// <summary>The 400 text of a negative member count (both paths).</summary>
    internal const string NegativeReplicasMessage = "Replicas must not be negative.";

    /// <summary><c>true</c> for the handler's validation failure of a negative count (field <c>replicas</c>, reason <c>negative</c>).</summary>
    private static bool IsNegativeReplicas(CkMethodError? error) =>
        error?.Code == CkMethodErrorCodes.MethodValidationFailed &&
        error.Parameters.TryGetValue(CkMethodErrorParameters.Field, out var field) && field == "replicas" &&
        error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason) &&
        reason == AdapterPoolScaleHandler.NegativeReplicas;
}

/// <summary>
/// Result of an adapter-pool scale request (AB#4924): the member count actually requested from the
/// operator after the deploymentSite's declared <c>MinReplicas..MaxReplicas</c> range was applied.
/// </summary>
/// <param name="Replicas">Effective member count.</param>
public record AdapterPoolScaleResultDto(int Replicas);

/// <summary>
/// Result of a workload deploy that carries a warning (AB#7428): an explicit Helm value
/// (<c>ValuesYaml</c> or <c>Values[]</c>) overrode a typed CPU/memory setting. The same text is stored in
/// the workload's <c>LastDeploymentWarning</c>.
/// </summary>
/// <param name="Warning">The warning text.</param>
public record DeployWorkloadResultDto(string Warning);
