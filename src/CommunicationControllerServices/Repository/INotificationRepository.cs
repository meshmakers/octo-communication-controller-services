using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// One notification preference that has to be on the schedule
/// </summary>
/// <param name="PreferenceRtId">The preference whose schedule fires</param>
/// <param name="CronExpression">Cron expression of the preference (five or six fields)</param>
/// <param name="TimeZone">Time zone the cron expression is evaluated in; null when the preference has none</param>
/// <param name="HasNeverRun">True while the engine has not run the preference yet</param>
/// <param name="TakesBaseline">True for Onwards, which records its start with an immediate first run</param>
public record NotificationPreferenceSchedule(
    OctoObjectId PreferenceRtId,
    string CronExpression,
    string? TimeZone,
    bool HasNeverRun,
    bool TakesBaseline);

/// <summary>
/// Reads and writes the notification entities the schedule is built from
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Returns the schedules of the preferences whose subscription is deployed
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">Only the preferences of this subscription; null for every deployed subscription</param>
    Task<IReadOnlyList<NotificationPreferenceSchedule>> GetDeployedPreferenceSchedulesAsync(string tenantId,
        OctoObjectId? subscriptionRtId = null);

    /// <summary>
    /// Returns the schedule of one preference; null when it is gone, unscheduled or its subscription is not deployed
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="preferenceRtId">The preference</param>
    Task<NotificationPreferenceSchedule?> GetDeployedPreferenceScheduleAsync(string tenantId, OctoObjectId preferenceRtId);

    /// <summary>
    /// Returns the rtIds of all preferences of a subscription, deployed or not
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">The subscription</param>
    Task<IReadOnlyList<OctoObjectId>> GetPreferenceRtIdsAsync(string tenantId, OctoObjectId subscriptionRtId);

    /// <summary>
    /// Whether a subscription with this rtId exists
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">The subscription</param>
    Task<bool> SubscriptionExistsAsync(string tenantId, OctoObjectId subscriptionRtId);

    /// <summary>
    /// Marks a subscription as deployed at the given moment, or as undeployed when there is none
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">The subscription</param>
    /// <param name="deployedAt">The deployment moment; null undeploys</param>
    Task SetSubscriptionDeployedAsync(string tenantId, OctoObjectId subscriptionRtId, DateTime? deployedAt);

    /// <summary>
    /// Erases every delivery state whose preference no longer exists and returns how many were erased
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    Task<int> DeleteOrphanedDeliveryStatesAsync(string tenantId);
}
