using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5827 Fix A — a registration racing a tenant pre/post-update must never leave the adapter in
/// an orphaned <see cref="AdapterTenant" /> instance. Reproduced on test-2-dev: two ClearCache 8.6 s
/// apart; the second pre/post pair landed while the registration was reading the adapter
/// configuration, the registration then wrote into the instance the pair had just replaced, the
/// adapter was told "registered" and was deaf from then on (metrics endpoint 404, every push lost).
/// <para>
/// Deterministic: the repository read is parked on a <see cref="TaskCompletionSource{T}" />, the
/// tenant update runs while it is parked, and only then is the read released. No timing involved.
/// </para>
/// </summary>
internal class RegistrationTenantUpdateRaceTests() : AdapterServiceTestsBase(new AdapterCache())
{
    private const string SecondConnectionId = "connection-2";

    private (RtAdapter Adapter, RtEntityId AdapterId) ArrangeAdapterWithOnePipeline()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        InitAdapterConfiguration(rtAdapter, RtEntityCreator.CreateDataFlow(), [RtEntityCreator.CreatePipeline()]);
        return (rtAdapter, rtAdapter.ToRtEntityId());
    }

    /// <summary>
    /// Parks every call of <c>GetAdapterAsync</c> until <paramref name="release" /> completes and
    /// signals <paramref name="readStarted" /> when the first one arrives. Later calls (the re-read)
    /// return at once.
    /// </summary>
    private void ParkFirstConfigurationRead(RtAdapter rtAdapter, TaskCompletionSource readStarted,
        TaskCompletionSource release, Func<int>? onCall = null)
    {
        var calls = 0;
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                onCall?.Invoke();
                if (call == 1)
                {
                    readStarted.TrySetResult();
                    await release.Task;
                }

                return rtAdapter;
            });
    }

    private bool IsCurrentlyCachedOn(RtEntityId adapterId, string connectionId) =>
        AdapterCache.TryGetTenant(TenantId, out var current)
        && current.AdapterById.TryGetValue(adapterId, out var adapter)
        && adapter.ConnectionId == connectionId;

    [Test]
    public async Task PreAndPosUpdateDuringTheConfigurationRead_RegistrationLandsInTheCurrentCacheInstance()
    {
        var (rtAdapter, adapterId) = ArrangeAdapterWithOnePipeline();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        ParkFirstConfigurationRead(rtAdapter, readStarted, release, () => Interlocked.Increment(ref reads));

        var registration = AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId);
        await readStarted.Task;

        // The second ClearCache of the repro: a complete pre/post pair while the read is parked.
        await AdapterService.PreUpdateTenantAsync(TenantId);
        await AdapterService.PosUpdateTenantAsync(TenantId);
        AdapterCache.TryGetTenant(TenantId, out var instanceAfterUpdate);

        release.SetResult();
        var configuration = await registration;

        using var _ = Assert.Multiple();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);
        // The bug: the adapter landed in the instance the update had replaced, not in this one.
        await Assert.That(IsCurrentlyCachedOn(adapterId, ConnectionId)).IsTrue();
        await Assert.That(ReferenceEquals(instanceAfterUpdate, AdapterTenant)).IsFalse();
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(adapterId)).IsFalse();
        // The configuration was read again for the new instance (the update may have changed it).
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    public async Task OrphanedRegistration_WouldHaveBeenDeaf_NowItsMetricsAreReadable()
    {
        // The observable symptom of F2 was the metrics endpoint answering 404 for a connected,
        // "registered" adapter. Same interleaving, then the read the endpoint does.
        var (rtAdapter, adapterId) = ArrangeAdapterWithOnePipeline();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ParkFirstConfigurationRead(rtAdapter, readStarted, release);

        var registration = AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId);
        await readStarted.Task;
        await AdapterService.PreUpdateTenantAsync(TenantId);
        await AdapterService.PosUpdateTenantAsync(TenantId);
        release.SetResult();
        await registration;

        var outcome = AdapterService.RecordMetricsSample(TenantId, ConnectionId, new AdapterMetricsSampleDto
        {
            AdapterRtEntityId = adapterId, Timestamp = DateTime.UtcNow, CpuPercent = 1, WorkingSetBytes = 1, GcHeapBytes = 1, ThreadCount = 1
        });

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(MetricsSampleOutcome.Recorded);
        await Assert.That(AdapterService.GetMetricsSamples(TenantId, adapterId, since: null)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task PreUpdateWithoutPosUpdateDuringTheRead_RegistrationFailsAndCachesNothing()
    {
        // Pre has flushed the tenant and Pos has not run yet: nothing current to write into. The
        // registration must fail (the adapter retries) instead of writing into the flushed instance.
        var (rtAdapter, adapterId) = ArrangeAdapterWithOnePipeline();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ParkFirstConfigurationRead(rtAdapter, readStarted, release);

        var registration = AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId);
        await readStarted.Task;
        await AdapterService.PreUpdateTenantAsync(TenantId);
        release.SetResult();

        using var _ = Assert.Multiple();
        await Assert.That(async () => await registration)
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(adapterId)).IsFalse();
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(default!, default!);
    }

    [Test]
    public async Task TenantUpdateDuringEveryAttempt_RegistrationGivesUpWithANamedReason()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var adapterId = rtAdapter.ToRtEntityId();
        InitAdapterConfiguration(rtAdapter, RtEntityCreator.CreateDataFlow(), [RtEntityCreator.CreatePipeline()]);
        var reads = 0;
        // Each read is overtaken by a full tenant update before it returns.
        CommunicationRepository.GetAdapterAsync(TenantId, adapterId)
            .Returns(async _ =>
            {
                Interlocked.Increment(ref reads);
                await AdapterService.PreUpdateTenantAsync(TenantId);
                await AdapterService.PosUpdateTenantAsync(TenantId);
                return rtAdapter;
            });

        using var _ = Assert.Multiple();
        await Assert.That(async () => await AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("gave up after 3 attempts");
        await Assert.That(reads).IsEqualTo(AdapterService.RegistrationCacheRaceMaxAttempts);
        await Assert.That(IsCurrentlyCachedOn(adapterId, ConnectionId)).IsFalse();
    }

    [Test]
    public async Task TenantUpdateRightAfterTheCommit_TheAdapterIsNotifiedAndTheRegistrationFails()
    {
        // The commit has happened, so the pre-update finds the adapter cached and tells it to
        // restart; reporting success afterwards would still let the adapter mark itself
        // registered on a connection nothing reads. The reconcile push is where it surfaces.
        var (_, adapterId) = ArrangeAdapterWithOnePipeline();
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>())
            .Returns(async _ => await AdapterService.PreUpdateTenantAsync(TenantId));

        using var _ = Assert.Multiple();
        await Assert.That(async () => await AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("was lost right after it completed");
        await AdapterHubCallbacks.Received(1).PreUpdateTenantAsync(TenantId);
    }

    [Test]
    public async Task AdapterCachedUnderAnotherConnectionAfterTheCommit_RegistrationStillSucceeds()
    {
        // A second process with the same rtId re-registering in between (the dual-adapter trap) is
        // not a lost registration: failing here would only make the two steal it from each other.
        var (_, adapterId) = ArrangeAdapterWithOnePipeline();
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>())
            .Returns(_ =>
            {
                AdapterTenant.UpdateConnectionId(adapterId, SecondConnectionId);
                return Task.CompletedTask;
            });

        var configuration = await AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId);

        await Assert.That(configuration).IsNotNull();
    }

    [Test]
    public async Task RegistrationWithoutAnyTenantUpdate_IsUnchanged()
    {
        var (_, adapterId) = ArrangeAdapterWithOnePipeline();

        await AdapterService.RegisterAdapterAsync(TenantId, adapterId, ConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(IsCurrentlyCachedOn(adapterId, ConnectionId)).IsTrue();
        await Assert.That(ReferenceEquals(AdapterTenant, GetCurrentTenant())).IsTrue();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Is<AdapterConfigurationDto>(c => c.AdapterRtEntityId == adapterId));
    }

    private AdapterTenant? GetCurrentTenant() =>
        AdapterCache.TryGetTenant(TenantId, out var current) ? current : null;
}
