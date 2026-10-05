using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#5425. These instruments are the only thing that can ever alert on a pipeline that fails
///     or quietly stops running, so a silently-broken tag, a wake that never gets counted or —
///     worst of all — a healthy pipeline that publishes nothing would hide exactly the outage the
///     work item exists for. The <c>Monitoring.CkHealth</c> pipeline on test-2 stood still for four
///     hours without anyone noticing; that is the scenario the gauge tests reproduce.
///
///     Recorded through a real <see cref="MeterListener" /> rather than a seam, because the point
///     of the assertions is the instrument names and tags the exporter will actually publish.
/// </summary>
internal class PipelineExecutionMetricsTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    /// <summary>
    ///     Collects measurements on the communication meter while <paramref name="act" /> runs,
    ///     keeping only those tagged with <paramref name="tenantId" />. The instruments are
    ///     process-wide and the suite runs tests concurrently, so an unfiltered listener also sees
    ///     every other test's measurements — hence the unique tenant per test.
    /// </summary>
    private static List<Recorded> Collect(string tenantId, Action act, bool observeGauges = false)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineExecutionMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        act();

        if (observeGauges)
        {
            listener.RecordObservableInstruments();
        }

        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.tenant.id") == tenantId).ToList();
    }

    private static Dictionary<string, string> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return map;
    }

    /// <summary>
    ///     A fresh tenant that has opted into observability (AB#5432). The opt-in is a precondition
    ///     for every instrument in this class, so it is arranged here rather than repeated in twenty
    ///     tests; the tests that pin the gate itself use <see cref="UniqueTenantWithoutOptIn" />.
    /// </summary>
    private static string UniqueTenant()
    {
        var tenantId = UniqueTenantWithoutOptIn();
        WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: true, TimeSpan.FromMinutes(5));
        return tenantId;
    }

    private static string UniqueTenantWithoutOptIn() => $"tenant-{Guid.NewGuid():N}";

    private static double? Gauge(List<Recorded> recorded, string instrument, OctoObjectId pipelineRtId) =>
        recorded.SingleOrDefault(r =>
            r.Instrument == instrument &&
            r.Tags["octo.pipeline.rt_id"] == pipelineRtId.ToString())?.Value;

    [Test]
    [Arguments(RtPipelineExecutionStatusEnum.Completed, "completed")]
    [Arguments(RtPipelineExecutionStatusEnum.Failed, "failed")]
    [Arguments(RtPipelineExecutionStatusEnum.Cancelled, "cancelled")]
    [Arguments(RtPipelineExecutionStatusEnum.Interrupted, "interrupted")]
    public async Task ReportedOutcome_IsCountedUnderItsOwnLabel(RtPipelineExecutionStatusEnum status,
        string expectedOutcome)
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId, () => PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, status));

        // Assert
        var execution = recorded.Single(r => r.Instrument == "octo.pipeline.execution.count");
        await Assert.That(execution.Value).IsEqualTo(1);
        await Assert.That(execution.Tags["octo.pipeline.outcome"]).IsEqualTo(expectedOutcome);
        await Assert.That(execution.Tags["octo.tenant.id"]).IsEqualTo(tenantId);
        // Deliberately not per pipeline: the hot path stays cheap, the gauges carry the detail.
        await Assert.That(execution.Tags.ContainsKey("octo.pipeline.rt_id")).IsFalse();
    }

    /// <summary>
    ///     <c>Running</c> is not an outcome. Counting it would make the failure ratio meaningless
    ///     and would double-count every execution that later reports a real result.
    /// </summary>
    [Test]
    public async Task RunningExecution_IsNotAnOutcomeAndIsNotCounted()
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId, () =>
            PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, RtPipelineExecutionStatusEnum.Running));

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    [Test]
    public async Task BatchOfOutcomes_IsCountedInOneMeasurement()
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId, () =>
            PipelineExecutionMetrics.RecordExecutionOutcomes(tenantId, RtPipelineExecutionStatusEnum.Failed, 7));

        // Assert
        var execution = recorded.Single(r => r.Instrument == "octo.pipeline.execution.count");
        await Assert.That(execution.Value).IsEqualTo(7);
        await Assert.That(execution.Tags["octo.pipeline.outcome"]).IsEqualTo("failed");
    }

    [Test]
    public async Task EmptyBatch_RecordsNothing()
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId, () =>
            PipelineExecutionMetrics.RecordExecutionOutcomes(tenantId, RtPipelineExecutionStatusEnum.Completed, 0));

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     The whole point of the work item. The existing <c>octo.ck.*</c> gauges are fault-only, so
    ///     "repaired" and "dead" look identical and no staleness rule can be written on them. A
    ///     healthy pipeline must publish zeros, not silence.
    /// </summary>
    [Test]
    public async Task HealthyPipeline_PublishesZerosRatherThanNothing()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Name = "Monitoring.CkHealth";

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 12, 0);
        }, observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.failures", pipeline.RtId)).IsEqualTo(0);
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.successes", pipeline.RtId)).IsEqualTo(12);

        var failures = recorded.Single(r => r.Instrument == "octo.pipeline.execution.failures");
        await Assert.That(failures.Tags["octo.pipeline.name"]).IsEqualTo("Monitoring.CkHealth");
        await Assert.That(failures.Tags["octo.pipeline.deployment_state"]).IsEqualTo("deployed");
    }

    /// <summary>
    ///     The outage this work item comes from: a pipeline that ran twice and then stood still for
    ///     four hours. The age has to keep climbing so a threshold can catch it.
    /// </summary>
    [Test]
    public async Task StalledPipeline_ReportsARisingAge()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId,
                DateTime.UtcNow.AddHours(-4), 2, 0);
        }, observeGauges: true);

        // Assert — four hours, give or take the time the test itself takes.
        var age = Gauge(recorded, "octo.pipeline.execution.age", pipeline.RtId);
        await Assert.That(age).IsNotNull();
        await Assert.That(age!.Value).IsBetween(4 * 3600 - 60, 4 * 3600 + 60);
    }

    /// <summary>
    ///     A pipeline that has never executed must still have a series — otherwise "no data" would
    ///     again mean two different things. It reports the sentinel instead of an age, which every
    ///     <c>&gt; threshold</c> rule excludes on its own.
    /// </summary>
    [Test]
    public async Task NeverExecutedPipeline_StillPublishesASeries()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, null, 0, 0);
        }, observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", pipeline.RtId))
            .IsEqualTo(PipelineExecutionMetrics.NeverExecuted);
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.failures", pipeline.RtId)).IsEqualTo(0);
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.successes", pipeline.RtId)).IsEqualTo(0);
    }

    /// <summary>
    ///     A deliberately switched-off pipeline stops running by design. It stays visible — silence
    ///     would be ambiguous again — but the tag lets the alert rule leave it alone.
    /// </summary>
    [Test]
    public async Task DisabledPipeline_IsTaggedSoAnAlertCanSkipIt()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Enabled = false;

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId,
                DateTime.UtcNow.AddDays(-30), 0, 0);
        }, observeGauges: true);

        // Assert — still reported, but as "disabled" even though DeploymentState still says Deployed.
        var age = recorded.Single(r => r.Instrument == "octo.pipeline.execution.age");
        await Assert.That(age.Tags["octo.pipeline.deployment_state"]).IsEqualTo("disabled");
    }

    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed, "undeployed")]
    [Arguments(RtDeploymentStateEnum.Pending, "pending")]
    [Arguments(RtDeploymentStateEnum.Deployed, "deployed")]
    [Arguments(RtDeploymentStateEnum.Error, "error")]
    public async Task DeploymentState_IsCarriedAsALowerCaseTag(RtDeploymentStateEnum state, string expected)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.DeploymentState = state;

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 0, 0);
        }, observeGauges: true);

        // Assert
        await Assert.That(recorded.First(r => r.Instrument == "octo.pipeline.execution.age")
            .Tags["octo.pipeline.deployment_state"]).IsEqualTo(expected);
    }

    /// <summary>
    ///     A deleted pipeline whose age gauge kept climbing would eventually alert about something
    ///     nobody can fix. The sweep hands in what still exists; everything else has to go.
    /// </summary>
    [Test]
    public async Task RetainPipelines_DropsWhatTheSweepNoLongerSees()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var surviving = RtEntityCreator.CreatePipeline();
        var deleted = RtEntityCreator.CreatePipeline();
        foreach (var pipeline in new[] { surviving, deleted })
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 0, 0);
        }

        // Act
        var recorded = Collect(tenantId,
            () => PipelineExecutionMetrics.RetainPipelines(tenantId, new HashSet<string> { surviving.RtId.ToString() }),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", surviving.RtId)).IsNotNull();
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", deleted.RtId)).IsNull();
    }

    /// <summary>
    ///     A tenant that was switched off is never swept again, so its pipelines would report a
    ///     forever-rising age.
    /// </summary>
    [Test]
    public async Task ForgetTenant_DropsEveryPipelineOfThatTenant()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
        PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 0, 0);

        // Act
        var recorded = Collect(tenantId, () => PipelineExecutionMetrics.ForgetTenant(tenantId), observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     AB#5432. One per-tenant flag decides whether a tenant's workload and pipeline
    ///     observability is published at all, and a tenant that never opted in must produce nothing —
    ///     not a counter increment, not a single gauge series. This is the half that used to be
    ///     ungated: 175 pipeline series were on the wire from test-2 while nobody had opted in.
    /// </summary>
    [Test]
    public async Task TenantThatDidNotOptIn_PublishesNothingAtAll()
    {
        // Arrange
        var tenantId = UniqueTenantWithoutOptIn();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, RtPipelineExecutionStatusEnum.Failed);
            PipelineExecutionMetrics.RecordExecutionOutcomes(tenantId, RtPipelineExecutionStatusEnum.Completed, 5);
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 3, 1);
        }, observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     Opting out has to <b>stop</b> the series, not freeze them at their last value — otherwise a
    ///     tenant that switched observability off keeps a stalled-looking age on the wire until the pod
    ///     restarts. The gate lives in the collection callback, which is what makes this immediate.
    /// </summary>
    [Test]
    public async Task TenantThatOptsOut_StopsExportingTheGaugesItHadPublished()
    {
        // Arrange — a tenant that published, then opted out.
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
        PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 3, 1);

        var whileOptedIn = Collect(tenantId, () => { }, observeGauges: true);

        // Act
        var afterOptOut = Collect(tenantId,
            () => WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: false, TimeSpan.FromMinutes(5)),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(whileOptedIn, "octo.pipeline.execution.age", pipeline.RtId)).IsNotNull();
        await Assert.That(afterOptOut).IsEmpty();
    }

    /// <summary>
    ///     A verdict nobody has refreshed is a verdict this process can no longer confirm — the sweep
    ///     that reads the flag has stopped. Publishing on it would be the same mistake as treating an
    ///     unreadable opt-in as an opt-in, so the gate fails closed.
    /// </summary>
    [Test]
    public async Task ExpiredOptIn_IsTreatedAsNotOptedIn()
    {
        // Arrange
        var tenantId = UniqueTenantWithoutOptIn();
        var pipeline = RtEntityCreator.CreatePipeline();
        WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: true, TimeSpan.Zero);

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 3, 1);
            PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, RtPipelineExecutionStatusEnum.Failed);
        }, observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     Identity and numbers arrive from two different places in the sweep. A statistics update
    ///     that lands first must not leave the series without a readable name, and it must not blank
    ///     one the other call already supplied.
    /// </summary>
    [Test]
    public async Task IdentityAndStatistics_MergeIntoOneSeriesWhicheverArrivesFirst()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Name = "Accounting.ImportMail";

        // Act
        var recorded = Collect(tenantId, () =>
        {
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, DateTime.UtcNow, 3, 1);
            PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
        }, observeGauges: true);

        // Assert
        var failures = recorded.Single(r => r.Instrument == "octo.pipeline.execution.failures");
        await Assert.That(failures.Value).IsEqualTo(1);
        await Assert.That(failures.Tags["octo.pipeline.name"]).IsEqualTo("Accounting.ImportMail");
    }

    // ---------------------------------------------------------------------------------------------
    // AB#5492 — octo.pipeline.cron.missed_executions / octo.pipeline.cron.interval
    //
    // The prod-1 finAPI adapter failed on every hourly cron execution for five days while every
    // health signal stayed green; the age gauge rose but cannot be alerted on because an idle HTTP
    // pipeline legitimately has an age of months. These tests pin the series contract the Dash0 rule
    // is written against: the names, the four tags, zero for a healthy pipeline, N after N fire
    // times, and — as important — NO series for pipelines the schedule says nothing about.
    //
    // Wall-clock tests: an interval of exactly k hours ending at `now - grace` contains exactly k
    // hourly fire times (exclusive start, inclusive end) unless a fire time coincides with the
    // start to the millisecond. The exact boundary cases live in PipelineCronScheduleTests.
    // ---------------------------------------------------------------------------------------------

    private static RtPipelineTrigger HourlyTrigger(DateTime? changedAt = null)
    {
        var trigger = RtEntityCreator.CreatePipelineTrigger("0 * * * *");
        trigger.RtChangedDateTime = changedAt;
        return trigger;
    }

    private static void ObserveCronPipeline(string tenantId, RtPipeline pipeline, DateTime? lastExecutionAt,
        params RtPipelineTrigger[] triggers)
    {
        PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline);
        PipelineExecutionMetrics.ObserveCronTriggers(tenantId, pipeline.RtId, triggers);
        PipelineExecutionMetrics.ObserveStatistics(tenantId, pipeline.RtId, lastExecutionAt, 0, 0);
    }

    [Test]
    public async Task CronPipelineOnSchedule_PublishesZeroMissedExecutionsWithTheContractTags()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Name = "Accounting.FinApiImport";

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow, HourlyTrigger()),
            observeGauges: true);

        // Assert
        var missed = recorded.Single(r => r.Instrument == "octo.pipeline.cron.missed_executions");
        await Assert.That(missed.Value).IsEqualTo(0);
        await Assert.That(missed.Tags["octo.tenant.id"]).IsEqualTo(tenantId);
        await Assert.That(missed.Tags["octo.pipeline.rt_id"]).IsEqualTo(pipeline.RtId.ToString());
        await Assert.That(missed.Tags["octo.pipeline.name"]).IsEqualTo("Accounting.FinApiImport");
        await Assert.That(missed.Tags["octo.cron.expression"]).IsEqualTo("0 * * * *");
        // Exactly the four tags of the contract — the series only exists while deployed, so no state tag.
        await Assert.That(missed.Tags.Count).IsEqualTo(4);

        await Assert.That(Gauge(recorded, "octo.pipeline.cron.interval", pipeline.RtId)).IsEqualTo(3600);
    }

    /// <summary>The finAPI outage: hourly cron, last execution three fire times ago.</summary>
    [Test]
    public async Task CronPipelineThatMissedThreeFireTimes_PublishesThree()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var lastExecutionAt = DateTime.UtcNow.Subtract(PipelineCronSchedule.Grace).AddHours(-3);

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, lastExecutionAt, HourlyTrigger()),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.cron.missed_executions", pipeline.RtId)).IsEqualTo(3);
    }

    /// <summary>
    ///     The reason this is a separate instrument and not a threshold on the age: an HTTP or
    ///     data-event pipeline has no schedule, so its silence is not a stall and must not even be a
    ///     series the rule could match.
    /// </summary>
    [Test]
    public async Task PipelineWithoutCronTrigger_HasNoCronSeriesButKeepsItsOtherGauges()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-90)),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument.StartsWith("octo.pipeline.cron."))).IsFalse();
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", pipeline.RtId)).IsNotNull();
    }

    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed)]
    [Arguments(RtDeploymentStateEnum.Pending)]
    [Arguments(RtDeploymentStateEnum.Error)]
    public async Task CronPipelineThatIsNotDeployed_HasNoCronSeries(RtDeploymentStateEnum state)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.DeploymentState = state;

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-3), HourlyTrigger()),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument.StartsWith("octo.pipeline.cron."))).IsFalse();
    }

    /// <summary>Same collapsed predicate as the state tag: a disabled pipeline is not expected to fire.</summary>
    [Test]
    public async Task DisabledCronPipeline_HasNoCronSeries()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Enabled = false;

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-3), HourlyTrigger()),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument.StartsWith("octo.pipeline.cron."))).IsFalse();
    }

    /// <summary>A trigger the operator switched off is not a schedule, whatever the sweep hands in.</summary>
    [Test]
    public async Task DisabledTrigger_IsNotASchedule()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var trigger = HourlyTrigger();
        trigger.Enabled = false;

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-3), trigger),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument.StartsWith("octo.pipeline.cron."))).IsFalse();
    }

    /// <summary>
    ///     A pipeline that never executed has no <c>LastExecutionAt</c>. Counting starts at the
    ///     trigger's modification timestamp — a schedule that never produced a run is a stall too.
    /// </summary>
    [Test]
    public async Task NeverExecutedCronPipeline_CountsFromTheTriggerTimestamp()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var trigger = HourlyTrigger(changedAt: DateTime.UtcNow.Subtract(PipelineCronSchedule.Grace).AddHours(-2));

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, null, trigger),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.cron.missed_executions", pipeline.RtId)).IsEqualTo(2);
    }

    /// <summary>
    ///     No execution and no trigger timestamp: nothing can be said, so nothing is published —
    ///     a zero here would read as "on schedule". The age gauge still carries its sentinel.
    /// </summary>
    [Test]
    public async Task NeverExecutedCronPipelineWithoutAnchor_HasNoCronSeries()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var trigger = HourlyTrigger();
        trigger.RtCreationDateTime = null;

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, null, trigger),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.cron.missed_executions", pipeline.RtId)).IsNull();
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", pipeline.RtId))
            .IsEqualTo(PipelineExecutionMetrics.NeverExecuted);
    }

    [Test]
    public async Task CronPipelineStalledForAMonth_IsCappedAtTenThousand()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var everyMinute = RtEntityCreator.CreatePipelineTrigger("* * * * *");

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-30), everyMinute),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, "octo.pipeline.cron.missed_executions", pipeline.RtId))
            .IsEqualTo(PipelineCronSchedule.MaxMissedExecutions);
    }

    /// <summary>Several triggers: expressions joined with ';', fire times summed, shortest interval.</summary>
    [Test]
    public async Task SeveralCronTriggers_AreJoinedInTheTagAndSummedInTheValue()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        var lastExecutionAt = DateTime.UtcNow.Subtract(PipelineCronSchedule.Grace).AddHours(-2);

        // Act — hourly misses 2, the quarter-hourly one 8.
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, lastExecutionAt, HourlyTrigger(),
                RtEntityCreator.CreatePipelineTrigger("*/15 * * * *")),
            observeGauges: true);

        // Assert
        var missed = recorded.Single(r => r.Instrument == "octo.pipeline.cron.missed_executions");
        await Assert.That(missed.Value).IsEqualTo(10);
        await Assert.That(missed.Tags["octo.cron.expression"]).IsEqualTo("0 * * * *;*/15 * * * *");
        await Assert.That(Gauge(recorded, "octo.pipeline.cron.interval", pipeline.RtId)).IsEqualTo(900);
    }

    /// <summary>
    ///     A malformed expression could never have been scheduled; it must not take down the series
    ///     of a pipeline whose other trigger is fine.
    /// </summary>
    [Test]
    public async Task UnparsableCronExpression_IsIgnored()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow, HourlyTrigger(),
                RtEntityCreator.CreatePipelineTrigger("every full hour")),
            observeGauges: true);

        // Assert
        var missed = recorded.Single(r => r.Instrument == "octo.pipeline.cron.missed_executions");
        await Assert.That(missed.Tags["octo.cron.expression"]).IsEqualTo("0 * * * *");
    }

    /// <summary>
    ///     The sweep hands in the current trigger set on every pass. A trigger that was deleted or
    ///     disabled withdraws the series instead of leaving a stale count on the wire.
    /// </summary>
    [Test]
    public async Task RemovingTheLastCronTrigger_WithdrawsTheCronSeries()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pipeline = RtEntityCreator.CreatePipeline();
        ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-3), HourlyTrigger());

        // Act
        var recorded = Collect(tenantId,
            () => PipelineExecutionMetrics.ObserveCronTriggers(tenantId, pipeline.RtId, []),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument.StartsWith("octo.pipeline.cron."))).IsFalse();
        await Assert.That(Gauge(recorded, "octo.pipeline.execution.age", pipeline.RtId)).IsNotNull();
    }

    /// <summary>AB#5432 applies here as well: the cron gauges answer to the same per-tenant switch.</summary>
    [Test]
    public async Task CronGauges_HonourTheTenantOptIn()
    {
        // Arrange
        var tenantId = UniqueTenantWithoutOptIn();
        var pipeline = RtEntityCreator.CreatePipeline();

        // Act
        var recorded = Collect(tenantId,
            () => ObserveCronPipeline(tenantId, pipeline, DateTime.UtcNow.AddDays(-3), HourlyTrigger()),
            observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }
}
