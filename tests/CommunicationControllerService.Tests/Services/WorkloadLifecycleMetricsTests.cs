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

    private static string UniqueTenant() => $"tenant-{Guid.NewGuid():N}";

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

    private static double? Gauge(List<Recorded> recorded, string tenantId, OctoObjectId rtId,
        string instrument = "octo.workload.hibernated") =>
        recorded.SingleOrDefault(r =>
            r.Instrument == instrument &&
            r.Tags["octo.tenant.id"] == tenantId &&
            r.Tags["octo.workload.rt_id"] == rtId.ToString())?.Value;
}
