using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

/// <summary>
/// Fixed-size, thread-safe ring buffer of <see cref="AdapterMetricsSampleDto"/>
/// per adapter. Used by the controller to back the UI sparklines without
/// persisting metrics to MongoDB / CrateDB.
///
/// Capacity is bounded so a long-lived adapter cannot grow the buffer beyond
/// the configured retention window — old samples are dropped on insert.
/// </summary>
internal sealed class AdapterMetricsRingBuffer
{
    private readonly object _lock = new();
    private readonly AdapterMetricsSampleDto?[] _samples;
    private int _writeIndex;
    private int _count;

    public AdapterMetricsRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _samples = new AdapterMetricsSampleDto?[capacity];
    }

    public int Capacity => _samples.Length;

    public void Add(AdapterMetricsSampleDto sample)
    {
        lock (_lock)
        {
            _samples[_writeIndex] = sample;
            _writeIndex = (_writeIndex + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the buffer in chronological (oldest-first) order.
    /// When <paramref name="since"/> is provided, only samples with a strictly
    /// later <see cref="AdapterMetricsSampleDto.Timestamp"/> are returned.
    /// </summary>
    public IReadOnlyList<AdapterMetricsSampleDto> Snapshot(DateTime? since = null)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return Array.Empty<AdapterMetricsSampleDto>();
            }

            var result = new List<AdapterMetricsSampleDto>(_count);
            // _writeIndex points to the next slot to overwrite; the oldest
            // sample sits at (_writeIndex - _count) modulo capacity.
            var startIndex = (_writeIndex - _count + _samples.Length) % _samples.Length;
            for (var i = 0; i < _count; i++)
            {
                var sample = _samples[(startIndex + i) % _samples.Length];
                if (sample == null)
                {
                    continue;
                }

                if (since.HasValue && sample.Timestamp <= since.Value)
                {
                    continue;
                }

                result.Add(sample);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_samples);
            _writeIndex = 0;
            _count = 0;
        }
    }
}
