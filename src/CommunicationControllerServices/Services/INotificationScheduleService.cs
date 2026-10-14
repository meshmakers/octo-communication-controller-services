using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Keeps the schedule of the notification engine in line with the notification preferences
/// </summary>
public interface INotificationScheduleService
{
    /// <summary>
    /// Rebuilds the tenant's notification schedule: one recurring tick per preference of a deployed subscription
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    Task UpdateScheduleAsync(string tenantId);

    /// <summary>
    /// Brings one preference's schedule in line after it was saved or deleted, without rebuilding the others
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="preferenceRtId">The preference</param>
    Task SyncPreferenceAsync(string tenantId, OctoObjectId preferenceRtId);

    /// <summary>
    /// Removes the tenant's whole notification schedule, for example when Communication is disabled
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    Task RemoveScheduleAsync(string tenantId);

    /// <summary>
    /// Deploys a subscription: records the moment and rebuilds the schedule
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">The subscription</param>
    Task DeploySubscriptionAsync(string tenantId, OctoObjectId subscriptionRtId);

    /// <summary>
    /// Undeploys a subscription and rebuilds the schedule; its preferences are kept
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="subscriptionRtId">The subscription</param>
    Task UndeploySubscriptionAsync(string tenantId, OctoObjectId subscriptionRtId);
}
