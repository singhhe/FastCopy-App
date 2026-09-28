namespace FastCopy.Core;

public enum FileConflictAction
{
    /// <summary>Replace the destination file unconditionally.</summary>
    Overwrite,

    /// <summary>Leave the destination alone and count the file as skipped.</summary>
    Skip,

    /// <summary>Replace only when the source is newer or a different size.</summary>
    OverwriteIfNewer,

    /// <summary>Copy alongside the existing file as "name (2).ext".</summary>
    KeepBoth,
}

public sealed class CopyOptions
{
    public FileConflictAction ConflictAction { get; set; } = FileConflictAction.Overwrite;

    public bool VerifyAfterCopy { get; set; }

    /// <summary>
    /// Files copied concurrently. When <see cref="AutoTuneParallelism"/> is set this is an upper bound
    /// that gets lowered for spinning disks.
    /// </summary>
    public int MaxParallelFiles { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Adapt concurrency to the detected media type (HDD vs SSD) of the source and destination.</summary>
    public bool AutoTuneParallelism { get; set; } = true;

    /// <summary>Retries for transient failures such as a file briefly locked by another process.</summary>
    public int RetryCount { get; set; } = 2;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Fail fast when the destination volume cannot hold the payload, rather than dying mid-copy.</summary>
    public bool CheckFreeSpace { get; set; } = true;

    /// <summary>
    /// Files at or above this size bypass the system file cache. Large transfers otherwise evict the
    /// entire working set of the machine for data that will never be read again.
    /// </summary>
    public long UnbufferedThresholdBytes { get; set; } = 64L * 1024 * 1024;
}
