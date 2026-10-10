using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     AB#6332 — pins that <see cref="MeasurementCapture" /> stays correct while other threads record on
///     the same instruments, which is the situation every metrics test in this project is in: the
///     instruments are process-wide statics and the suite runs concurrently.
/// </summary>
/// <remarks>
///     The listener-based harnesses this replaced collected into a plain <see cref="List{T}" /> and
///     failed in CI with a <c>NullReferenceException</c> while filtering (a <c>null</c> slot left behind
///     a concurrent resize) and with a measurement that was simply missing (two <c>Add</c>s writing the
///     same slot). These tests drive the same shape on purpose and with a meter of their own, so they
///     neither see nor disturb the product instruments.
/// </remarks>
[NotInParallel(nameof(MeterListener))]
internal class MeasurementCaptureTests
{
    private const int Threads = 8;
    private const int MeasurementsPerThread = 5_000;

    [Test]
    public async Task ConcurrentRecordingFromManyThreads_KeepsEveryOwnMeasurementAndNoForeignOne()
    {
        // Arrange
        using var meter = new Meter($"test.measurement-capture.{Guid.NewGuid():N}");
        var counter = meter.CreateCounter<long>("test.count");
        var own = Guid.NewGuid().ToString("N");
        var foreign = Guid.NewGuid().ToString("N");

        // Act — half of the threads record this test's scope, half a foreign one, all at once.
        var recorded = MeasurementCapture.Collect(meter.Name, MeasurementCapture.TaggedWith("scope", own), () =>
        {
            using var start = new Barrier(Threads * 2);
            var workers = Enumerable.Range(0, Threads * 2)
                .Select(i => new Thread(() =>
                {
                    var tag = new KeyValuePair<string, object?>("scope", i % 2 == 0 ? own : foreign);
                    start.SignalAndWait();
                    for (var n = 0; n < MeasurementsPerThread; n++)
                    {
                        counter.Add(1, tag);
                    }
                }))
                .ToList();
            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
        });

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Count).IsEqualTo(Threads * MeasurementsPerThread);
        await Assert.That(recorded.Any(r => r is null)).IsFalse();
        await Assert.That(recorded.All(r => r.Tags["scope"] == own)).IsTrue();
        await Assert.That(recorded.All(r => r.Instrument == "test.count" && r.Value == 1)).IsTrue();
    }

    [Test]
    public async Task TheReturnedSnapshot_IsNotWrittenToByMeasurementsAfterTheCapture()
    {
        // Arrange
        using var meter = new Meter($"test.measurement-capture.{Guid.NewGuid():N}");
        var counter = meter.CreateCounter<long>("test.count");
        var tag = new KeyValuePair<string, object?>("scope", "mine");

        // Act
        var recorded = MeasurementCapture.Collect(meter.Name, _ => true, () => counter.Add(1, tag));
        counter.Add(1, tag);

        // Assert — the listener is gone, so the late measurement reaches nobody.
        await Assert.That(recorded.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ObservableGauges_AreOnlyPolledWhenAsked()
    {
        // Arrange
        using var meter = new Meter($"test.measurement-capture.{Guid.NewGuid():N}");
        meter.CreateObservableGauge("test.gauge", () => new Measurement<int>(7));

        // Act
        var withoutPoll = MeasurementCapture.Collect(meter.Name, _ => true, () => { });
        var withPoll = MeasurementCapture.Collect(meter.Name, _ => true, () => { }, observeGauges: true);

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(withoutPoll).IsEmpty();
        await Assert.That(withPoll.Single().Value).IsEqualTo(7);
    }
}
