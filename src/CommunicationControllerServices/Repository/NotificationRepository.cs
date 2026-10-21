using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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

        List<RtNotificationSubscription> subscriptions;
        if (subscriptionRtId == null)
        {
            var deployed = await tenantRepository.GetRtEntitiesByTypeAsync<RtNotificationSubscription>(session,
                RtEntityQueryOptions.Create().FieldEquals(nameof(RtNotificationSubscription.Enabled), true));
            subscriptions = deployed.Items.ToList();
        }
        else
        {
            var subscription = await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationSubscription>(session,
                subscriptionRtId.Value);
            subscriptions = subscription is { Enabled: true } ? [subscription] : [];
        }

        if (subscriptions.Count == 0)
        {
            return [];
        }

        var preferences = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationSubscription, RtNotificationPreference>(
            session, subscriptions.Select(subscription => subscription.RtId).ToList(),
            SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId, GraphDirections.Inbound, null,
            RtEntityQueryOptions.Create());
        var scheduled = subscriptions
            .SelectMany(subscription => TargetsOf(preferences, SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId,
                    subscription.RtId)
                .Where(preference => HasSchedule(tenantId, preference))
                .Select(preference => (preference, subscription)))
            .ToList();
        return await ToSchedulesAsync(tenantRepository, session, scheduled);
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
        if (subscriptions.Values.SelectMany(resultSet => resultSet.Items).FirstOrDefault() is not { Enabled: true } subscription ||
            !HasSchedule(tenantId, preference))
        {
            return null;
        }

        return (await ToSchedulesAsync(tenantRepository, session, [(preference, subscription)]))[0];
    }

    public async Task<IReadOnlyList<OctoObjectId>> GetPreferenceRtIdsAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        return (await GetPreferencesAsync(tenantRepository, session, [subscriptionRtId]))
            .Select(preference => preference.RtId)
            .ToList();
    }

    public async Task<NotificationSubscriptionState?> GetSubscriptionStateAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();

        var subscription = await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationSubscription>(session, subscriptionRtId);
        return subscription == null ? null : new NotificationSubscriptionState(subscription.Enabled == true, subscription.Name);
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
        ITenantRepository tenantRepository, IOctoSession session,
        IReadOnlyList<(RtNotificationPreference Preference, RtNotificationSubscription Subscription)> preferences)
    {
        if (preferences.Count == 0)
        {
            return [];
        }

        var states = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationPreference, RtNotificationDeliveryState>(
            session, preferences.Select(item => item.Preference.RtId).ToList(),
            SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Inbound, null,
            RtEntityQueryOptions.Create());
        var queries = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationSubscription, RtPersistentQuery>(
            session, preferences.Select(item => item.Subscription.RtId).Distinct().ToList(),
            SystemNotificationCkIds.RtCkSubscriptionQueryRoleId, GraphDirections.Outbound, null,
            RtEntityQueryOptions.Create());

        return preferences
            .Select(item => new NotificationPreferenceSchedule(item.Preference.RtId, item.Preference.CronExpression,
                string.IsNullOrWhiteSpace(item.Preference.TimeZone) ? null : item.Preference.TimeZone,
                NotificationFirstRun.IsDue(
                    TargetsOf(states, SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, item.Preference.RtId)
                        .FirstOrDefault(),
                    item.Preference, item.Subscription.DeployedAt,
                    TargetsOf(queries, SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, item.Subscription.RtId)
                        .FirstOrDefault()?.RtId),
                item.Preference.SkipExistingRows != false))
            .ToList();
    }

    private static IEnumerable<TEntity> TargetsOf<TEntity>(IMultipleOriginResultSet<TEntity> resultSets,
        RtCkId<CkTypeId> originCkTypeId, OctoObjectId originRtId) where TEntity : RtEntity =>
        resultSets.TryGetValue(new RtEntityId(originCkTypeId, originRtId), out var resultSet) ? resultSet.Items : [];

    private static async Task<IReadOnlyList<RtNotificationPreference>> GetPreferencesAsync(ITenantRepository tenantRepository,
        IOctoSession session, List<OctoObjectId> subscriptionRtIds)
    {
        var preferences = await tenantRepository.GetRtAssociationTargetsAsync<RtNotificationSubscription, RtNotificationPreference>(
            session, subscriptionRtIds, SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId,
            GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

        return preferences.Values.SelectMany(resultSet => resultSet.Items).ToList();
    }
}
