using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

/// <summary>
/// AB#5618: the outcome history of a deployable's status line. A success records when and resets
/// the streak; a failure counts up and never touches the last success.
/// </summary>
internal class DeployableStatusTrackingTests
{
    private static readonly DateTime ReportedAt = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task IsFailure_FlagSet_IsFailure()
    {
        await Assert.That(DeployableStatusTracking.IsFailure(true, "2026-10-06T08:00:00Z · seen 1")).IsTrue();
    }

    [Test]
    public async Task IsFailure_NoFlagButErrorPrefix_IsFailure()
    {
        await Assert.That(DeployableStatusTracking.IsFailure(false, "ERROR 2026-10-06T08:00:00Z · login failed"))
            .IsTrue();
    }

    [Test]
    public async Task IsFailure_NoFlagNoPrefix_IsSuccess()
    {
        await Assert.That(DeployableStatusTracking.IsFailure(false, "2026-10-06T08:00:00Z · ERROR count 0"))
            .IsFalse();
    }

    [Test]
    public async Task IsFailure_PrefixIsCaseSensitive()
    {
        // "Error" in free text of an info line is not the contract prefix.
        await Assert.That(DeployableStatusTracking.IsFailure(false, "Error-free poll")).IsFalse();
    }

    [Test]
    public async Task IsFailure_NullMessage_IsSuccess()
    {
        await Assert.That(DeployableStatusTracking.IsFailure(false, null)).IsFalse();
    }

    [Test]
    public async Task Apply_Success_SetsLastSuccessAndResetsStreak()
    {
        var current = new RtPipeline { ConsecutiveStatusFailures = 5 };
        var update = new RtPipeline();

        DeployableStatusTracking.Apply(update, current, isFailure: false, ReportedAt);

        await Assert.That(update.LastSuccessfulStatusAt).IsEqualTo(ReportedAt);
        await Assert.That(update.ConsecutiveStatusFailures).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_Failure_IncrementsStreakAndKeepsLastSuccess()
    {
        var current = new RtPipeline
        {
            ConsecutiveStatusFailures = 2,
            LastSuccessfulStatusAt = ReportedAt.AddHours(-1)
        };
        var update = new RtPipeline();

        DeployableStatusTracking.Apply(update, current, isFailure: true, ReportedAt);

        await Assert.That(update.ConsecutiveStatusFailures).IsEqualTo(3);
        // Not assigned on the partial update, so the persisted value stays.
        await Assert.That(update.LastSuccessfulStatusAt).IsNull();
    }

    [Test]
    public async Task Apply_FirstFailureEver_StartsAtOne()
    {
        var update = new RtPipeline();

        DeployableStatusTracking.Apply(update, new RtPipeline(), isFailure: true, ReportedAt);

        await Assert.That(update.ConsecutiveStatusFailures).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_FailureWithUnreadableCurrent_StartsAtOne()
    {
        var update = new RtPipeline();

        DeployableStatusTracking.Apply(update, null, isFailure: true, ReportedAt);

        await Assert.That(update.ConsecutiveStatusFailures).IsEqualTo(1);
    }
}
