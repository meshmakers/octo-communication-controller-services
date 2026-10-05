using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#5492. The arithmetic behind <c>octo.pipeline.cron.missed_executions</c>, pinned against a
///     fixed clock so every expected number is exact. The gauge tests in
///     <see cref="PipelineExecutionMetricsTests" /> run against the wall clock and can only pin what
///     stays stable there; the boundary cases — grace, exclusive start, anchors, the cap — live here.
/// </summary>
internal class PipelineCronScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>A Tuesday, 14:37:12Z — deliberately not on a minute or hour boundary.</summary>
    private static readonly DateTime Now = new(2026, 10, 6, 14, 37, 12, DateTimeKind.Utc);

    private static CronTrigger Hourly(DateTime? anchor = null) => Trigger("0 * * * *", anchor);

    private static CronTrigger Trigger(string expression, DateTime? anchor = null)
    {
        var parsed = PipelineCronSchedule.TryParse(expression, out var cron);
        if (!parsed)
        {
            throw new ArgumentException($"'{expression}' does not parse", nameof(expression));
        }

        return new CronTrigger(expression, cron, anchor);
    }

    [Test]
    [Arguments("0 * * * *")]
    [Arguments("*/5 * * * *")]
    [Arguments("0 0 * * MON-FRI")]
    [Arguments("@hourly")]
    public async Task TryParse_AcceptsTheFiveFieldFormatHangfireSchedules(string expression)
    {
        await Assert.That(PipelineCronSchedule.TryParse(expression, out _)).IsTrue();
    }

    /// <summary>Six fields mean seconds — the rule Hangfire applies, so what is counted is what fires.</summary>
    [Test]
    public async Task TryParse_TreatsSixFieldsAsIncludingSeconds()
    {
        await Assert.That(PipelineCronSchedule.TryParse("30 0 * * * *", out var cron)).IsTrue();

        var next = cron.GetNextOccurrence(Now, Utc);
        await Assert.That(next).IsEqualTo(new DateTime(2026, 10, 6, 15, 0, 30, DateTimeKind.Utc));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("every hour")]
    [Arguments("0 * * *")]
    [Arguments("99 * * * *")]
    public async Task TryParse_RejectsWhatTheSchedulerWouldHaveRejected(string? expression)
    {
        await Assert.That(PipelineCronSchedule.TryParse(expression, out _)).IsFalse();
    }

    /// <summary>The healthy case: the last execution started right after the last fire time.</summary>
    [Test]
    public async Task OnSchedule_ReportsZero()
    {
        var lastExecutionAt = new DateTime(2026, 10, 6, 14, 0, 3, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt, Now, Utc);

        await Assert.That(missed).IsEqualTo(0);
    }

    /// <summary>
    ///     The prod-1 finAPI case in miniature: hourly cron, last recorded execution three fire
    ///     times ago. 12:00, 13:00 and 14:00 passed without one.
    /// </summary>
    [Test]
    public async Task StalledForThreeFireTimes_ReportsThree()
    {
        var lastExecutionAt = new DateTime(2026, 10, 6, 11, 0, 2, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt, Now, Utc);

        await Assert.That(missed).IsEqualTo(3);
    }

    /// <summary>
    ///     The fire time that produced the last execution is not a miss, even when the execution's
    ///     recorded start coincides with it to the second.
    /// </summary>
    [Test]
    public async Task FireTimeEqualToTheLastExecution_IsNotCounted()
    {
        var lastExecutionAt = new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt, Now, Utc);

        await Assert.That(missed).IsEqualTo(0);
    }

    /// <summary>
    ///     A fire time younger than the grace is still in flight — trigger message, adapter start,
    ///     buffered start report — and must not look like a miss for a minute on every scrape.
    /// </summary>
    [Test]
    public async Task FireTimeWithinTheGrace_IsNotCountedYet()
    {
        var now = new DateTime(2026, 10, 6, 14, 0, 40, DateTimeKind.Utc);
        var lastExecutionAt = new DateTime(2026, 10, 6, 13, 0, 1, DateTimeKind.Utc);

        var withinGrace = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt, now, Utc);
        var afterGrace = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt,
            now.Add(PipelineCronSchedule.Grace), Utc);

        await Assert.That(withinGrace).IsEqualTo(0);
        await Assert.That(afterGrace).IsEqualTo(1);
    }

    /// <summary>Two triggers are two expected executions per fire time: a sum, not a union.</summary>
    [Test]
    public async Task SeveralTriggers_AreSummed()
    {
        var lastExecutionAt = new DateTime(2026, 10, 6, 12, 30, 0, DateTimeKind.Utc);

        // Hourly fires 13:00, 14:00; the half-hourly one fires 13:00, 13:30, 14:00, 14:30.
        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly(), Trigger("0,30 * * * *")],
            lastExecutionAt, Now, Utc);

        await Assert.That(missed).IsEqualTo(6);
    }

    /// <summary>
    ///     A pipeline that never executed has no <c>LastExecutionAt</c>; counting starts at the
    ///     trigger's own anchor instead, so a schedule that never produced a single run is a stall too.
    /// </summary>
    [Test]
    public async Task NeverExecuted_CountsFromTheTriggerAnchor()
    {
        var anchor = new DateTime(2026, 10, 6, 12, 15, 0, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly(anchor)], null, Now, Utc);

        await Assert.That(missed).IsEqualTo(2);
    }

    /// <summary>A recorded execution wins over the trigger anchor — the anchor is only the fallback.</summary>
    [Test]
    public async Task LastExecution_TakesPrecedenceOverTheTriggerAnchor()
    {
        var anchor = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
        var lastExecutionAt = new DateTime(2026, 10, 6, 13, 0, 1, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly(anchor)], lastExecutionAt, Now, Utc);

        await Assert.That(missed).IsEqualTo(1);
    }

    /// <summary>
    ///     No execution and no anchor means nothing can be said. That is <c>null</c> — "do not
    ///     publish" — never a zero that would read as "on schedule".
    /// </summary>
    [Test]
    public async Task NeverExecutedWithoutAnyAnchor_ReportsNothing()
    {
        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly()], null, Now, Utc);

        await Assert.That(missed).IsNull();
    }

    /// <summary>One anchored trigger is enough for a series; the unanchored one contributes nothing.</summary>
    [Test]
    public async Task NeverExecuted_OnlyAnchoredTriggersCount()
    {
        var anchor = new DateTime(2026, 10, 6, 12, 15, 0, DateTimeKind.Utc);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly(), Hourly(anchor)], null, Now, Utc);

        await Assert.That(missed).IsEqualTo(2);
    }

    [Test]
    public async Task NoTriggers_ReportsNothing()
    {
        var missed = PipelineCronSchedule.CountMissedExecutions([], Now.AddHours(-5), Now, Utc);

        await Assert.That(missed).IsNull();
    }

    /// <summary>
    ///     An every-minute pipeline that stalled for a month would otherwise cost 43 200 cron steps
    ///     per scrape, for a number no rule reads differently from 10 000.
    /// </summary>
    [Test]
    public async Task LongOutage_IsCappedAtTenThousand()
    {
        var missed = PipelineCronSchedule.CountMissedExecutions([Trigger("* * * * *")], Now.AddDays(-30), Now, Utc);

        await Assert.That(missed).IsEqualTo(PipelineCronSchedule.MaxMissedExecutions);
    }

    /// <summary>Entity timestamps may come back with an unspecified kind; they denote UTC instants.</summary>
    [Test]
    public async Task UnspecifiedKindTimestamps_AreReadAsUtc()
    {
        var lastExecutionAt = new DateTime(2026, 10, 6, 11, 0, 2, DateTimeKind.Unspecified);
        var now = new DateTime(2026, 10, 6, 14, 37, 12, DateTimeKind.Unspecified);

        var missed = PipelineCronSchedule.CountMissedExecutions([Hourly()], lastExecutionAt, now, Utc);

        await Assert.That(missed).IsEqualTo(3);
    }

    [Test]
    public async Task TypicalInterval_IsTheDistanceBetweenTheNextTwoFireTimes()
    {
        var interval = PipelineCronSchedule.TypicalIntervalSeconds([Hourly()], Now, Utc);

        await Assert.That(interval).IsEqualTo(3600);
    }

    [Test]
    public async Task TypicalInterval_PicksTheShortestOfSeveralTriggers()
    {
        var interval = PipelineCronSchedule.TypicalIntervalSeconds([Hourly(), Trigger("*/15 * * * *")], Now, Utc);

        await Assert.That(interval).IsEqualTo(900);
    }

    [Test]
    public async Task TypicalInterval_WithoutTriggers_ReportsNothing()
    {
        await Assert.That(PipelineCronSchedule.TypicalIntervalSeconds([], Now, Utc)).IsNull();
    }
}
