using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#5432, item 3. These gauges are the only thing that can ever alert on a workload stuck in a
///     deployment error, an adapter that fell offline for a reason nobody chose, or the
///     configuration error Gerald asked for by name. Two kinds of mistake would make them worthless
///     and both are pinned here: a severity that reads healthy for a fault (nothing ever fires), and
///     a severity that reads faulty for a state somebody chose on purpose — a hibernated workload, a
///     deliberately disabled pool — which trains everyone to ignore the alert.
///
///     Recorded through a real <see cref="MeterListener" /> rather than a seam, because the point of
///     the assertions is the instrument names, values and tags the exporter will actually publish.
/// </summary>
internal class WorkloadStateMetricsTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    /// <summary>
    ///     Collects measurements on the communication meter while <paramref name="act" /> runs,
    ///     keeping only those tagged with <paramref name="tenantId" />. The instruments are
    ///     process-wide and the suite runs tests concurrently, so an unfiltered listener also sees
    ///     every other test's measurements — hence the unique tenant per test.
    /// </summary>
    private static List<Recorded> Collect(string tenantId, Action act)
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
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        act();
        listener.RecordObservableInstruments();

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

    private static Recorded? Entry(List<Recorded> recorded, string instrument, OctoObjectId rtId) =>
        recorded.SingleOrDefault(r =>
            r.Instrument == instrument && r.Tags["octo.workload.rt_id"] == rtId.ToString());

    private static double? Value(List<Recorded> recorded, string instrument, OctoObjectId rtId) =>
        Entry(recorded, instrument, rtId)?.Value;

    /// <summary>
    ///     Publishes one adapter and closes the sweep, the way the background service does.
    /// </summary>
    private static List<Recorded> Sweep(string tenantId, RtDeployableWorkload workload) =>
        Collect(tenantId, () =>
        {
            WorkloadStateMetrics.ObserveWorkload(tenantId, workload);
            WorkloadStateMetrics.CompleteSweep(tenantId, new HashSet<string> { workload.RtId.ToString() });
        });

    private static List<Recorded> SweepPool(string tenantId, RtDeploymentSite pool) =>
        Collect(tenantId, () =>
        {
            WorkloadStateMetrics.ObserveDeploymentSite(tenantId, pool);
            WorkloadStateMetrics.CompleteSweep(tenantId, new HashSet<string> { pool.RtId.ToString() });
        });

    // ---------------------------------------------------------------------------------------------
    // The three gauges exist, are labelled, and a healthy entity publishes zeros rather than silence
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     The difference to the fault-only <c>octo.ck.library.state</c>: a healthy adapter must be
    ///     visible, because "this adapter exists and is fine" is the fact an operator wants and
    ///     because it makes the absence of a series mean exactly one thing.
    /// </summary>
    [Test]
    public async Task HealthyAdapter_PublishesAllThreeGaugesAsZero()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "MeshAdapter";
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.CommunicationState = RtCommunicationStateEnum.Online;
        adapter.ConfigurationState = RtConfigurationStateEnum.Configured;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);

        var deployment = Entry(recorded, "octo.workload.deployment_state", adapter.RtId)!;
        await Assert.That(deployment.Tags["octo.workload.name"]).IsEqualTo("MeshAdapter");
        await Assert.That(deployment.Tags["octo.workload.kind"]).IsEqualTo(WorkloadStateMetrics.AdapterKind);
        await Assert.That(deployment.Tags["octo.workload.state"]).IsEqualTo("deployed");
        await Assert.That(Entry(recorded, "octo.workload.communication_state", adapter.RtId)!
            .Tags["octo.workload.state"]).IsEqualTo("online");
        await Assert.That(Entry(recorded, "octo.workload.configuration_state", adapter.RtId)!
            .Tags["octo.workload.state"]).IsEqualTo("configured");
    }

    [Test]
    public async Task DeploymentSite_PublishesAllThreeGaugesUnderTheDeploymentSiteKind()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var pool = RtEntityCreator.CreateDeploymentSite("Cloud");
        pool.DeploymentState = RtDeploymentStateEnum.Deployed;
        pool.CommunicationState = RtCommunicationStateEnum.Online;
        pool.ConfigurationState = RtConfigurationStateEnum.Configured;

        // Act
        var recorded = SweepPool(tenantId, pool);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", pool.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.communication_state", pool.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", pool.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Entry(recorded, "octo.workload.deployment_state", pool.RtId)!
            .Tags["octo.workload.kind"]).IsEqualTo(WorkloadStateMetrics.DeploymentSiteKind);
    }

    /// <summary>
    ///     AB#4924 / decision E4: an AdapterPool is a <c>DeployableWorkload</c> without communication
    ///     or configuration state. Before the 0.2-lane port it fell into the application branch and
    ///     was published as <c>application</c>; it now has a kind of its own and, like an
    ///     application, publishes the deployment state only.
    /// </summary>
    [Test]
    public async Task AdapterPool_PublishesDeploymentStateOnlyUnderTheAdapterPoolKind()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapterPool = RtEntityCreator.CreateAdapterPool("accounting-pool");
        adapterPool.DeploymentState = RtDeploymentStateEnum.Error;

        // Act
        var recorded = Sweep(tenantId, adapterPool);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", adapterPool.RtId))
            .IsEqualTo(WorkloadStateMetrics.Critical);
        await Assert.That(Entry(recorded, "octo.workload.deployment_state", adapterPool.RtId)!
            .Tags["octo.workload.kind"]).IsEqualTo(WorkloadStateMetrics.AdapterPoolKind);
        await Assert.That(Entry(recorded, "octo.workload.communication_state", adapterPool.RtId)).IsNull();
        await Assert.That(Entry(recorded, "octo.workload.configuration_state", adapterPool.RtId)).IsNull();
    }

    /// <summary>
    ///     AB#4924: a <c>Leased</c> adapter owns no process — it is never deployed and never comes
    ///     online. It must read healthy on every gauge (the not-expected-to-run suppression), or every
    ///     borrower in the estate would sit at a permanent warning.
    /// </summary>
    [Test]
    public async Task LeasedAdapter_NeverDeployedNorOnline_IsHealthyOnEveryGauge()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.LifecycleMode = RtLifecycleModeEnum.Leased;
        adapter.DeploymentState = RtDeploymentStateEnum.Undeployed;
        adapter.CommunicationState = RtCommunicationStateEnum.Unregistered;
        adapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
    }

    /// <summary>
    ///     Decision E4 (2026-10-07): the 0.2 lane publishes the 4.x names, no alias. The check rules
    ///     match on these literals, so they are pinned.
    /// </summary>
    [Test]
    public async Task KindLabels_AreTheAgreedLiterals()
    {
        await Assert.That(WorkloadStateMetrics.AdapterKind).IsEqualTo("adapter");
        await Assert.That(WorkloadStateMetrics.ApplicationKind).IsEqualTo("application");
        await Assert.That(WorkloadStateMetrics.DeploymentSiteKind).IsEqualTo("deployment_site");
        await Assert.That(WorkloadStateMetrics.AdapterPoolKind).IsEqualTo("adapter_pool");
    }

    /// <summary>
    ///     An Application has no <c>CommunicationState</c> / <c>ConfigurationState</c> in the CK
    ///     model. Reporting a fabricated zero would make "configured" mean two different things and
    ///     would hide the fact that the platform never asks an Application to configure itself.
    /// </summary>
    [Test]
    public async Task Application_PublishesOnlyTheDeploymentState()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var application = RtEntityCreator.CreateApplication("energy-community");
        application.DeploymentState = RtDeploymentStateEnum.Deployed;

        // Act
        var recorded = Sweep(tenantId, application);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", application.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Entry(recorded, "octo.workload.deployment_state", application.RtId)!
            .Tags["octo.workload.kind"]).IsEqualTo(WorkloadStateMetrics.ApplicationKind);
        await Assert.That(Entry(recorded, "octo.workload.communication_state", application.RtId)).IsNull();
        await Assert.That(Entry(recorded, "octo.workload.configuration_state", application.RtId)).IsNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Severity: what "bad" means
    // ---------------------------------------------------------------------------------------------

    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed, WorkloadStateMetrics.Healthy, "undeployed")]
    [Arguments(RtDeploymentStateEnum.Deployed, WorkloadStateMetrics.Healthy, "deployed")]
    [Arguments(RtDeploymentStateEnum.Disabled, WorkloadStateMetrics.Healthy, "disabled")]
    [Arguments(RtDeploymentStateEnum.Pending, WorkloadStateMetrics.Warning, "pending")]
    [Arguments(RtDeploymentStateEnum.Error, WorkloadStateMetrics.Critical, "error")]
    public async Task DeploymentState_IsMappedToItsSeverityAndNamed(RtDeploymentStateEnum state,
        int expectedSeverity, string expectedName)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = state;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        var entry = Entry(recorded, "octo.workload.deployment_state", adapter.RtId)!;
        await Assert.That(entry.Value).IsEqualTo(expectedSeverity);
        await Assert.That(entry.Tags["octo.workload.state"]).IsEqualTo(expectedName);
    }

    /// <summary>
    ///     The condition the work item exists for. A deployed adapter that lost its connection for
    ///     no chosen reason is the outage an operator has to be paged about.
    /// </summary>
    [Test]
    public async Task DeployedAdapterThatWentOffline_IsCritical()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        adapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        var entry = Entry(recorded, "octo.workload.communication_state", adapter.RtId)!;
        await Assert.That(entry.Value).IsEqualTo(WorkloadStateMetrics.Critical);
        await Assert.That(entry.Tags["octo.workload.state"]).IsEqualTo("offline");
    }

    [Test]
    public async Task DeployedAdapterThatNeverRegistered_IsAWarning()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.CommunicationState = RtCommunicationStateEnum.Unregistered;
        adapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Warning);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Warning);
    }

    /// <summary>
    ///     The state Gerald named explicitly: a configuration error is the fault, not a warning.
    /// </summary>
    [Test]
    public async Task ConfigurationError_IsCritical()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.CommunicationState = RtCommunicationStateEnum.Online;
        adapter.ConfigurationState = RtConfigurationStateEnum.Error;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        var entry = Entry(recorded, "octo.workload.configuration_state", adapter.RtId)!;
        await Assert.That(entry.Value).IsEqualTo(WorkloadStateMetrics.Critical);
        await Assert.That(entry.Tags["octo.workload.state"]).IsEqualTo("error");
        // The connection is fine — only the configuration is broken, and only that gauge says so.
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
    }

    /// <summary>
    ///     A configuration error survives a hibernation: the watchdog only drains a workload that is
    ///     otherwise fine, so an <c>Error</c> found here is real and must not be swallowed with the
    ///     incomplete states.
    /// </summary>
    [Test]
    public async Task HibernatedAdapterWithAConfigurationError_IsStillCritical()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.LifecycleState = RtLifecycleStateEnum.Hibernated;
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        adapter.ConfigurationState = RtConfigurationStateEnum.Error;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Critical);
    }

    // ---------------------------------------------------------------------------------------------
    // Severity: what must NOT read as bad
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     "Offline" stopped meaning "broken" the day scale-to-zero shipped (AB#4919). A hibernated
    ///     workload is offline and unconfigured by design — the offline write and the
    ///     <c>ConfigurationState</c> reset are both deliberate — so an alert on either would fire on
    ///     every single on-demand adapter and be switched off within a week.
    /// </summary>
    [Test]
    [Arguments(RtLifecycleStateEnum.Hibernated)]
    [Arguments(RtLifecycleStateEnum.Draining)]
    [Arguments(RtLifecycleStateEnum.Waking)]
    public async Task IntentionallyDownWorkload_ReportsHealthyCommunicationAndConfiguration(
        RtLifecycleStateEnum lifecycleState)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        adapter.LifecycleState = lifecycleState;
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        adapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        // Still reported, and still carrying the real state name — suppressed severity, not silence.
        await Assert.That(Entry(recorded, "octo.workload.communication_state", adapter.RtId)!
            .Tags["octo.workload.state"]).IsEqualTo("offline");
    }

    /// <summary>
    ///     Nothing is trying to have an Undeployed or Disabled entity online, and whatever its
    ///     communication and configuration attributes still hold is stale.
    /// </summary>
    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed)]
    [Arguments(RtDeploymentStateEnum.Disabled)]
    public async Task EntityThatIsNotMeantToRun_ReportsHealthyCommunicationAndConfiguration(
        RtDeploymentStateEnum deploymentState)
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = deploymentState;
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        adapter.ConfigurationState = RtConfigurationStateEnum.Error;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
    }

    /// <summary>
    ///     A failed deploy is one incident, not three. The deployment gauge fires; the offline-ness
    ///     that follows from it does not fire a second time.
    /// </summary>
    [Test]
    public async Task DeploymentError_IsReportedOnceAndNotAmplifiedByTheOtherTwoGauges()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Error;
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        adapter.ConfigurationState = RtConfigurationStateEnum.Error;

        // Act
        var recorded = Sweep(tenantId, adapter);

        // Assert
        await Assert.That(Value(recorded, "octo.workload.deployment_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Critical);
        await Assert.That(Value(recorded, "octo.workload.communication_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
        await Assert.That(Value(recorded, "octo.workload.configuration_state", adapter.RtId))
            .IsEqualTo(WorkloadStateMetrics.Healthy);
    }

    // ---------------------------------------------------------------------------------------------
    // Sign of life
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     The 29.9. failure: an emitter that stopped while the process kept running, unnoticed for
    ///     four hours. The age is computed at collection time so it keeps climbing and a threshold
    ///     catches it.
    /// </summary>
    [Test]
    public async Task CompletedSweep_PublishesAFreshAgeAndTheEntityCount()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();
        var pool = RtEntityCreator.CreateDeploymentSite();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            WorkloadStateMetrics.ObserveWorkload(tenantId, adapter);
            WorkloadStateMetrics.ObserveDeploymentSite(tenantId, pool);
            WorkloadStateMetrics.CompleteSweep(tenantId,
                new HashSet<string> { adapter.RtId.ToString(), pool.RtId.ToString() });
        });

        // Assert
        var age = recorded.Single(r => r.Instrument == "octo.workload.sweep.age");
        await Assert.That(age.Value).IsBetween(0, 60);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.workload.sweep.entities").Value)
            .IsEqualTo(2);
    }

    /// <summary>
    ///     An opted-in tenant with nothing to report must still say so — otherwise "no adapters yet"
    ///     and "the sweep is dead" are the same observation again.
    /// </summary>
    [Test]
    public async Task TenantWithNoEntities_StillPublishesTheHeartbeat()
    {
        // Arrange
        var tenantId = UniqueTenant();

        // Act
        var recorded = Collect(tenantId,
            () => WorkloadStateMetrics.CompleteSweep(tenantId, new HashSet<string>()));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.workload.sweep.entities").Value)
            .IsEqualTo(0);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.workload.sweep.age")).IsTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // Pruning
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     A deleted adapter whose severity kept reporting would alert about something that no longer
    ///     exists — and the alert would be unfixable, because there is nothing left to repair.
    /// </summary>
    [Test]
    public async Task EntityTheSweepNoLongerSees_IsDropped()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var kept = RtEntityCreator.CreateAdapter();
        kept.DeploymentState = RtDeploymentStateEnum.Deployed;
        var deleted = RtEntityCreator.CreateAdapter();
        deleted.DeploymentState = RtDeploymentStateEnum.Error;

        // Act
        var recorded = Collect(tenantId, () =>
        {
            WorkloadStateMetrics.ObserveWorkload(tenantId, kept);
            WorkloadStateMetrics.ObserveWorkload(tenantId, deleted);
            WorkloadStateMetrics.CompleteSweep(tenantId,
                new HashSet<string> { kept.RtId.ToString(), deleted.RtId.ToString() });

            // Next sweep: only the surviving adapter.
            WorkloadStateMetrics.ObserveWorkload(tenantId, kept);
            WorkloadStateMetrics.CompleteSweep(tenantId, new HashSet<string> { kept.RtId.ToString() });
        });

        // Assert
        await Assert.That(Entry(recorded, "octo.workload.deployment_state", kept.RtId)).IsNotNull();
        await Assert.That(Entry(recorded, "octo.workload.deployment_state", deleted.RtId)).IsNull();
    }

    /// <summary>
    ///     A tenant that was switched off or opted back out is never swept again, so its heartbeat
    ///     would climb forever and page somebody about a tenant nobody is watching.
    /// </summary>
    [Test]
    public async Task ForgottenTenant_PublishesNothingAtAll()
    {
        // Arrange
        var tenantId = UniqueTenant();
        var adapter = RtEntityCreator.CreateAdapter();

        // Act
        var recorded = Collect(tenantId, () =>
        {
            WorkloadStateMetrics.ObserveWorkload(tenantId, adapter);
            WorkloadStateMetrics.CompleteSweep(tenantId, new HashSet<string> { adapter.RtId.ToString() });
            WorkloadStateMetrics.ForgetTenant(tenantId);
        });

        // Assert
        await Assert.That(recorded).IsEmpty();
    }

    /// <summary>
    ///     Pruning is scoped to the tenant that was swept. One tenant's sweep must never drop
    ///     another tenant's series — the instruments are process-wide and shared by every tenant.
    /// </summary>
    [Test]
    public async Task SweepOfOneTenant_LeavesAnotherTenantsEntitiesAlone()
    {
        // Arrange
        var first = UniqueTenant();
        var second = UniqueTenant();
        var firstAdapter = RtEntityCreator.CreateAdapter();
        var secondAdapter = RtEntityCreator.CreateAdapter();

        // Act
        var recorded = Collect(first, () =>
        {
            WorkloadStateMetrics.ObserveWorkload(first, firstAdapter);
            WorkloadStateMetrics.CompleteSweep(first, new HashSet<string> { firstAdapter.RtId.ToString() });
            WorkloadStateMetrics.ObserveWorkload(second, secondAdapter);
            WorkloadStateMetrics.CompleteSweep(second, new HashSet<string> { secondAdapter.RtId.ToString() });
        });

        try
        {
            // Assert
            await Assert.That(Entry(recorded, "octo.workload.deployment_state", firstAdapter.RtId)).IsNotNull();
        }
        finally
        {
            WorkloadStateMetrics.ForgetTenant(first);
            WorkloadStateMetrics.ForgetTenant(second);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The contract the live instruments depend on
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     The two AB#4919 instruments are live on test-2 and prod. Publishing on the same meter is
    ///     what makes these gauges exportable without touching the observability wiring, and
    ///     renaming or re-scoping the meter would silently switch both families off.
    /// </summary>
    [Test]
    public async Task Meter_IsTheOneTheObservabilityBuilderRegisters()
    {
        await Assert.That(WorkloadStateMetrics.MeterName).IsEqualTo("Meshmakers.Octo.Communication");
        await Assert.That(WorkloadStateMetrics.MeterName).IsEqualTo(WorkloadLifecycleMetrics.MeterName);
    }

    /// <summary>
    ///     The severity values ARE the alerting contract — the check rules are thresholds on them.
    ///     Renumbering would silently invert every rule, so they are pinned literally.
    /// </summary>
    [Test]
    public async Task SeverityValues_AreTheAlertingContract()
    {
        var contract = new List<int>
        {
            WorkloadStateMetrics.Healthy,
            WorkloadStateMetrics.Warning,
            WorkloadStateMetrics.Critical,
        };

        await Assert.That(contract).IsEquivalentTo(new List<int> { 0, 1, 2 });
    }
}
