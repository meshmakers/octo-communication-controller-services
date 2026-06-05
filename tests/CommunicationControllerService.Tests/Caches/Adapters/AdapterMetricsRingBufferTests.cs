using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Caches.Adapters;

internal class AdapterMetricsRingBufferTests
{
    private static AdapterMetricsSampleDto Sample(DateTime ts, double cpu = 0)
        => new()
        {
            AdapterRtEntityId = RtEntityCreator.CreateAdapter().ToRtEntityId(),
            Timestamp = ts,
            CpuPercent = cpu,
            WorkingSetBytes = 1024,
            GcHeapBytes = 512,
            ThreadCount = 8
        };

    [Test]
    public async Task EmptyBuffer_SnapshotReturnsEmpty()
    {
        var buffer = new AdapterMetricsRingBuffer(capacity: 4);

        var snapshot = buffer.Snapshot();

        await Assert.That(snapshot).IsEmpty();
    }

    [Test]
    public async Task Add_LessThanCapacity_PreservesChronologicalOrder()
    {
        var buffer = new AdapterMetricsRingBuffer(capacity: 4);
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        buffer.Add(Sample(baseTime, cpu: 10));
        buffer.Add(Sample(baseTime.AddSeconds(10), cpu: 20));
        buffer.Add(Sample(baseTime.AddSeconds(20), cpu: 30));

        var snapshot = buffer.Snapshot();

        await Assert.That(snapshot).Count().IsEqualTo(3);
        await Assert.That(snapshot[0].CpuPercent).IsEqualTo(10);
        await Assert.That(snapshot[1].CpuPercent).IsEqualTo(20);
        await Assert.That(snapshot[2].CpuPercent).IsEqualTo(30);
    }

    [Test]
    public async Task Add_BeyondCapacity_DropsOldestKeepsNewest()
    {
        // Pins the ring-buffer contract: the rolling window stays bounded at
        // `capacity` and the eviction order is oldest-first, so the UI never
        // sees stale samples past the retention horizon.
        var buffer = new AdapterMetricsRingBuffer(capacity: 3);
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            buffer.Add(Sample(baseTime.AddSeconds(i * 10), cpu: i));
        }

        var snapshot = buffer.Snapshot();

        await Assert.That(snapshot).Count().IsEqualTo(3);
        await Assert.That(snapshot[0].CpuPercent).IsEqualTo(2);
        await Assert.That(snapshot[1].CpuPercent).IsEqualTo(3);
        await Assert.That(snapshot[2].CpuPercent).IsEqualTo(4);
    }

    [Test]
    public async Task Snapshot_WithSinceFilter_ReturnsOnlyStrictlyNewerSamples()
    {
        var buffer = new AdapterMetricsRingBuffer(capacity: 4);
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        buffer.Add(Sample(baseTime, cpu: 10));
        buffer.Add(Sample(baseTime.AddSeconds(10), cpu: 20));
        buffer.Add(Sample(baseTime.AddSeconds(20), cpu: 30));

        var snapshot = buffer.Snapshot(since: baseTime.AddSeconds(10));

        // `since` is exclusive — the 10s sample must NOT appear.
        await Assert.That(snapshot).Count().IsEqualTo(1);
        await Assert.That(snapshot[0].CpuPercent).IsEqualTo(30);
    }

    [Test]
    public async Task Clear_ResetsBuffer()
    {
        var buffer = new AdapterMetricsRingBuffer(capacity: 4);
        buffer.Add(Sample(DateTime.UtcNow));
        buffer.Add(Sample(DateTime.UtcNow));

        buffer.Clear();

        await Assert.That(buffer.Snapshot()).IsEmpty();
    }

    [Test]
    public async Task ConcurrentAddAndSnapshot_DoesNotThrow()
    {
        // The buffer is touched from the SignalR hub (writes) and the REST
        // endpoint (reads) concurrently; this pins that the lock contract
        // holds under contention.
        var buffer = new AdapterMetricsRingBuffer(capacity: 64);
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                buffer.Add(Sample(baseTime.AddMilliseconds(i++), cpu: i % 100));
            }
        });
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = buffer.Snapshot();
            }
        });

        await Task.WhenAll(writer, reader);

        var final = buffer.Snapshot();
        await Assert.That(final.Count).IsGreaterThan(0);
        await Assert.That(final.Count).IsLessThanOrEqualTo(buffer.Capacity);
    }

    [Test]
    public async Task Constructor_NonPositiveCapacity_Throws()
    {
        await Assert.That(() => new AdapterMetricsRingBuffer(capacity: 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new AdapterMetricsRingBuffer(capacity: -1))
            .Throws<ArgumentOutOfRangeException>();
    }
}
