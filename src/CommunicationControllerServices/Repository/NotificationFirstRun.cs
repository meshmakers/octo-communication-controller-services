using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

internal static class NotificationFirstRun
{
    public static bool IsDue(RtNotificationDeliveryState? state, RtNotificationPreference preference,
        DateTime? deployedAt, OctoObjectId? queryRtId)
    {
        if (state?.LastRunAt is not { } lastRunAt)
        {
            return true;
        }

        var skipExistingRows = preference.SkipExistingRows != false;
        var created = preference.RtCreationDateTime ?? DateTime.MinValue;
        var deployed = deployedAt ?? DateTime.MinValue;
        return skipExistingRows && lastRunAt < (created > deployed ? created : deployed) ||
               state.SubjectId != null && state.SubjectId != preference.SubjectId ||
               state.QueryRtId != null && state.QueryRtId != queryRtId?.ToString() ||
               state.SkipExistingRows != null && state.SkipExistingRows != skipExistingRows ||
               skipExistingRows && state.ProcessedUntil == null;
    }
}
