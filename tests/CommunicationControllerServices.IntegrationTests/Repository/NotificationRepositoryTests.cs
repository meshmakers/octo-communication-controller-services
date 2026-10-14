using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
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

    private async Task<OctoObjectId> CreateSubscriptionAsync(bool deployed)
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
        return rtId;
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

    private async Task CreateDeliveryStateAsync(OctoObjectId preferenceRtId, DateTime? lastRunAt)
    {
        var state = new RtNotificationDeliveryState { RtId = OctoObjectId.GenerateNewId(), LastRunAt = lastRunAt };
        await ApplyAsync(
            [EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state)],
            [
                AssociationUpdateInfo.CreateInsert(
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId, state.RtId),
                    new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preferenceRtId),
                    SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId)
            ]);
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
    public async Task GetDeployedPreferenceSchedulesAsync_APreferenceThatRanIsNoLongerNew()
    {
        var subscription = await CreateSubscriptionAsync(true);
        var ran = await CreatePreferenceAsync(subscription, "0 7 * * *");
        var stateWithoutRun = await CreatePreferenceAsync(subscription, "0 8 * * *");
        await CreateDeliveryStateAsync(ran, DateTime.UtcNow);
        await CreateDeliveryStateAsync(stateWithoutRun, null);

        var schedules = await CreateRepository().GetDeployedPreferenceSchedulesAsync(fixture.TestTenantId, subscription);

        schedules.Should().HaveCount(2);
        schedules.Single(schedule => schedule.PreferenceRtId == ran).HasNeverRun.Should().BeFalse();
        schedules.Single(schedule => schedule.PreferenceRtId == stateWithoutRun).HasNeverRun.Should().BeTrue();
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
    public async Task SubscriptionExistsAsync_TellsAnExistingSubscriptionFromAnUnknownId()
    {
        var subscription = await CreateSubscriptionAsync(false);
        var repository = CreateRepository();

        (await repository.SubscriptionExistsAsync(fixture.TestTenantId, subscription)).Should().BeTrue();
        (await repository.SubscriptionExistsAsync(fixture.TestTenantId, OctoObjectId.GenerateNewId())).Should().BeFalse();
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
}
