using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
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
