using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Deploys notification subscriptions and keeps the notification schedule in line with the preferences
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/notifications")]
[ApiVersion("1.0")]
public class NotificationsController(
    ILogger<NotificationsController> logger,
    INotificationScheduleService notificationScheduleService) : ControllerBase
{
    /// <summary>
    /// Deploys a subscription: its preferences start getting mails on their schedules
    /// </summary>
    /// <param name="subscriptionRtId">The subscription</param>
    /// <response code="404">The subscription does not exist</response>
    /// <response code="409">The subscription is deployed, but the notification engine cannot run</response>
    /// <response code="503">Some preferences could not be put on the schedule</response>
    [HttpPost("subscriptions/{subscriptionRtId}/deploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> DeploySubscription(string subscriptionRtId) =>
        RunForSubscriptionAsync("deploy", subscriptionRtId,
            (tenantId, rtId) => notificationScheduleService.DeploySubscriptionAsync(tenantId, rtId));

    /// <summary>
    /// Undeploys a subscription: mails pause for all its recipients, their preferences are kept
    /// </summary>
    /// <param name="subscriptionRtId">The subscription</param>
    /// <response code="404">The subscription does not exist</response>
    [HttpPost("subscriptions/{subscriptionRtId}/undeploy")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<IActionResult> UndeploySubscription(string subscriptionRtId) =>
        RunForSubscriptionAsync("undeploy", subscriptionRtId,
            (tenantId, rtId) => notificationScheduleService.UndeploySubscriptionAsync(tenantId, rtId));

    /// <summary>
    /// Brings the notification schedule in line, for example after a preference was saved or deleted
    /// </summary>
    /// <param name="preferenceRtId">Only this preference; without it the whole schedule is rebuilt</param>
    /// <response code="503">Some preferences could not be put on the schedule</response>
    [HttpPost("schedule/sync")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SyncSchedule([FromQuery] string? preferenceRtId = null)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (preferenceRtId == null)
        {
            return await RunAsync(tenantId, "sync the notification schedule",
                () => notificationScheduleService.UpdateScheduleAsync(tenantId));
        }

        if (!OctoObjectId.TryParse(preferenceRtId, out var rtId))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = $"'{preferenceRtId}' is not a valid rtId" });
        }

        return await RunAsync(tenantId, $"sync the schedule of preference '{preferenceRtId}'",
            () => notificationScheduleService.SyncPreferenceAsync(tenantId, rtId));
    }

    private async Task<IActionResult> RunForSubscriptionAsync(string action, string subscriptionRtId,
        Func<string, OctoObjectId, Task> run)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        if (!OctoObjectId.TryParse(subscriptionRtId, out var rtId))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = $"'{subscriptionRtId}' is not a valid rtId" });
        }

        return await RunAsync(tenantId, $"{action} subscription '{subscriptionRtId}'", () => run(tenantId, rtId));
    }

    private async Task<IActionResult> RunAsync(string tenantId, string description, Func<Task> run)
    {
        try
        {
            await run();
            return NoContent();
        }
        catch (NotificationScheduleServiceException e)
        {
            logger.LogWarning(e, "[{TenantId}] Failed to {Description}", tenantId, description);
            var body = new ErrorResponse { ErrorMessage = e.Message };
            return e.Kind switch
            {
                NotificationScheduleErrorKind.SubscriptionNotFound => NotFound(body),
                NotificationScheduleErrorKind.EngineUnavailable => Conflict(body),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable, body)
            };
        }
        catch (Exception e)
        {
            logger.LogError(e, "[{TenantId}] Failed to {Description}", tenantId, description);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse { ErrorMessage = e.Message });
        }
    }
}
