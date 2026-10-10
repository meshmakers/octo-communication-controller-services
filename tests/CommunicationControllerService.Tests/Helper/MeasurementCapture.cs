using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     One measurement as a <see cref="MeasurementCapture" /> saw it: the instrument name, the value
///     widened to <see cref="double" />, and the tags rendered as strings.
/// </summary>
internal sealed record RecordedMeasurement(string Instrument, double Value, Dictionary<string, string> Tags);

/// <summary>
///     AB#6332 — the one way a test in this project listens to a process-wide meter.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The listener callback runs on whichever thread records the measurement.</b> The
///         instruments are static, so that includes every other test running concurrently — tests
///         that never open a listener themselves and are therefore not held back by
///         <c>[NotInParallel(nameof(MeterListener))]</c>. A plain <see cref="List{T}" /> filled from
///         that callback loses measurements (two <c>Add</c>s writing the same slot) and leaves
///         <c>null</c> slots behind a concurrent resize. Both were seen in CI: an
///         a <c>NullReferenceException</c> while filtering in
///         <c>SchedulerMetricsTests.AnIdleShrink_CountsTheDrainAgainstThePoolAsPoolIdle</c>, and a
///         counter that was "never recorded" in <c>PipelineExecutionMetricsTests</c>.
///     </para>
///     <para>
///         This capture therefore (1) applies the test's own filter <i>inside</i> the callback, so a
///         foreign measurement is dropped on the spot and never shares a collection with the test's
///         own, (2) keeps what passes in a <see cref="ConcurrentQueue{T}" />, and (3) disposes the
///         listener before it hands back a snapshot, so the list the test asserts on is never written
///         to again. The filter runs on foreign threads too: keep it a pure function of the
///         measurement.
///     </para>
/// </remarks>
internal sealed class MeasurementCapture : IDisposable
{
    private readonly ConcurrentQueue<RecordedMeasurement> _kept = new();
    private readonly Func<RecordedMeasurement, bool> _keep;
    private readonly MeterListener _listener = new();

    /// <summary>
    ///     Starts listening to every instrument of <paramref name="meterName" />.
    /// </summary>
    /// <param name="meterName">The meter whose instruments are captured.</param>
    /// <param name="keep">
    ///     Which measurements belong to the calling test — typically a tag carrying a tenant id or
    ///     pool rtId unique to it. Called concurrently from other tests' threads.
    /// </param>
    /// <param name="instrumentsOwner">
    ///     The class that holds the instruments as static fields, if any. Its class constructor is run
    ///     before the listener starts: if the instruments were created inside the act instead, their
    ///     publication would race this listener's own subscription.
    /// </param>
    public MeasurementCapture(string meterName, Func<RecordedMeasurement, bool> keep, Type? instrumentsOwner = null)
    {
        _keep = keep;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        // One callback per numeric type: counters are long, histograms double, gauges int or double.
        // A missing callback is not an error, it is silence — which in a metrics test reads exactly
        // like "the instrument was never recorded".
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));

        if (instrumentsOwner != null)
        {
            RuntimeHelpers.RunClassConstructor(instrumentsOwner.TypeHandle);
        }

        _listener.Start();
    }

    /// <summary>
    ///     Captures what <paramref name="act" /> records on <paramref name="meterName" />.
    /// </summary>
    public static List<RecordedMeasurement> Collect(string meterName, Func<RecordedMeasurement, bool> keep,
        Action act, bool observeGauges = false, Type? instrumentsOwner = null)
    {
        using var capture = new MeasurementCapture(meterName, keep, instrumentsOwner);
        act();
        return capture.Stop(observeGauges);
    }

    /// <summary>
    ///     Captures what <paramref name="act" /> records on <paramref name="meterName" />.
    /// </summary>
    public static async Task<List<RecordedMeasurement>> CollectAsync(string meterName,
        Func<RecordedMeasurement, bool> keep, Func<Task> act, bool observeGauges = false,
        Type? instrumentsOwner = null)
    {
        using var capture = new MeasurementCapture(meterName, keep, instrumentsOwner);
        await act();
        return capture.Stop(observeGauges);
    }

    /// <summary>
    ///     A filter keeping the measurements whose tag <paramref name="tagName" /> equals
    ///     <paramref name="value" />.
    /// </summary>
    public static Func<RecordedMeasurement, bool> TaggedWith(string tagName, string value) =>
        m => m.Tags.GetValueOrDefault(tagName) == value;

    /// <summary>
    ///     Optionally polls the observable instruments, stops listening, and returns a snapshot of
    ///     the kept measurements in the order they were recorded.
    /// </summary>
    public List<RecordedMeasurement> Stop(bool observeGauges = false)
    {
        if (observeGauges)
        {
            _listener.RecordObservableInstruments();
        }

        _listener.Dispose();
        return [.. _kept.ToArray()];
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var measurement = new RecordedMeasurement(instrument.Name, value, ToDictionary(tags));
        if (_keep(measurement))
        {
            _kept.Enqueue(measurement);
        }
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
}
