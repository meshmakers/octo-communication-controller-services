using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
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
/// Reads and updates the chart metadata on <c>RtDeployableWorkload</c> entities
/// (Adapter + Application). Used by the CI/CD rollout flow (Epic 3054, Phase 2)
/// to: (a) discover which workloads use a given Helm chart in a tenant, and
/// (b) bump the chart version after a successful CI build. The actual deploy
/// trigger lives on <see cref="PoolController"/> — chart-version update and
/// deploy are intentionally split so an operator (or a smarter CI pipeline) can
/// stage version writes across many tenants before rolling them.
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class WorkloadController : ControllerBase
{
    private readonly ILogger<WorkloadController> _logger;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly ICommunicationEventService _eventService;

    /// <summary>
    /// Constructor.
    /// </summary>
    public WorkloadController(ILogger<WorkloadController> logger,
        ICommunicationRepository communicationRepository,
        ICommunicationEventService eventService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _eventService = eventService;
    }

    /// <summary>
    /// Lists workloads in the tenant whose <c>ChartName</c> matches the query
    /// parameter. Empty array when no workload uses this chart — CI scripts
    /// interpret that as "this tenant has no exposure to the chart, skip".
    /// </summary>
    [HttpGet]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<WorkloadSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([Required][FromQuery] string chartName)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        var workloads = await _communicationRepository.GetWorkloadsByChartNameAsync(tenantId, chartName);
        var dtos = workloads.Select(w => new WorkloadSummaryDto(
            RtId: w.RtId.ToString(),
            Name: w.Name ?? string.Empty,
            CkTypeId: w.CkTypeId?.ToString() ?? string.Empty,
            ChartName: w.ChartName ?? string.Empty,
            CurrentChartVersion: w.ChartVersion ?? string.Empty,
            DeploymentState: w.DeploymentState.ToString()));
        return Ok(dtos);
    }

    /// <summary>
    /// Sets <c>ChartVersion</c> on a single workload. Validates that the
    /// supplied value is non-empty and parseable as a SemVer triple — anything
    /// else is rejected at the controller boundary so a bad CI input never
    /// lands in MongoDB. <strong>Does not trigger a deploy</strong>; the caller
    /// hits <c>POST /pool/workloads/deploy</c> for that, typically right after.
    /// </summary>
    [HttpPatch("{workloadRtId}/chart-version")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateChartVersion(
        [Required] OctoObjectId workloadRtId,
        [Required][FromBody] UpdateChartVersionDto body)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (string.IsNullOrWhiteSpace(body.ChartVersion))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "ChartVersion is required" });
        }

        if (!IsValidSemVer(body.ChartVersion))
        {
            return BadRequest(new ErrorResponse
            {
                ErrorMessage =
                    $"ChartVersion '{body.ChartVersion}' is not a valid SemVer (expected e.g. '1.2.3' or '1.2.3-beta.1')"
            });
        }

        try
        {
            var previousVersion = await _communicationRepository
                .UpdateWorkloadChartVersionAsync(tenantId, workloadRtId, body.ChartVersion);

            // Audit trail: operators inspect this in the event log when a CI
            // rollout misbehaves. Source tag distinguishes CI/CD-driven changes
            // from manual Studio edits.
            var auditMessage = previousVersion is null
                ? $"Chart version for workload {workloadRtId} set to {body.ChartVersion} (source: CI/CD)."
                : $"Chart version for workload {workloadRtId} updated from {previousVersion} to {body.ChartVersion} (source: CI/CD).";
            await _eventService.StoreInformationEventAsync(tenantId, auditMessage);

            return NoContent();
        }
        catch (CommunicationRepositoryException e)
        {
            _logger.LogError(e, "Failed to update chart version for workload {WorkloadRtId} in tenant {TenantId}",
                workloadRtId, tenantId);
            return NotFound(new ErrorResponse { ErrorMessage = e.Message });
        }
    }

    /// <summary>
    /// Cheap, format-only SemVer guard. We deliberately don't pull in a full
    /// SemVer library — the chart-version write itself doesn't care about
    /// ordering, only Helm at deploy time does, and Helm has its own (much
    /// stricter) validation. The intent here is to catch obvious typos before
    /// they reach Mongo, not to be RFC-perfect.
    /// </summary>
    private static bool IsValidSemVer(string value)
    {
        // Accept MAJOR.MINOR.PATCH optionally followed by `-prerelease` and / or
        // `+buildmetadata`. Matches every Helm chart version Octo ships today.
        return System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z\.-]+)?(?:\+[0-9A-Za-z\.-]+)?$");
    }
}
