using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class MetricsTests : AdapterServiceTestsBase
{
    private static AdapterMetricsSampleDto BuildSample(RtEntityId adapterRtEntityId, DateTime ts, double cpu)
        => new()
        {
            AdapterRtEntityId = adapterRtEntityId,
            Timestamp = ts,
            CpuPercent = cpu,
            WorkingSetBytes = 100,
            GcHeapBytes = 50,
            ThreadCount = 4
        };

    private RtEntityId RegisterAdapterInCache()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtEntityId = rtAdapter.ToRtEntityId();
        AdapterTenant.AddAdapter(rtEntityId, ConnectionId,
            new AdapterConfigurationDto(rtEntityId, adapterConfiguration: null, pipelines: []));
        return rtEntityId;
    }

    [Test]
    public async Task RecordMetricsSample_KnownAdapter_AppendsToBuffer()
    {
        var rtEntityId = RegisterAdapterInCache();
        var sample = BuildSample(rtEntityId, DateTime.UtcNow, cpu: 42);

        AdapterService.RecordMetricsSample(TenantId, sample);

        var buffered = AdapterService.GetMetricsSamples(TenantId, rtEntityId, since: null);
        await Assert.That(buffered).Count().IsEqualTo(1);
        await Assert.That(buffered[0].CpuPercent).IsEqualTo(42);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task RecordMetricsSample_UnknownTenant_SilentlyDropped()
    {
        // The SignalR caller must not see exceptions when the tenant cache
        // is missing — race during enable/disable. Pin the silent-no-op
        // contract so future refactors don't accidentally re-throw.
        AdapterCache.TryGetTenant("unknown", out Arg.Any<AdapterTenant?>()).Returns(false);
        var sample = BuildSample(RtEntityCreator.CreateAdapter().ToRtEntityId(), DateTime.UtcNow, cpu: 5);

        AdapterService.RecordMetricsSample("unknown", sample);

        // No way to observe the no-op beyond "did not throw"; assert is implicit.
        await Task.CompletedTask;
    }

    [Test]
    public async Task RecordMetricsSample_UnknownAdapter_SilentlyDropped()
    {
        // Tenant exists, but the adapter is not in the cache (mid-reconnect).
        // Same silent-drop contract.
        var unknownAdapterId = RtEntityCreator.CreateAdapter().ToRtEntityId();
        var sample = BuildSample(unknownAdapterId, DateTime.UtcNow, cpu: 5);

        AdapterService.RecordMetricsSample(TenantId, sample);

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetMetricsSamples_KnownAdapter_ReturnsBuffered()
    {
        var rtEntityId = RegisterAdapterInCache();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        AdapterService.RecordMetricsSample(TenantId, BuildSample(rtEntityId, t0, cpu: 10));
        AdapterService.RecordMetricsSample(TenantId, BuildSample(rtEntityId, t0.AddSeconds(10), cpu: 20));

        var samples = AdapterService.GetMetricsSamples(TenantId, rtEntityId, since: null);

        await Assert.That(samples).Count().IsEqualTo(2);
        await Assert.That(samples[0].CpuPercent).IsEqualTo(10);
        await Assert.That(samples[1].CpuPercent).IsEqualTo(20);
    }

    [Test]
    public async Task GetMetricsSamples_WithSince_FiltersOlderSamples()
    {
        var rtEntityId = RegisterAdapterInCache();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        AdapterService.RecordMetricsSample(TenantId, BuildSample(rtEntityId, t0, cpu: 10));
        AdapterService.RecordMetricsSample(TenantId, BuildSample(rtEntityId, t0.AddSeconds(10), cpu: 20));
        AdapterService.RecordMetricsSample(TenantId, BuildSample(rtEntityId, t0.AddSeconds(20), cpu: 30));

        var samples = AdapterService.GetMetricsSamples(TenantId, rtEntityId, since: t0.AddSeconds(10));

        await Assert.That(samples).Count().IsEqualTo(1);
        await Assert.That(samples[0].CpuPercent).IsEqualTo(30);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task GetMetricsSamples_UnknownTenant_ThrowsTenantNotEnabled()
    {
        // The REST controller relies on this exception to surface a 404 to the
        // UI. Pin so the polymorphic exception is not lost in a future cleanup.
        AdapterCache.TryGetTenant("unknown", out Arg.Any<AdapterTenant?>()).Returns(false);

        var ex = await Assert.That(() =>
                AdapterService.GetMetricsSamples("unknown", RtEntityCreator.CreateAdapter().ToRtEntityId(), since: null))
            .Throws<AdapterServiceException>();

        await Assert.That(ex!.Message).Contains("Tenant not enabled");
    }

    [Test]
    public async Task GetMetricsSamples_UnknownAdapter_ThrowsAdapterNotLoaded()
    {
        var unknownAdapterId = RtEntityCreator.CreateAdapter().ToRtEntityId();

        var ex = await Assert.That(() =>
                AdapterService.GetMetricsSamples(TenantId, unknownAdapterId, since: null))
            .Throws<AdapterServiceException>();

        await Assert.That(ex!.Message).Contains("no live SignalR connection");
    }
}
