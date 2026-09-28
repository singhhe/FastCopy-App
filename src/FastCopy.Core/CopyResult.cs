namespace FastCopy.Core;

public sealed class CopyResult
{
    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public int FilesVerifiedOk { get; set; }
    public int FilesVerifiedFailed { get; set; }
    public int FilesRetried { get; set; }
    public long BytesCopied { get; set; }
    public bool Canceled { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>Concurrency actually used, after media auto-tuning.</summary>
    public int ParallelismUsed { get; set; }

    public List<string> Errors { get; } = new();

    public double AverageBytesPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : BytesCopied / Elapsed.TotalSeconds;
}
