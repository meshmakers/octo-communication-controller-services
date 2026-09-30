using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.BackgroundServices;

/// <summary>
///     Pins the AB#5432 workload-state sweep. Three things have to hold and each of them is a way the
///     feature would otherwise fail quietly: a tenant that never opted in must publish nothing (the
///     opt-in is what keeps observability from being billed for every test tenant), an opted-in
///     tenant must publish adapters, applications AND pools in one pass, and a tenant that drops out
///     must stop publishing — otherwise its sweep-age gauge climbs forever and pages somebody about
///     a tenant nobody is watching.
///
///     <para>
///         It is also the process's single reader of <c>PublishWorkloadObservability</c>: the verdict
///         it publishes into <see cref="WorkloadObservabilityOptIn" /> is what gates the pipeline and
///         lifecycle instruments, which cannot read the flag themselves. That wiring is pinned here
///         too — it has no other test that would notice it breaking.
///     </para>
/// </summary>
internal class WorkloadStateMetricsBackgroundServiceTests
{
    private sealed record Recorded(string Instrument, Dictionary<string, string> Tags);

    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly WorkloadStateMetricsBackgroundService _service;

    public WorkloadStateMetricsBackgroundServiceTests()
    {
        _service = new WorkloadStateMetricsBackgroundService(
            _adapterCache,
            _repository,
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()));
    }

    [After(Test)]
    public void DisposeService()
    {
        // BackgroundService owns a stopping CTS; the tests never start the loop, but it still has to
        // be disposed. Forget the tenants too — the instruments are process-wide.
        _service.Dispose();
        WorkloadStateMetrics.ForgetTenant(_tenantId);
        PipelineExecutionMetrics.ForgetTenant(_tenantId);
        WorkloadLifecycleMetrics.ForgetTenant(_tenantId);
        WorkloadObservabilityOptIn.Forget(_tenantId);
    }

    private readonly string _tenantId = $"tenant-{Guid.NewGuid():N}";

    private List<Recorded> Collect(Func<Task> act)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkloadStateMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, ToDictionary(tags))));
        // The pipeline and lifecycle families share this meter and report longs — they are gated by
        // the same opt-in this sweep publishes, so the tests below have to see them.
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, ToDictionary(tags))));
        listener.Start();

        act().GetAwaiter().GetResult();
        listener.RecordObservableInstruments();

        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.tenant.id") == _tenantId).ToList();
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

    private void ArrangeTenant(bool optedIn, IReadOnlyCollection<RtDeployableWorkload> workloads,
        IReadOnlyCollection<RtPool> pools)
    {
        _adapterCache.GetEnabledTenantIds().Returns([_tenantId]);
        _repository.IsWorkloadObservabilityEnabledAsync(_tenantId).Returns(optedIn);
        _repository.GetWorkloadsAsync(_tenantId).Returns(workloads);
        _repository.GetPoolsAsync(_tenantId).Returns(pools);
    }

    /// <summary>
    ///     The opt-in is the whole cost control: a test tenant nobody cares about must not produce a
    ///     single series, and the sweep must not even read its entities.
    /// </summary>
    [Test]
    public async Task TenantThatDidNotOptIn_PublishesNothingAndIsNotRead()
    {
        // Arrange
        ArrangeTenant(optedIn: false, [RtEntityCreator.CreateAdapter()], [RtEntityCreator.CreatePool()]);

        // Act
        var recorded = Collect(() => _service.SweepAllTenantsAsync());

        // Assert
        await Assert.That(recorded).IsEmpty();
        await _repository.DidNotReceive().GetWorkloadsAsync(_tenantId);
        await _repository.DidNotReceive().GetPoolsAsync(_tenantId);
    }

    /// <summary>
    ///     One pass covers all three kinds. Pools are read separately because they are the layer
    ///     above a workload — the same split the operator model makes — and forgetting one of the
    ///     three reads is how a whole class of entity would silently never be observable.
    /// </summary>
    [Test]
    public async Task OptedInTenant_PublishesAdaptersApplicationsAndPools()
    {
        // Arrange
        var adapter = RtEntityCreator.CreateAdapter();
        var application = RtEntityCreator.CreateApplication();
        var pool = RtEntityCreator.CreatePool();
        ArrangeTenant(optedIn: true, [adapter, application], [pool]);

        // Act
        var recorded = Collect(() => _service.SweepAllTenantsAsync());

        // Assert
        var deployment = recorded.Where(r => r.Instrument == "octo.workload.deployment_state").ToList();
        await Assert.That(deployment.Count).IsEqualTo(3);
        await Assert.That(deployment.Select(r => r.Tags["octo.workload.kind"]).Order().ToList())
            .IsEquivalentTo(new List<string>
            {
                WorkloadStateMetrics.AdapterKind,
                WorkloadStateMetrics.ApplicationKind,
                WorkloadStateMetrics.PoolKind,
            });

        // Adapter and pool carry the other two gauges; the application carries neither.
        await Assert.That(recorded.Count(r => r.Instrument == "octo.workload.communication_state")).IsEqualTo(2);
        await Assert.That(recorded.Count(r => r.Instrument == "octo.workload.configuration_state")).IsEqualTo(2);

        // And the sign of life.
        await Assert.That(recorded.Count(r => r.Instrument == "octo.workload.sweep.age")).IsEqualTo(1);
        await Assert.That(recorded.Count(r => r.Instrument == "octo.workload.sweep.entities")).IsEqualTo(1);
    }

    /// <summary>
    ///     A tenant that was switched off, or opted back out, must stop publishing — including its
    ///     heartbeat, which would otherwise rise without bound and alert about nothing.
    /// </summary>
    [Test]
    public async Task TenantThatDropsOutBetweenSweeps_IsForgotten()
    {
        // Arrange
        ArrangeTenant(optedIn: true, [RtEntityCreator.CreateAdapter()], []);

        // Act
        var recorded = Collect(async () =>
        {
            await _service.SweepAllTenantsAsync();
            _repository.IsWorkloadObservabilityEnabledAsync(_tenantId).Returns(false);
            await _service.SweepAllTenantsAsync();
        });

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     AB#5432: one switch, one reader. This sweep is the only code that reads
    ///     <c>PublishWorkloadObservability</c>, and the pipeline and lifecycle instruments — which are
    ///     fed from event paths and gauge callbacks that cannot await a repository — decide from the
    ///     verdict it publishes. If this wiring broke, those two families would go silent for every
    ///     tenant and no test of theirs would notice.
    /// </summary>
    [Test]
    public async Task OptedInTenant_AlsoOpensTheGateForThePipelineAndLifecycleFamilies()
    {
        // Arrange
        ArrangeTenant(optedIn: true, [RtEntityCreator.CreateAdapter()], []);
        var pipeline = RtEntityCreator.CreatePipeline();
        var workloadRtId = OctoObjectId.GenerateNewId();

        // Act
        var recorded = Collect(async () =>
        {
            await _service.SweepAllTenantsAsync();

            PipelineExecutionMetrics.ObservePipeline(_tenantId, pipeline);
            PipelineExecutionMetrics.ObserveStatistics(_tenantId, pipeline.RtId, DateTime.UtcNow, 1, 0);
            PipelineExecutionMetrics.RecordExecutionOutcome(_tenantId, RtPipelineExecutionStatusEnum.Failed);
            WorkloadLifecycleMetrics.RecordHibernated(_tenantId, workloadRtId, "Mesh Adapter");
        });

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument == "octo.pipeline.execution.count")).IsTrue();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.pipeline.execution.age")).IsTrue();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.hibernation.count")).IsTrue();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.hibernated")).IsTrue();
    }

    /// <summary>
    ///     Opting out has to stop <b>all three</b> families and drop their state. The gauges would stop
    ///     on their own (their gate is in the collection callback), but the in-memory entries have to go
    ///     as well, or a tenant that opts back in weeks later reports the state it had when it left.
    ///     This sweep is the only place that sees the transition.
    /// </summary>
    [Test]
    public async Task TenantThatOptsOut_ForgetsThePipelineAndLifecycleStateAsWell()
    {
        // Arrange — a tenant reporting in all three families.
        ArrangeTenant(optedIn: true, [RtEntityCreator.CreateAdapter()], []);
        var pipeline = RtEntityCreator.CreatePipeline();
        var workloadRtId = OctoObjectId.GenerateNewId();

        await _service.SweepAllTenantsAsync();
        PipelineExecutionMetrics.ObservePipeline(_tenantId, pipeline);
        PipelineExecutionMetrics.ObserveStatistics(_tenantId, pipeline.RtId, DateTime.UtcNow, 1, 0);
        WorkloadLifecycleMetrics.RecordHibernated(_tenantId, workloadRtId, "Mesh Adapter");

        // Act — the tenant opts out, the sweep notices, and the tenant is opted back in afterwards so
        // that anything left in the maps would show up again.
        var recorded = Collect(async () =>
        {
            _repository.IsWorkloadObservabilityEnabledAsync(_tenantId).Returns(false);
            await _service.SweepAllTenantsAsync();
            WorkloadObservabilityOptIn.Refresh(_tenantId, optedIn: true, TimeSpan.FromMinutes(5));
        });

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     An opt-in that cannot be read is not an opt-in, and it must not cost the other tenants their
    ///     observability either — one broken tenant configuration used to be enough to abort the pass.
    /// </summary>
    [Test]
    public async Task TenantWhoseOptInReadThrows_IsTreatedAsOptedOutAndTheSweepCarriesOn()
    {
        // Arrange
        var healthyTenantId = $"tenant-{Guid.NewGuid():N}";
        _adapterCache.GetEnabledTenantIds().Returns([_tenantId, healthyTenantId]);
        _repository.IsWorkloadObservabilityEnabledAsync(_tenantId)
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));
        _repository.IsWorkloadObservabilityEnabledAsync(healthyTenantId).Returns(true);
        _repository.GetWorkloadsAsync(healthyTenantId).Returns([RtEntityCreator.CreateAdapter()]);
        _repository.GetPoolsAsync(healthyTenantId).Returns([]);

        try
        {
            // Act
            var recorded = Collect(() => _service.SweepAllTenantsAsync());

            // Assert — the broken tenant publishes nothing and is never read for entities…
            await Assert.That(recorded).IsEmpty();
            await _repository.DidNotReceive().GetWorkloadsAsync(_tenantId);
            await Assert.That(WorkloadObservabilityOptIn.IsEnabled(_tenantId)).IsFalse();
            // …and the tenant behind it in the list still does.
            await Assert.That(WorkloadObservabilityOptIn.IsEnabled(healthyTenantId)).IsTrue();
        }
        finally
        {
            WorkloadStateMetrics.ForgetTenant(healthyTenantId);
            WorkloadObservabilityOptIn.Forget(healthyTenantId);
        }
    }

    /// <summary>
    ///     A sweep that throws must not stamp the liveness signal: leaving the previous values in
    ///     place and letting the age rise is the honest report, and it is what makes a tenant whose
    ///     reads keep failing visible at all.
    /// </summary>
    [Test]
    public async Task TenantWhoseReadFails_DoesNotStampTheHeartbeatAndDoesNotFailTheSweep()
    {
        // Arrange
        _adapterCache.GetEnabledTenantIds().Returns([_tenantId]);
        _repository.IsWorkloadObservabilityEnabledAsync(_tenantId).Returns(true);
        _repository.GetWorkloadsAsync(_tenantId)
            .Returns<IReadOnlyCollection<RtDeployableWorkload>>(_ => throw new InvalidOperationException("boom"));

        // Act
        var recorded = Collect(() => _service.SweepAllTenantsAsync());

        // Assert
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.sweep.age")).IsFalse();
    }
}
