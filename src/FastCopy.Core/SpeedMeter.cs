using System.Diagnostics;

namespace FastCopy.Core;

/// <summary>
/// Rolling-window throughput meter. A cumulative average (total bytes / total elapsed) is misleading
/// during a copy: one fast large file early on keeps the number inflated for minutes afterwards.
/// This keeps only the last <see cref="WindowSeconds"/> of samples so the reading reflects what the
/// disk is doing right now, which is what a progress UI actually needs for a believable ETA.
/// </summary>
internal sealed class SpeedMeter
{
    private const double WindowSeconds = 3.0;

    private readonly object _gate = new();
    private readonly Queue<(double Timestamp, long Bytes)> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _windowBytes;

    public void Add(long bytes)
    {
        if (bytes <= 0) return;

        lock (_gate)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _samples.Enqueue((now, bytes));
            _windowBytes += bytes;
            Trim(now);
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                double now = _clock.Elapsed.TotalSeconds;
                Trim(now);
                if (_samples.Count == 0) return 0;

                // Measure across the span the samples actually cover, so a copy that just started
                // does not divide by a full window it has not filled yet and read absurdly low.
                double span = Math.Max(now - _samples.Peek().Timestamp, 0.05);
                return _windowBytes / span;
            }
        }
    }

    private void Trim(double now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().Timestamp > WindowSeconds)
            _windowBytes -= _samples.Dequeue().Bytes;
    }
}
