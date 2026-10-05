using Cronos;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     One enabled cron <c>PipelineTrigger</c> of a pipeline, as the missed-execution gauge needs it
///     (AB#5492).
/// </summary>
/// <param name="Expression">The cron expression as authored on the trigger — becomes the <c>octo.cron.expression</c> tag.</param>
/// <param name="Parsed">The expression parsed the way the scheduler parses it (see <see cref="PipelineCronSchedule.TryParse" />).</param>
/// <param name="AnchorUtc">
///     Where counting starts when the pipeline has no recorded execution at all: the trigger's last
///     modification, falling back to its creation. <c>null</c> when the entity carries neither, in
///     which case the trigger contributes nothing.
/// </param>
internal sealed record CronTrigger(string Expression, CronExpression Parsed, DateTime? AnchorUtc);

/// <summary>
///     Pure cron arithmetic for <c>octo.pipeline.cron.missed_executions</c> (AB#5492).
///
///     <b>Why the age gauge was not enough.</b> On prod-1 the finAPI adapter failed on every hourly
///     cron execution from 1.10. to 5.10.2026 — the exception was thrown before the adapter reported
///     the execution start, so <c>PipelineStatistics.LastExecutionAt</c> froze at 30.9. 21:00Z while
///     every health signal (workload Deployed/Online, service account Healthy, pod Running) stayed
///     green. <c>octo.pipeline.execution.age</c> did rise, but it cannot be alerted on: an idle
///     <c>FromHttpRequest</c> pipeline legitimately has an age of months, and a threshold low enough
///     for an hourly pipeline pages for every daily one. What the controller does know — and nothing
///     else does — is the schedule: the <c>RtPipelineTrigger</c> cron expressions it hands to the
///     scheduler. Counting the fire times that passed without an execution turns "how old is the last
///     run" into "how many runs are missing", which is <c>0</c> for every healthy pipeline regardless
///     of its cadence and a rule can be written against once.
///
///     <b>Parsed like the scheduler parses it.</b> Triggers are scheduled through MassTransit.Hangfire
///     (<c>TriggerManagementService.UpdateScheduleAsync</c>), and Hangfire evaluates the expression
///     with Cronos: six whitespace-separated fields mean seconds are included, five mean the standard
///     format. This class applies the same rule, so what it counts is what Hangfire would have fired.
///     The time zone is <see cref="TimeZoneInfo.Local" /> for the same reason — <c>OctoRecurringSchedule</c>
///     stamps every schedule with the controller's local zone id.
///
///     <b>Counting rule.</b> Fire times strictly after the anchor and no later than <c>now − grace</c>,
///     summed over the pipeline's enabled cron triggers (a sum, not a union: two triggers on the same
///     pipeline are two expected executions, and the simpler arithmetic is the one that is also easy to
///     explain in an alert). The anchor is the pipeline's last recorded execution start; a pipeline that
///     has never executed falls back to each trigger's own anchor (see <see cref="CronTrigger.AnchorUtc" />).
///     The grace (<see cref="Grace" />) keeps a fire time whose execution is still being reported from
///     counting as missed. The result is capped at <see cref="MaxMissedExecutions" /> so an every-minute
///     pipeline that stalled for a month costs a bounded number of cron steps per scrape.
/// </summary>
internal static class PipelineCronSchedule
{
    /// <summary>
    ///     Upper bound of the gauge. Above it the exact number carries no information an alert rule
    ///     would act on differently, while the enumeration cost would keep growing with the outage.
    /// </summary>
    public const int MaxMissedExecutions = 10000;

    /// <summary>
    ///     A fire time younger than this is not counted: the trigger message is in flight, the adapter
    ///     is starting the execution, or the start report is still buffered — none of which is a miss.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Parses a <c>PipelineTrigger</c> expression the way Hangfire does: six fields include
    ///     seconds, five are standard. An empty or malformed expression yields <c>false</c>; such a
    ///     trigger would have failed to schedule anyway and must not take the whole pipeline's series
    ///     down with it.
    /// </summary>
    public static bool TryParse(string? expression, out CronExpression parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var fieldCount = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fieldCount == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try
        {
            parsed = CronExpression.Parse(expression, format);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Number of fire times that passed without an execution, or <c>null</c> when nothing can be
    ///     said — no trigger, or no execution and no trigger with an anchor. <c>null</c> means "do not
    ///     publish", never "zero": a series that exists has to mean the pipeline was actually checked.
    /// </summary>
    public static long? CountMissedExecutions(IReadOnlyList<CronTrigger> triggers, DateTime? lastExecutionAt,
        DateTime nowUtc, TimeZoneInfo zone)
    {
        if (triggers.Count == 0)
        {
            return null;
        }

        var toUtc = AsUtc(nowUtc) - Grace;
        long total = 0;
        var anchored = false;

        foreach (var trigger in triggers)
        {
            var anchor = lastExecutionAt ?? trigger.AnchorUtc;
            if (anchor == null)
            {
                continue;
            }

            anchored = true;
            var fromUtc = AsUtc(anchor.Value);
            if (fromUtc >= toUtc)
            {
                continue;
            }

            // Exclusive start: the fire time that produced the last execution is not a miss.
            // Inclusive end: a fire time exactly `grace` ago has had its grace.
            foreach (var _ in trigger.Parsed.GetOccurrences(fromUtc, toUtc, zone, fromInclusive: false,
                         toInclusive: true))
            {
                if (++total >= MaxMissedExecutions)
                {
                    return MaxMissedExecutions;
                }
            }
        }

        return anchored ? total : null;
    }

    /// <summary>
    ///     The pipeline's expected cadence in seconds: the distance between the next two fire times,
    ///     the shortest one when several triggers apply. <c>null</c> when no trigger fires twice more
    ///     (an expression Cronos cannot advance). Two <c>GetNextOccurrence</c> steps per trigger, so it
    ///     is cheap enough to compute at collection time.
    /// </summary>
    public static double? TypicalIntervalSeconds(IReadOnlyList<CronTrigger> triggers, DateTime nowUtc,
        TimeZoneInfo zone)
    {
        double? shortest = null;
        var from = AsUtc(nowUtc);

        foreach (var trigger in triggers)
        {
            var first = trigger.Parsed.GetNextOccurrence(from, zone, inclusive: true);
            if (first == null)
            {
                continue;
            }

            var second = trigger.Parsed.GetNextOccurrence(first.Value, zone);
            if (second == null)
            {
                continue;
            }

            var seconds = (second.Value - first.Value).TotalSeconds;
            shortest = shortest == null ? seconds : Math.Min(shortest.Value, seconds);
        }

        return shortest;
    }

    /// <summary>
    ///     Cronos insists on <see cref="DateTimeKind.Utc" />. Statistics timestamps come back from
    ///     MongoDB as UTC, entity timestamps may come back unspecified; both denote UTC instants.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
