using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

internal class NotificationFirstRunTests
{
    private static readonly DateTime Created = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeployedAt = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastRunAt = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
    private static readonly OctoObjectId QueryRtId = OctoObjectId.GenerateNewId();

    private static RtNotificationPreference Preference(bool skipExistingRows = true, string subjectId = "user-1") =>
        new() { RtId = OctoObjectId.GenerateNewId(), SubjectId = subjectId, SkipExistingRows = skipExistingRows, RtCreationDateTime = Created };

    private static RtNotificationDeliveryState State(bool skipExistingRows = true) =>
        new()
        {
            RtId = OctoObjectId.GenerateNewId(), LastRunAt = LastRunAt, ProcessedUntil = LastRunAt,
            SubjectId = "user-1", QueryRtId = QueryRtId.ToString(), SkipExistingRows = skipExistingRows
        };

    [Test]
    public async Task IsDue_NoStateOrNoRun_IsAFirstRun()
    {
        await Assert.That(NotificationFirstRun.IsDue(null, Preference(), DeployedAt, QueryRtId)).IsTrue();

        var state = State();
        state.LastRunAt = null;
        await Assert.That(NotificationFirstRun.IsDue(state, Preference(), DeployedAt, QueryRtId)).IsTrue();
    }

    [Test]
    public async Task IsDue_SameRecipientQueryAndStartOption_IsNoFirstRun()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(), Preference(), DeployedAt, QueryRtId)).IsFalse();
        await Assert.That(NotificationFirstRun.IsDue(State(false), Preference(false), DeployedAt, QueryRtId)).IsFalse();
    }

    [Test]
    public async Task IsDue_RecipientChanged_StartsAgain()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(), Preference(subjectId: "user-2"), DeployedAt, QueryRtId))
            .IsTrue();
    }

    [Test]
    public async Task IsDue_QueryChanged_StartsAgain()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(), Preference(), DeployedAt, OctoObjectId.GenerateNewId()))
            .IsTrue();
    }

    [Test]
    public async Task IsDue_StartOptionChanged_StartsAgain()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(false), Preference(), DeployedAt, QueryRtId)).IsTrue();
        await Assert.That(NotificationFirstRun.IsDue(State(), Preference(false), DeployedAt, QueryRtId)).IsTrue();
    }

    [Test]
    public async Task IsDue_RanBeforeTheLatestDeploy_StartsOnwardsAgain()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(), Preference(), LastRunAt.AddMinutes(1), QueryRtId))
            .IsTrue();
    }

    [Test]
    public async Task IsDue_RanBeforeTheLatestDeploy_LetsCatchUpReadOn()
    {
        await Assert.That(NotificationFirstRun.IsDue(State(false), Preference(false), LastRunAt.AddMinutes(1), QueryRtId))
            .IsFalse();
    }

    [Test]
    public async Task IsDue_OnwardsWithoutARecordedBaseline_StartsAgain()
    {
        var state = State();
        state.ProcessedUntil = null;

        await Assert.That(NotificationFirstRun.IsDue(state, Preference(), DeployedAt, QueryRtId)).IsTrue();
    }
}
