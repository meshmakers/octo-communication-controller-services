using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
///     AB#7056 — two controller pods on one store. Pod A is a partitioned pod that keeps running and
///     sweeping, pod B its replacement that holds the adapters' live connections. The rule: a pod writes
///     Offline only for connections it can prove are gone, never for connections it merely does not hold;
///     and the pod that holds a connection heals a wrong Offline.
/// </summary>
internal class TwoControllerPodsTests : AdapterServiceTestsBase
{
    private readonly SharedStore _store = new();
    private readonly Pod _podB;

    public TwoControllerPodsTests()
    {
        _store.Attach(CommunicationRepository, () => Clock.UtcNow);
        _podB = new Pod();
        _store.Attach(_podB.Repository, () => Clock.UtcNow);
    }

    [Test]
    public async Task PodA_Sweep_DoesNotOverwriteTheLiveOnlineOfPodB()
    {
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        // The adapter reconnected to pod B, which writes Online (after pod A started).
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, adapter.ToRtEntityId(), "connB");
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Online);

        // Pod A holds no connection, is not shutting down and runs its sweep, twice.
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);
        count += await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task WrongOffline_IsHealedByThePodThatHoldsTheConnection()
    {
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, adapter.ToRtEntityId(), "connB");
        // A late Offline of the partitioned pod (its own disconnect handler) wins by timestamp.
        _store.Write(adapter, RtCommunicationStateEnum.Offline);
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Offline);

        var healed = await _podB.Service.ReassertOnlineForLiveConnectionsAsync(TenantId);

        await Assert.That(healed).IsEqualTo(1);
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task Healing_DoesNotTouchOfflineAdaptersThatNoPodHolds()
    {
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);

        var healedByA = await AdapterService.ReassertOnlineForLiveConnectionsAsync(TenantId);
        var healedByB = await _podB.Service.ReassertOnlineForLiveConnectionsAsync(TenantId);

        await Assert.That(healedByA + healedByB).IsEqualTo(0);
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task ConnectionClosedWhileHealing_EndsOffline()
    {
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, adapter.ToRtEntityId(), "connB");
        _store.Write(adapter, RtCommunicationStateEnum.Offline);
        // The connection closes while the Online write is in flight.
        _podB.Repository
            .When(r => r.SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(),
                RtCommunicationStateEnum.Online))
            .Do(_ => _podB.Tracker.TrackDisconnected(TenantId, adapter.ToRtEntityId(), "connB"));

        await _podB.Service.ReassertOnlineForLiveConnectionsAsync(TenantId);

        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task AdapterNoPodHolds_WithOnlineFromBeforeThisProcess_IsStillReconciledToOffline()
    {
        // AB#4699: a stale Online written by a previous process generation, nobody reconnected.
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Online, Clock.UtcNow - TimeSpan.FromMinutes(30));

        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task BackgroundTick_HealsEvenInsideTheStartupGrace()
    {
        // The heal needs no grace: holding the connection is the proof. The service is wired to pod B.
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, adapter.ToRtEntityId(), "connB");
        _store.Write(adapter, RtCommunicationStateEnum.Offline);
        var cache = Substitute.For<Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters.IAdapterCache>();
        cache.GetEnabledTenantIds().Returns(new[] { TenantId });
        using var sweep = new AdapterOfflineReconciliationBackgroundService(cache, _podB.Service, _podB.Tracker,
            Clock, Microsoft.Extensions.Options.Options.Create(
                new Meshmakers.Octo.Backend.CommunicationControllerServices.Options.CommunicationControllerOptions()));

        await sweep.SweepIfSettledAsync(); // no time has passed: the Offline sweep waits, the heal runs

        await Assert.That(_store.StateOf(adapter)).IsEqualTo(RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task Heal_RestoresTheConfigurationStateThisPodSawTheAdapterReport()
    {
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        var id = adapter.ToRtEntityId();
        _podB.Tenant.AddAdapter(id, "connB", new AdapterConfigurationDto(id, null, []));
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, id, "connB");
        await _podB.Service.UpdateConfigurationStateAsync(TenantId, id,
            new DeploymentResult { IsSuccess = true, ErrorMessages = null });
        // The partitioned pod's late Offline also reset ConfigurationState to Unconfigured.
        _store.Write(adapter, RtCommunicationStateEnum.Offline);
        _podB.Repository.ClearReceivedCalls();
        _podB.Callbacks.ClearReceivedCalls();

        await _podB.Service.ReassertOnlineForLiveConnectionsAsync(TenantId);

        await _podB.Repository.Received(1)
            .SetAdapterConfigurationStateAsync(TenantId, id, RtConfigurationStateEnum.Configured, null);
        await _podB.Callbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(default!, default!);
    }

    [Test]
    public async Task Heal_WithoutAKnownReport_AsksTheAdapterOverItsLiveConnectionToApplyItsConfigurationAgain()
    {
        // The configuration was acked on the pod that is gone: this pod has no reported state to restore.
        var adapter = _store.AddAdapter(RtCommunicationStateEnum.Offline);
        var id = adapter.ToRtEntityId();
        var pipeline = RtEntityCreator.CreatePipeline();
        var configuration = new AdapterConfigurationDto(id, null,
        [
            new PipelineConfigurationDto(OctoObjectId.GenerateNewId(), pipeline.ToRtEntityId(), false,
                pipeline.PipelineDefinition, [])
        ]);
        _podB.Tenant.AddAdapter(id, "connB", configuration);
        await _podB.Service.SetAdapterCommunicationStateOnlineAsync(TenantId, id, "connB");
        _store.Write(adapter, RtCommunicationStateEnum.Offline);
        _podB.Repository.ClearReceivedCalls();
        _podB.Callbacks.ClearReceivedCalls();

        await _podB.Service.ReassertOnlineForLiveConnectionsAsync(TenantId);

        await _podB.Callbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId, configuration);
        // The configuration state itself is written by the adapter's ack (UpdateConfigurationStateAsync).
    }

    /// <summary>A second controller pod: its own tracker, cache and service over the shared store.</summary>
    private sealed class Pod : AdapterServiceTestsBase
    {
        public AdapterService Service => AdapterService;
        public ICommunicationRepository Repository => CommunicationRepository;
        public AdapterConnectionTracker Tracker => AdapterConnectionTracker;
        public Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters.AdapterTenant Tenant => AdapterTenant;
        public IAdapterHubCallbacks Callbacks => AdapterHubCallbacks;
        public string Connection => ConnectionId;
    }

    /// <summary>One persisted adapter collection with newer-wins state writes, shared by both pods.</summary>
    private sealed class SharedStore
    {
        private readonly List<RtAdapter> _adapters = [];
        private DateTime _clock;

        public void Attach(ICommunicationRepository repository, Func<DateTime> start)
        {
            if (_clock == default)
            {
                // Every write is a second after the previous one and after pod A started.
                _clock = start();
            }

            repository.GetAdaptersAsync(Arg.Any<string>()).Returns(_ => Task.FromResult<IReadOnlyCollection<RtAdapter>>(_adapters.ToArray()));
            repository.SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>()).Returns(ci =>
            {
                var id = ci.ArgAt<RtEntityId>(1);
                Write(_adapters.Single(a => a.RtId == id.RtId), ci.ArgAt<RtCommunicationStateEnum>(2));
                return Task.CompletedTask;
            });
        }

        public RtAdapter AddAdapter(RtCommunicationStateEnum state, DateTime? timestamp = null)
        {
            var adapter = RtEntityCreator.CreateAdapter();
            adapter.CommunicationState = state;
            adapter.CommunicationStateTimestamp = timestamp;
            _adapters.Add(adapter);
            return adapter;
        }

        public void Write(RtAdapter adapter, RtCommunicationStateEnum state)
        {
            _clock += TimeSpan.FromSeconds(1);
            adapter.CommunicationState = state;
            adapter.CommunicationStateTimestamp = _clock;
        }

        public RtCommunicationStateEnum StateOf(RtAdapter adapter) => adapter.CommunicationState;
    }
}
