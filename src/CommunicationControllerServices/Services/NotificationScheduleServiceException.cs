using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// What went wrong in the notification schedule service
/// </summary>
public enum NotificationScheduleErrorKind
{
    /// <summary>The subscription does not exist</summary>
    SubscriptionNotFound,

    /// <summary>The subscription changed, but the notification engine cannot run</summary>
    EngineUnavailable,

    /// <summary>Some preferences could not be put on the schedule</summary>
    ScheduleIncomplete
}

/// <summary>
/// Exception thrown by the notification schedule service
/// </summary>
public class NotificationScheduleServiceException : Exception
{
    private NotificationScheduleServiceException(NotificationScheduleErrorKind kind, string message,
        Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>
    /// What went wrong
    /// </summary>
    public NotificationScheduleErrorKind Kind { get; }

    internal static Exception SubscriptionNotFound(string tenantId, OctoObjectId subscriptionRtId)
    {
        return new NotificationScheduleServiceException(NotificationScheduleErrorKind.SubscriptionNotFound,
            $"Notification subscription '{subscriptionRtId}' does not exist in tenant {tenantId}.");
    }

    internal static Exception EngineMissing(string tenantId)
    {
        return new NotificationScheduleServiceException(NotificationScheduleErrorKind.EngineUnavailable,
            $"The notification engine pipeline {NotificationEngine.PipelineRtId} does not exist in tenant {tenantId}. " +
            "It is seeded by the System.Communication.Notifications blueprint; restart the Communication Controller or clear the tenant cache to apply it.");
    }

    internal static Exception EngineDisabled(string tenantId)
    {
        return new NotificationScheduleServiceException(NotificationScheduleErrorKind.EngineUnavailable,
            $"The subscription is deployed, but the notification engine pipeline {NotificationEngine.PipelineRtId} is disabled in tenant {tenantId}, so no mail is sent. Enable it and deploy again.");
    }

    internal static Exception EngineDeployFailed(string tenantId, Exception inner)
    {
        return new NotificationScheduleServiceException(NotificationScheduleErrorKind.EngineUnavailable,
            $"The subscription is deployed, but the notification engine could not be deployed in tenant {tenantId}, so no mail is sent yet: {inner.Message}",
            inner);
    }

    internal static Exception ScheduleIncomplete(string tenantId, IReadOnlyList<OctoObjectId> preferenceRtIds,
        Exception firstError)
    {
        return new NotificationScheduleServiceException(NotificationScheduleErrorKind.ScheduleIncomplete,
            $"{preferenceRtIds.Count} notification preference(s) in tenant {tenantId} could not be scheduled ({string.Join(", ", preferenceRtIds)}): {firstError.Message}",
            firstError);
    }
}
