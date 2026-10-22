using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

[Collection("CommunicationController")]
public class NotificationRepositoryTests(CommunicationControllerFixture fixture)
{
    private NotificationRepository CreateRepository() =>
        new(fixture.GetSystemContext(), NullLogger<NotificationRepository>.Instance);

    private async Task<OctoObjectId> CreateSubscriptionAsync(bool deployed) =>
        (await CreateSubscriptionWithQueryAsync(deployed)).RtId;

    private async Task<(OctoObjectId RtId, OctoObjectId QueryRtId)> CreateSubscriptionWithQueryAsync(bool deployed)
    {
        var rtId = OctoObjectId.GenerateNewId();
        var queryRtId = OctoObjectId.GenerateNewId();
        var templateRtId = OctoObjectId.GenerateNewId();
        var subscriptionId = new RtEntityId(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, rtId);
        await ApplyAsync([
            EntityUpdateInfo<RtEntity>.CreateInsert(SystemCkIds.RtCkSimpleRtQueryTypeId,
                new RtSimpleRtQuery
                {
                    RtId = queryRtId, Name = $"query-{queryRtId}", QueryCkTypeId = "System/Entity",
                    Columns = new AttributeStringValueList(["rtId"])
                }),
            EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationTemplateTypeId,
                new RtNotificationTemplate
                {
                    RtId = templateRtId, SubjectTemplate = "${count} new", BodyTemplate = "${rtId}",
                    Type = RtNotificationTypesEnum.EMail, RenderingType = RtRenderingTypesEnum.Html
                }),
            EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId,
                new RtNotificationSubscription { RtId = rtId, Name = $"subscription-{rtId}", Enabled = deployed })
        ], [
            AssociationUpdateInfo.CreateInsert(subscriptionId,
                new RtEntityId(SystemCkIds.RtCkSimpleRtQueryTypeId, queryRtId),
                SystemNotificationCkIds.RtCkSubscriptionQueryRoleId),
            AssociationUpdateInfo.CreateInsert(subscriptionId,
                new RtEntityId(SystemNotificationCkIds.RtCkNotificationTemplateTypeId, templateRtId),
                SystemNotificationCkIds.RtCkSubscriptionTemplateRoleId)
        ]);
        return (rtId, queryRtId);
    }

    private async Task<OctoObjectId> CreatePreferenceAsync(OctoObjectId subscriptionRtId, string? cron,
        string? timeZone = null)
    {
        var preference = new RtNotificationPreference
        {
            RtId = OctoObjectId.GenerateNewId(), SubjectId = "user-1", SkipExistingRows = true, TimeZone = timeZone
        };
        if (cron != null)
        {
            preference.CronExpression = cron;
        }

        await ApplyAsync(
            [EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference)],
            [
                AssociationUpdateInfo.CreateInsert(
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference.RtId),
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, subscriptionRtId),
                    SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId)
            ]);
        return preference.RtId;
    }

    private Task<OctoObjectId> CreateDeliveryStateAsync(OctoObjectId preferenceRtId, DateTime? lastRunAt) =>
        InsertDeliveryStateAsync(preferenceRtId,
            new RtNotificationDeliveryState { RtId = OctoObjectId.GenerateNewId(), LastRunAt = lastRunAt });

    private Task<OctoObjectId> CreateRanStateAsync(OctoObjectId preferenceRtId, OctoObjectId queryRtId,
        string subjectId = "user-1") =>
        InsertDeliveryStateAsync(preferenceRtId, new RtNotificationDeliveryState
        {
            RtId = OctoObjectId.GenerateNewId(), LastRunAt = DateTime.UtcNow, ProcessedUntil = DateTime.UtcNow,
            SubjectId = subjectId, QueryRtId = queryRtId.ToString(), SkipExistingRows = true
        });

    private async Task<OctoObjectId> InsertDeliveryStateAsync(OctoObjectId preferenceRtId, RtNotificationDeliveryState state)
    {
        await ApplyAsync(
            [EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state)],
            [
                AssociationUpdateInfo.CreateInsert(
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state.RtId),
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preferenceRtId),
                    SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId)
            ]);
        return state.RtId;
    }

    private async Task ApplyAsync(List<EntityUpdateInfo<RtEntity>> entities, List<AssociationUpdateInfo> associations)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entities, associations, result);
        result.HasErrors.Should().BeFalse(result.GetMessages());
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task GetDeployedPreferenceSchedulesAsync_ReturnsOnlyPreferencesOfDeployedSubscriptionsWithACron()
    {
        var deployed = await CreateSubscriptionAsync(true);
        var undeployed = await CreateSubscriptionAsync(false);
        var scheduled = await CreatePreferenceAsync(deployed, "0 7 * * *", "Europe/Vienna");
        var withoutCron = await CreatePreferenceAsync(deployed, "");
        var paused = await CreatePreferenceAsync(undeployed, "0 7 * * *");

        var schedules = await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId);

        var ids = schedules.Select(schedule => schedule.PreferenceRtId).ToList();
        ids.Should().Contain(scheduled);
        ids.Should().NotContain(withoutCron);
        ids.Should().NotContain(paused);
        schedules.Single(schedule => schedule.PreferenceRtId == scheduled).Should()
            .Be(new NotificationPreferenceSchedule(scheduled, "0 7 * * *", "Europe/Vienna", true, true));
    }

    [Fact]
    public async Task GetDeployedPreferenceSchedulesAsync_APreferenceThatRanUnderItsCurrentSettings_NeedsNoFirstRun()
    {
        var (subscription, query) = await CreateSubscriptionWithQueryAsync(true);
        var ran = await CreatePreferenceAsync(subscription, "0 7 * * *");
        var stateWithoutRun = await CreatePreferenceAsync(subscription, "0 8 * * *");
        await CreateRanStateAsync(ran, query);
        await CreateDeliveryStateAsync(stateWithoutRun, null);

        var schedules = await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId, subscription);

        schedules.Should().HaveCount(2);
        schedules.Single(schedule => schedule.PreferenceRtId == ran).NeedsFirstRun.Should().BeFalse();
        schedules.Single(schedule => schedule.PreferenceRtId == stateWithoutRun).NeedsFirstRun.Should().BeTrue();
    }

    [Fact]
    public async Task GetDeployedPreferenceSchedulesAsync_ARecipientOrQueryChange_NeedsAFirstRun()
    {
        var (subscription, query) = await CreateSubscriptionWithQueryAsync(true);
        var otherRecipient = await CreatePreferenceAsync(subscription, "0 7 * * *");
        var otherQuery = await CreatePreferenceAsync(subscription, "0 7 * * *");
        await CreateRanStateAsync(otherRecipient, query, "user-2");
        await CreateRanStateAsync(otherQuery, OctoObjectId.GenerateNewId());

        var schedules = await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId, subscription);

        schedules.Should().OnlyContain(schedule => schedule.NeedsFirstRun);
        (await CreateRepository().GetDeployedPreferenceScheduleAsync(fixture.TestTenantId, otherRecipient))!
            .NeedsFirstRun.Should().BeTrue();
    }

    [Fact]
    public async Task GetDeployedPreferenceSchedulesAsync_ARunBeforeTheLatestDeploy_NeedsAFirstRun()
    {
        var (subscription, query) = await CreateSubscriptionWithQueryAsync(false);
        var preference = await CreatePreferenceAsync(subscription, "0 7 * * *");
        await CreateRanStateAsync(preference, query);
        await CreateRepository().SetSubscriptionDeployedAsync(fixture.TestTenantId, subscription,
            DateTime.UtcNow.AddMinutes(1));

        var schedule = await CreateRepository().GetDeployedPreferenceScheduleAsync(fixture.TestTenantId, preference);

        schedule!.NeedsFirstRun.Should().BeTrue();
    }

    [Fact]
    public async Task GetDeployedPreferenceSchedulesAsync_FilteredBySubscription_LeavesOthersOut()
    {
        var mine = await CreateSubscriptionAsync(true);
        var other = await CreateSubscriptionAsync(true);
        var myPreference = await CreatePreferenceAsync(mine, "0 7 * * *");
        await CreatePreferenceAsync(other, "0 7 * * *");

        var schedules = await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId, mine);

        schedules.Select(schedule => schedule.PreferenceRtId).Should().Equal(myPreference);
    }

    [Fact]
    public async Task GetPreferenceRtIdsAsync_ReturnsThePreferencesOfAnUndeployedSubscriptionToo()
    {
        var subscription = await CreateSubscriptionAsync(false);
        var preference = await CreatePreferenceAsync(subscription, "0 7 * * *");

        var rtIds = await CreateRepository().GetPreferenceRtIdsAsync(fixture.TestTenantId, subscription);

        rtIds.Should().Equal(preference);
    }

    [Fact]
    public async Task GetSubscriptionStateAsync_TellsDeployedUndeployedAndUnknownApart_AndNamesTheSubscription()
    {
        var deployed = await CreateSubscriptionAsync(true);
        var undeployed = await CreateSubscriptionAsync(false);
        var repository = CreateRepository();

        (await repository.GetSubscriptionStateAsync(fixture.TestTenantId, deployed)).Should()
            .Be(new NotificationSubscriptionState(true, $"subscription-{deployed}"));
        (await repository.GetSubscriptionStateAsync(fixture.TestTenantId, undeployed)).Should()
            .Be(new NotificationSubscriptionState(false, $"subscription-{undeployed}"));
        (await repository.GetSubscriptionStateAsync(fixture.TestTenantId, OctoObjectId.GenerateNewId())).Should().BeNull();
    }

    [Fact]
    public async Task SetSubscriptionDeployedAsync_WritesTheFlagAndTheMoment()
    {
        var subscription = await CreateSubscriptionAsync(false);
        await CreatePreferenceAsync(subscription, "0 7 * * *");
        var deployedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        await CreateRepository().SetSubscriptionDeployedAsync(fixture.TestTenantId, subscription, deployedAt);

        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        var stored = await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationSubscription>(session, subscription);
        stored!.Enabled.Should().BeTrue();
        stored.DeployedAt.Should().Be(deployedAt);
        (await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId, subscription))
            .Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteOrphanedDeliveryStatesAsync_ErasesTheStateOfARemovedRecipient_AndKeepsTheOthers()
    {
        var subscription = await CreateSubscriptionAsync(true);
        var kept = await CreatePreferenceAsync(subscription, "0 7 * * *");
        var keptState = await CreateDeliveryStateAsync(kept, null);
        var removed = await CreatePreferenceAsync(subscription, "0 7 * * *");
        var orphanedState = await CreateDeliveryStateAsync(removed, null);
        await DeleteAsync(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, removed);

        var erased = await CreateRepository().DeleteOrphanedDeliveryStatesAsync(fixture.TestTenantId);

        erased.Should().BeGreaterThanOrEqualTo(1);
        (await StateExistsAsync(orphanedState)).Should().BeFalse();
        (await StateExistsAsync(keptState)).Should().BeTrue();
        (await CreateRepository().DeleteOrphanedDeliveryStatesAsync(fixture.TestTenantId)).Should().Be(0);
    }

    private async Task DeleteAsync(RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ckTypeId, rtId))], DeleteOptions.Erase, result);
        result.HasErrors.Should().BeFalse(result.GetMessages());
        await session.CommitTransactionAsync();
    }

    private async Task<bool> StateExistsAsync(OctoObjectId stateRtId)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        return await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationDeliveryState>(session, stateRtId) != null;
    }
}
