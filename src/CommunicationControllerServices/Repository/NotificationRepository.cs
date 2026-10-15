using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

internal class NotificationRepository(ISystemContext systemContext, ILogger<NotificationRepository> logger)
    : INotificationRepository
{
    public async Task<IReadOnlyList<NotificationPreferenceSchedule>> GetDeployedPreferenceSchedulesAsync(string tenantId,
        OctoObjectId? subscriptionRtId = null)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        List<OctoObjectId> subscriptionRtIds;
        if (subscriptionRtId == null)
        {
            var deployed = await tenantRepository.GetRtEntitiesByTypeAsync<RtNotificationSubscription>(session,
                RtEntityQueryOptions.Create().FieldEquals(nameof(RtNotificationSubscription.Enabled), true));
            subscriptionRtIds = deployed.Items.Select(subscription => subscription.RtId).ToList();
        }
        else
        {
            var subscription = await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationSubscription>(session,
                subscriptionRtId.Value);
            subscriptionRtIds = subscription is { Enabled: true } ? [subscription.RtId] : [];
        }

        if (subscriptionRtIds.Count == 0)
        {
            return [];
        }

        var preferences = (await GetPreferencesAsync(tenantRepository, session, subscriptionRtIds))
            .Where(preference => HasSchedule(tenantId, preference))
            .ToList();
        return await ToSchedulesAsync(tenantRepository, session, preferences);
    }

    public async Task<NotificationPreferenceSchedule?> GetDeployedPreferenceScheduleAsync(string tenantId,
        OctoObjectId preferenceRtId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        var preference = await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationPreference>(session, preferenceRtId);
        if (preference == null)
        {
            return null;
        }

        var subscriptions = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationPreference, RtNotificationSubscription>(
            session, [preferenceRtId], SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId,
            GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
        if (subscriptions.Values.SelectMany(resultSet => resultSet.Items).FirstOrDefault() is not { Enabled: true } ||
            !HasSchedule(tenantId, preference))
        {
            return null;
        }

        return (await ToSchedulesAsync(tenantRepository, session, [preference]))[0];
    }

    public async Task<IReadOnlyList<OctoObjectId>> GetPreferenceRtIdsAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        return (await GetPreferencesAsync(tenantRepository, session, [subscriptionRtId]))
            .Select(preference => preference.RtId)
            .ToList();
    }

    public async Task<bool> SubscriptionExistsAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        return await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationSubscription>(session, subscriptionRtId) != null;
    }

    public async Task SetSubscriptionDeployedAsync(string tenantId, OctoObjectId subscriptionRtId, DateTime? deployedAt)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var subscription = new RtNotificationSubscription { Enabled = deployedAt != null };
        if (deployedAt != null)
        {
            subscription.DeployedAt = deployedAt;
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
        [
            EntityUpdateInfo<RtNotificationSubscription>.CreateUpdate(
                new RtEntityId(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, subscriptionRtId),
                subscription)
        ], operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
        }

        await session.CommitTransactionAsync();
    }

    public async Task<int> DeleteOrphanedDeliveryStatesAsync(string tenantId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var states = await tenantRepository.GetRtEntitiesByTypeAsync<RtNotificationDeliveryState>(session,
            RtEntityQueryOptions.Create());
        var stateRtIds = states.Items.Select(state => state.RtId).ToList();
        if (stateRtIds.Count == 0)
        {
            await session.CommitTransactionAsync();
            return 0;
        }

        var owners = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationDeliveryState, RtNotificationPreference>(
            session, stateRtIds, SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Outbound,
            null, RtEntityQueryOptions.Create());
        var orphans = stateRtIds
            .Select(rtId => new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, rtId))
            .Where(stateId => !owners.TryGetValue(stateId, out var preferences) || !preferences.Items.Any())
            .ToList();
        if (orphans.Count == 0)
        {
            await session.CommitTransactionAsync();
            return 0;
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            orphans.Select(EntityUpdateInfo<RtNotificationDeliveryState>.CreateDelete).ToList(), DeleteOptions.Erase,
            operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
        }

        await session.CommitTransactionAsync();
        return orphans.Count;
    }

    private bool HasSchedule(string tenantId, RtNotificationPreference preference)
    {
        if (string.IsNullOrWhiteSpace(preference.CronExpression))
        {
            logger.LogWarning("[{TenantId}] Notification preference '{PreferenceRtId}' has no cron expression and is not scheduled",
                tenantId, preference.RtId);
            return false;
        }

        if (!NotificationCron.FiresAtMostOncePerMinute(preference.CronExpression))
        {
            logger.LogWarning("[{TenantId}] Notification preference '{PreferenceRtId}' fires more than once a minute ('{CronExpression}') and is not scheduled",
                tenantId, preference.RtId, preference.CronExpression);
            return false;
        }

        return true;
    }

    private static async Task<IReadOnlyList<NotificationPreferenceSchedule>> ToSchedulesAsync(
        ITenantRepository tenantRepository, IOctoSession session, IReadOnlyList<RtNotificationPreference> preferences)
    {
        if (preferences.Count == 0)
        {
            return [];
        }

        var states = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationPreference, RtNotificationDeliveryState>(
            session, preferences.Select(preference => preference.RtId).ToList(),
            SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Inbound, null,
            RtEntityQueryOptions.Create());

        return preferences
            .Select(preference => new NotificationPreferenceSchedule(preference.RtId, preference.CronExpression,
                string.IsNullOrWhiteSpace(preference.TimeZone) ? null : preference.TimeZone,
                !states.TryGetValue(new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference.RtId),
                    out var state) || state.Items.All(item => item.LastRunAt == null),
                preference.SkipExistingRows != false))
            .ToList();
    }

    private static async Task<IReadOnlyList<RtNotificationPreference>> GetPreferencesAsync(ITenantRepository tenantRepository,
        IOctoSession session, List<OctoObjectId> subscriptionRtIds)
    {
        var preferences = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationSubscription, RtNotificationPreference>(
            session, subscriptionRtIds, SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId,
            GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

        return preferences.Values.SelectMany(resultSet => resultSet.Items).ToList();
    }
}
