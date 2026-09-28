namespace FastCopy.Core;

public enum CopyPhase
{
    Scanning,
    Copying,
    Verifying,
    Completed,
    Canceled,
    Failed,
}

public sealed class CopyProgressEventArgs : EventArgs
{
    public required CopyPhase Phase { get; init; }
    public required string CurrentFile { get; init; }
    public required long TotalBytesCopied { get; init; }
    public required long TotalBytes { get; init; }
    public required int FilesCompleted { get; init; }
    public required int TotalFiles { get; init; }

    /// <summary>Current throughput over a short rolling window, not a cumulative average.</summary>
    public required double BytesPerSecond { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public double PercentComplete => TotalBytes == 0 ? 0 : (double)TotalBytesCopied / TotalBytes * 100.0;

    /// <summary>Estimated time remaining, or null when there is not enough signal to guess.</summary>
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            if (BytesPerSecond <= 0 || TotalBytes <= 0) return null;
            long remaining = TotalBytes - TotalBytesCopied;
            if (remaining <= 0) return TimeSpan.Zero;

            double seconds = remaining / BytesPerSecond;
            // Guard against a stalled meter producing a nonsense multi-year estimate.
            return seconds > TimeSpan.MaxValue.TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
        }
    }
}
