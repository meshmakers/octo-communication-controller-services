using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5827 Fix B — a connection whose registration this controller accepted, whose adapter is no
/// longer in the tenant's adapter cache, and which was never asked to restart, is deaf. The
/// periodic metrics sample is how the controller finds it: after
/// <see cref="AdapterService.OrphanedRegistrationGracePeriod" /> the outcome is
/// <see cref="MetricsSampleOutcome.RegistrationLost" />, exactly once per connection, and the hub
/// then asks the adapter to register again. Every regular way out of the cache must NOT trigger it.
/// </summary>
internal class OrphanedRegistrationDetectionTests : AdapterServiceTestsBase
{
    private static readonly DateTime T0 = new(2026, 10, 7, 19, 36, 51, DateTimeKind.Utc);
    private DateTime _now = T0;
    private RtEntityId _adapterId;

    public OrphanedRegistrationDetectionTests() : base(new AdapterCache())
    {
        AdapterService.UtcNow = () => _now;
        AdapterService.OrphanedRegistrationGracePeriod = TimeSpan.FromSeconds(30);
    }

    private async Task RegisterAsync(string connectionId)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        _adapterId = rtAdapter.ToRtEntityId();
        InitAdapterConfiguration(rtAdapter, RtEntityCreator.CreateDataFlow(), [RtEntityCreator.CreatePipeline()]);
        await AdapterService.RegisterAdapterAsync(TenantId, _adapterId, connectionId);
    }

    private MetricsSampleOutcome Sample(string connectionId, TimeSpan after)
    {
        _now = T0 + after;
        return AdapterService.RecordMetricsSample(TenantId, connectionId, new AdapterMetricsSampleDto
        {
            AdapterRtEntityId = _adapterId, Timestamp = _now, CpuPercent = 1, WorkingSetBytes = 1, GcHeapBytes = 1, ThreadCount = 1
        });
    }

    /// <summary>Takes the adapter out of the current cache instance WITHOUT notifying it.</summary>
    private void OrphanTheRegistration()
    {
        AdapterCache.TryGetTenant(TenantId, out var current);
        current!.RemoveAdapter(_adapterId);
    }

    [Test]
    public async Task RegisteredAndCached_SamplesAreRecorded()
    {
        await RegisterAsync(ConnectionId);

        await Assert.That(Sample(ConnectionId, TimeSpan.Zero)).IsEqualTo(MetricsSampleOutcome.Recorded);
    }

    [Test]
    public async Task Orphaned_IsReportedOnlyAfterTheGracePeriod_AndOnlyOnce()
    {
        await RegisterAsync(ConnectionId);
        OrphanTheRegistration();

        using var _ = Assert.Multiple();
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(0))).IsEqualTo(MetricsSampleOutcome.Dropped);
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(29))).IsEqualTo(MetricsSampleOutcome.Dropped);
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(30))).IsEqualTo(MetricsSampleOutcome.RegistrationLost);
        // A second request on the same connection would start a second, overlapping restart.
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(40))).IsEqualTo(MetricsSampleOutcome.Dropped);
        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(20))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }

    [Test]
    public async Task OrphanThatComesBackWithinTheGracePeriod_IsNotReported()
    {
        await RegisterAsync(ConnectionId);
        OrphanTheRegistration();
        Sample(ConnectionId, TimeSpan.FromSeconds(10));

        // e.g. a registration that replaced its own cache entry: back before the grace ran out.
        await AdapterService.RegisterAdapterAsync(TenantId, _adapterId, ConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(20))).IsEqualTo(MetricsSampleOutcome.Recorded);
        // The clock restarted: a later orphaning gets the full grace period again.
        OrphanTheRegistration();
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(45))).IsEqualTo(MetricsSampleOutcome.Dropped);
        await Assert.That(Sample(ConnectionId, TimeSpan.FromSeconds(75))).IsEqualTo(MetricsSampleOutcome.RegistrationLost);
    }

    [Test]
    public async Task TenantPreAndPosUpdate_NotifiedAdaptersAreNeverReported()
    {
        // The regular way out of the cache: the pre-update told the adapter to restart. Its old
        // connection keeps sampling while the shutdown runs (up to two minutes) — that is no orphan.
        await RegisterAsync(ConnectionId);

        await AdapterService.PreUpdateTenantAsync(TenantId);
        await AdapterService.PosUpdateTenantAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(Sample(ConnectionId, TimeSpan.Zero)).IsEqualTo(MetricsSampleOutcome.Dropped);
        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }

    [Test]
    public async Task BetweenPreAndPosUpdate_NothingIsJudged()
    {
        await RegisterAsync(ConnectionId);
        AdapterCache.RemoveTenant(TenantId);

        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }

    [Test]
    public async Task Disconnected_IsNeverReported()
    {
        await RegisterAsync(ConnectionId);
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, _adapterId, ConnectionId);
        OrphanTheRegistration();

        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }

    [Test]
    public async Task Unregistered_IsNeverReported()
    {
        await RegisterAsync(ConnectionId);
        await AdapterService.UnregisterAsync(TenantId, _adapterId, ConnectionId);

        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }

    [Test]
    public async Task AdapterCachedUnderAnotherConnection_IsNotAnOrphan()
    {
        // A second process with the same rtId took the registration over: deliberately not
        // reported, or the two processes would kick each other into restarts forever.
        await RegisterAsync(ConnectionId);
        AdapterTenant.UpdateConnectionId(_adapterId, "connection-2");

        await Assert.That(Sample(ConnectionId, TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Recorded);
    }

    [Test]
    public async Task ConnectionThatNeverRegistered_KeepsThePreviousSilentDrop()
    {
        // Registration still in flight, or failed and retrying: not this mechanism's business.
        await RegisterAsync(ConnectionId);
        OrphanTheRegistration();

        await Assert.That(Sample("never-registered", TimeSpan.FromMinutes(5))).IsEqualTo(MetricsSampleOutcome.Dropped);
    }
}
