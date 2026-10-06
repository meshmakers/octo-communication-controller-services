using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#4919. Scale-to-zero trades memory for latency and these instruments are the only place
///     that trade is visible, so a silently-broken tag or a wake that never gets counted would hide
///     exactly what the epic needs to prove.
///
///     Recorded through a real <see cref="MeterListener"/> rather than a seam, because the point of
///     the assertions is the instrument names and tags the exporter will actually publish.
/// </summary>
internal class WorkloadLifecycleMetricsTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    /// <summary>
    ///     Collects measurements on the lifecycle meter while <paramref name="act"/> runs, keeping
    ///     only those tagged with <paramref name="tenantId"/>. The instruments are process-wide and
    ///     the suite runs tests concurrently, so an unfiltered listener also sees every other
    ///     test's measurements — hence the unique tenant per test.
    /// </summary>
    private static List<Recorded> Collect(string tenantId, Action act, bool observeGauges = false)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkloadLifecycleMetrics.MeterName)
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
    ///     A fresh tenant that has opted into observability (AB#5432). Since these instruments are
    ///     governed by the same single per-tenant flag as the workload-state and pipeline families,
    ///     the opt-in is a precondition for all of them; the gate itself is pinned by the tests that
    ///     use <see cref="UniqueTenantWithoutOptIn" />.
    /// </summary>
    private static string UniqueTenant()
    {
        var tenantId = UniqueTenantWithoutOptIn();
        WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: true, TimeSpan.FromMinutes(5));
        return tenantId;
    }

    private static string UniqueTenantWithoutOptIn() => $"tenant-{Guid.NewGuid():N}";

    [Test]
    public async Task SuccessfulWake_CountsOnceAndRecordsItsDuration()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();

        // Act
        var recorded = Collect(tenantId, () => WorkloadLifecycleMetrics.RecordWakeSucceeded(tenantId, rtId,
            "Mesh Adapter", TimeSpan.FromSeconds(7.5)));

        // Assert
        var wake = recorded.Single(r => r.Instrument == "octo.workload.wake.count");
        await Assert.That(wake.Value).IsEqualTo(1);
        await Assert.That(wake.Tags["octo.wake.outcome"]).IsEqualTo("configured");
        await Assert.That(wake.Tags["octo.tenant.id"]).IsEqualTo(tenantId);
        await Assert.That(wake.Tags["octo.workload.rt_id"]).IsEqualTo(rtId.ToString());
        await Assert.That(wake.Tags["octo.workload.name"]).IsEqualTo("Mesh Adapter");

        var duration = recorded.Single(r => r.Instrument == "octo.workload.wake.duration");
        await Assert.That(duration.Value).IsEqualTo(7.5);
    }

    /// <summary>
    ///     The budget is a cut-off, not an observation: recording it would pull every percentile
    ///     towards the timeout and make wakes look slower than they are.
    /// </summary>
    [Test]
    public async Task TimedOutWake_IsCountedButNotRecordedAsADuration()
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId, () =>
            WorkloadLifecycleMetrics.RecordWakeTimedOut(tenantId, OctoObjectId.GenerateNewId(), "Mesh Adapter"));

        // Assert
        var wake = recorded.Single(r => r.Instrument == "octo.workload.wake.count");
        await Assert.That(wake.Tags["octo.wake.outcome"]).IsEqualTo("timeout");
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.wake.duration")).IsFalse();
    }

    [Test]
    public async Task HibernationAndWake_MoveTheGaugeBothWays()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();

        // Act — hibernate, then wake again.
        var afterHibernation = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter"), observeGauges: true);
        var afterWake = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordWakeSucceeded(tenantId, rtId, "Mesh Adapter", TimeSpan.FromSeconds(1)),
            observeGauges: true);

        // Assert
        await Assert.That(afterHibernation.Single(r => r.Instrument == "octo.workload.hibernation.count").Value)
            .IsEqualTo(1);
        await Assert.That(Gauge(afterHibernation, tenantId, rtId)).IsEqualTo(1);
        await Assert.That(Gauge(afterWake, tenantId, rtId)).IsEqualTo(0);
    }

    /// <summary>
    ///     The gauge map is in-memory, so a controller restart would report every workload as
    ///     running until it next hibernated. The watchdog republishes from the persisted state on
    ///     every sweep, which is what this covers.
    /// </summary>
    [Test]
    [Arguments(RtLifecycleStateEnum.Hibernated, 1)]
    [Arguments(RtLifecycleStateEnum.Draining, 1)]
    [Arguments(RtLifecycleStateEnum.Running, 0)]
    [Arguments(RtLifecycleStateEnum.Waking, 0)]
    public async Task ObservedState_PublishesTheGaugeFromThePersistedState(RtLifecycleStateEnum state, int expected)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.LifecycleState = state;

        // Act
        var recorded = Collect(tenantId, () => WorkloadLifecycleMetrics.ObserveState(tenantId, adapter),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(recorded, tenantId, adapter.RtId)).IsEqualTo(expected);
    }

    [Test]
    public async Task ForgottenWorkload_LeavesTheGauge()
    {
        // Arrange — an undeployed workload has no state to report; keeping it would show a
        // permanently hibernated workload that no longer exists.
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();
        WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter");

        // Act
        var recorded = Collect(tenantId, () => WorkloadLifecycleMetrics.Forget(tenantId, rtId), observeGauges: true);

        // Assert
        await Assert.That(recorded.Any(r =>
            r.Instrument == "octo.workload.hibernated" &&
            r.Tags["octo.workload.rt_id"] == rtId.ToString())).IsFalse();
    }

    /// <summary>
    ///     "Offline" stopped meaning "broken" the day scale-to-zero shipped. This gauge is the whole
    ///     alerting story: a hibernation must never raise it, anything else must.
    /// </summary>
    [Test]
    public async Task OfflineGauge_RisesOnlyWhenTheWorkloadDidNotGoDownOnPurpose()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();

        // Act
        var afterHibernation = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordOffline(tenantId, rtId, "Mesh Adapter", intentional: true),
            observeGauges: true);
        var afterCrash = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordOffline(tenantId, rtId, "Mesh Adapter", intentional: false),
            observeGauges: true);
        var afterRecovery = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordOnline(tenantId, rtId, "Mesh Adapter"), observeGauges: true);

        // Assert
        await Assert.That(Gauge(afterHibernation, tenantId, rtId, "octo.workload.offline_unexpected")).IsEqualTo(0);
        await Assert.That(Gauge(afterCrash, tenantId, rtId, "octo.workload.offline_unexpected")).IsEqualTo(1);
        await Assert.That(Gauge(afterRecovery, tenantId, rtId, "octo.workload.offline_unexpected")).IsEqualTo(0);
    }

    /// <summary>
    ///     The disconnect path only has the rtId, so it reports without a name. Blanking the label an
    ///     earlier caller supplied would leave the alert naming an id nobody recognises.
    /// </summary>
    [Test]
    public async Task ReportWithoutAName_KeepsTheNameAnEarlierOneSupplied()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();
        WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter");

        // Act
        var recorded = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordOffline(tenantId, rtId, workloadName: null, intentional: false),
            observeGauges: true);

        // Assert
        await Assert.That(recorded.First(r => r.Instrument == "octo.workload.offline_unexpected")
            .Tags["octo.workload.name"]).IsEqualTo("Mesh Adapter");
    }

    /// <summary>
    ///     AB#5432. These instruments predate the per-tenant flag and were unconditional — 20 series
    ///     from test-2 while nobody had opted in. One switch now governs the whole workload and
    ///     pipeline surface, so a tenant that never opted in must produce nothing here either.
    /// </summary>
    [Test]
    public async Task TenantThatDidNotOptIn_PublishesNothingAtAll()
    {
        // Arrange
        var tenantId = UniqueTenantWithoutOptIn();
        var rtId = OctoObjectId.GenerateNewId();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            WorkloadLifecycleMetrics.RecordWakeSucceeded(tenantId, rtId, "Mesh Adapter", TimeSpan.FromSeconds(3));
            WorkloadLifecycleMetrics.RecordWakeTimedOut(tenantId, rtId, "Mesh Adapter");
            WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter");
            WorkloadLifecycleMetrics.RecordOffline(tenantId, rtId, "Mesh Adapter", intentional: false);
        }, observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     Opting out must stop the two gauges rather than leave their last value on the wire. The gate
    ///     sits in the collection callback, so it takes effect on the next scrape.
    /// </summary>
    [Test]
    public async Task TenantThatOptsOut_StopsExportingTheGaugesItHadPublished()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var rtId = OctoObjectId.GenerateNewId();
        WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter");

        var whileOptedIn = Collect(tenantId, () => { }, observeGauges: true);

        // Act
        var afterOptOut = Collect(tenantId,
            () => WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: false, TimeSpan.FromMinutes(5)),
            observeGauges: true);

        // Assert
        await Assert.That(Gauge(whileOptedIn, tenantId, rtId)).IsEqualTo(1);
        await Assert.That(afterOptOut).IsEmpty();
    }

    /// <summary>
    ///     The state map is maintained for every tenant on purpose — only the export is gated — so a
    ///     tenant that opts in later reports its true hibernation state at the next scrape instead of
    ///     a stale zero until its next transition.
    /// </summary>
    [Test]
    public async Task TenantThatOptsInLater_ReportsTheStateItAlreadyHad()
    {
        // Arrange — the transition happens while the tenant is still opted out.
        var tenantId = UniqueTenantWithoutOptIn();
        var rtId = OctoObjectId.GenerateNewId();
        WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter");

        // Act
        var recorded = Collect(tenantId,
            () => WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: true, TimeSpan.FromMinutes(5)),
            observeGauges: true);

        // Assert — the gauge is there and correct; the hibernation counter of the opted-out window is
        // not, and cannot be: a counter has no state to replay.
        await Assert.That(Gauge(recorded, tenantId, rtId)).IsEqualTo(1);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.hibernation.count")).IsFalse();
    }

    /// <summary>
    ///     A verdict nobody refreshed can no longer be confirmed — the sweep that reads the flag has
    ///     stopped — so the gate fails closed rather than publishing on it.
    /// </summary>
    [Test]
    public async Task ExpiredOptIn_IsTreatedAsNotOptedIn()
    {
        // Arrange
        var tenantId = UniqueTenantWithoutOptIn();
        var rtId = OctoObjectId.GenerateNewId();
        WorkloadObservabilityOptIn.Refresh(tenantId, optedIn: true, TimeSpan.Zero);

        // Act
        var recorded = Collect(tenantId,
            () => WorkloadLifecycleMetrics.RecordHibernated(tenantId, rtId, "Mesh Adapter"), observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     A tenant that was switched off or deleted is never swept again; its workloads must not stay
    ///     in the map waiting to be re-exported if the tenant id ever comes back.
    /// </summary>
    [Test]
    public async Task ForgetTenant_DropsEveryWorkloadOfThatTenant()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var first = OctoObjectId.GenerateNewId();
        var second = OctoObjectId.GenerateNewId();
        WorkloadLifecycleMetrics.RecordHibernated(tenantId, first, "Mesh Adapter");
        WorkloadLifecycleMetrics.RecordOffline(tenantId, second, "Energy App", intentional: false);

        // Act
        var recorded = Collect(tenantId, () => WorkloadLifecycleMetrics.ForgetTenant(tenantId), observeGauges: true);

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    private static double? Gauge(List<Recorded> recorded, string tenantId, OctoObjectId rtId,
        string instrument = "octo.workload.hibernated") =>
        recorded.SingleOrDefault(r =>
            r.Instrument == instrument &&
            r.Tags["octo.tenant.id"] == tenantId &&
            r.Tags["octo.workload.rt_id"] == rtId.ToString())?.Value;
}
