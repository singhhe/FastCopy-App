using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static FastCopy.Core.NativeMethods;

namespace FastCopy.Core;

/// <summary>
/// Copies files at native OS speed via CopyFileEx (the same primitive Explorer and robocopy use),
/// layering progress reporting, pause/resume, cancellation, retry and optional verification on top.
/// Pause/resume works by blocking inside CopyFileEx's own progress callback, which the OS invokes on
/// the copy thread itself, so the underlying transfer genuinely stalls rather than us faking a pause.
/// </summary>
public sealed class CopyEngine
{
    private readonly object _pauseGate = new();
    private volatile bool _isPaused;

    public bool IsPaused => _isPaused;

    public void Pause() => _isPaused = true;

    public void Resume()
    {
        lock (_pauseGate)
        {
            _isPaused = false;
            Monitor.PulseAll(_pauseGate);
        }
    }

    public Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        CopyOptions options,
        IProgress<CopyProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
        => CopyAsync(new[] { sourcePath }, destinationDirectory, options, progress, cancellationToken);

    public async Task<CopyResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CopyOptions options,
        IProgress<CopyProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0) throw new ArgumentException("No source paths supplied.", nameof(sourcePaths));

        // Duplicate entries (the same path listed twice) would otherwise get planned twice. Under
        // Overwrite/Skip that's just wasted I/O onto the same destination, but under KeepBoth it plans
        // two distinct destinations for one source file and double-counts its size in the free-space
        // check. Normalize and dedupe up front so every source is planned exactly once.
        sourcePaths = sourcePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var stopwatch = Stopwatch.StartNew();
        var speed = new SpeedMeter();
        var result = new CopyResult();
        var state = new ProgressState();

        // Enumerating a large tree can take many seconds. Doing it on the caller's thread would freeze
        // a UI solid, so the scan runs on the thread pool and reports files-found as it goes.
        var plan = await Task.Run(
            () => BuildPlan(sourcePaths, destinationDirectory, options, result, found =>
            {
                state.TotalFiles = found;
                Report(progress, CopyPhase.Scanning, string.Empty, state, speed, stopwatch);
            }, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        state.TotalFiles = plan.Count;
        state.TotalBytes = plan.Sum(f => f.Size);

        if (options.CheckFreeSpace)
            EnsureFreeSpace(plan);

        foreach (var dir in plan.Select(f => Path.GetDirectoryName(f.Destination)!).Distinct())
            Directory.CreateDirectory(dir);

        int parallelism = options.AutoTuneParallelism
            ? StorageInfo.RecommendParallelism(sourcePaths[0], destinationDirectory, options.MaxParallelFiles)
            : options.MaxParallelFiles;
        parallelism = Math.Max(1, parallelism);
        result.ParallelismUsed = parallelism;

        var reportGate = new object();
        var lastReport = Stopwatch.StartNew();

        try
        {
            await Parallel.ForEachAsync(
                plan,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (file, token) =>
                {
                    await CopyOneAsync(file, options, result, state, speed, stopwatch, progress,
                        reportGate, lastReport, parallelism, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Canceled = true;
        }

        result.BytesCopied = Interlocked.Read(ref state.BytesCopied);
        result.Elapsed = stopwatch.Elapsed;
        result.FilesVerifiedOk = state.VerifiedOk;
        result.FilesVerifiedFailed = state.VerifiedFailed;

        Report(progress,
            result.Canceled ? CopyPhase.Canceled : result.FilesFailed > 0 ? CopyPhase.Failed : CopyPhase.Completed,
            string.Empty, state, speed, stopwatch);

        return result;
    }

    private async Task CopyOneAsync(
        FileCopyItem file,
        CopyOptions options,
        CopyResult result,
        ProgressState state,
        SpeedMeter speed,
        Stopwatch stopwatch,
        IProgress<CopyProgressEventArgs>? progress,
        object reportGate,
        Stopwatch lastReport,
        int parallelism,
        CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            long attemptBytes = 0;
            long previousTransferred = 0;

            CopyProgressResult OnChunk(long total, long transferred, long streamSize, long streamTransferred,
                uint streamNumber, CopyProgressCallbackReason reason, IntPtr src, IntPtr dst, IntPtr data)
            {
                if (token.IsCancellationRequested) return CopyProgressResult.Cancel;

                lock (_pauseGate)
                {
                    while (_isPaused && !token.IsCancellationRequested)
                        Monitor.Wait(_pauseGate, 200);
                }
                if (token.IsCancellationRequested) return CopyProgressResult.Cancel;

                long delta = transferred - previousTransferred;
                previousTransferred = transferred;
                attemptBytes += delta;

                long copiedSoFar = Interlocked.Add(ref state.BytesCopied, delta);
                speed.Add(delta);

                lock (reportGate)
                {
                    if (lastReport.ElapsedMilliseconds >= 120 || transferred == total)
                    {
                        Report(progress, CopyPhase.Copying, file.Source, state, speed, stopwatch);
                        lastReport.Restart();
                    }
                }

                return CopyProgressResult.Continue;
            }

            var callback = new CopyProgressRoutine(OnChunk);

            var flags = CopyFileFlags.None;
            if (file.Size >= options.UnbufferedThresholdBytes)
                flags |= CopyFileFlags.NoBuffering;

            int cancelFlag = 0;
            bool ok = CopyFileEx(file.Source, file.Destination, callback, IntPtr.Zero, ref cancelFlag, flags);
            GC.KeepAlive(callback);

            if (ok)
            {
                if (options.VerifyAfterCopy)
                {
                    Report(progress, CopyPhase.Verifying, file.Source, state, speed, stopwatch);
                    bool match = await VerifyAsync(file.Source, file.Destination, parallelism > 1, token)
                        .ConfigureAwait(false);

                    if (match)
                    {
                        Interlocked.Increment(ref state.VerifiedOk);
                    }
                    else
                    {
                        Interlocked.Increment(ref state.VerifiedFailed);
                        AddError(result, $"{file.Source}: verification mismatch (destination differs from source)");
                    }
                }

                Interlocked.Increment(ref state.FilesCompleted);
                lock (result) result.FilesCopied++;
                return;
            }

            int error = Marshal.GetLastWin32Error();

            // A partial attempt already counted its bytes; undo them so a retry cannot double-count.
            if (attemptBytes > 0) Interlocked.Add(ref state.BytesCopied, -attemptBytes);

            if (token.IsCancellationRequested)
            {
                result.Canceled = true;
                token.ThrowIfCancellationRequested();
            }

            if (error == ErrorRequestAborted)
            {
                // CopyFileEx aborted without our token actually being canceled - an unexpected native
                // abort, not a user cancellation. Count it as a real failure instead of silently dropping
                // the file from every counter while also mislabeling the whole run as canceled.
                AddError(result, $"{file.Source}: copy aborted unexpectedly (native ERROR_REQUEST_ABORTED)");
                lock (result) result.FilesFailed++;
                Interlocked.Increment(ref state.FilesCompleted);
                return;
            }

            if (IsTransient(error) && attempt < options.RetryCount)
            {
                lock (result) result.FilesRetried++;
                await Task.Delay(options.RetryDelay, token).ConfigureAwait(false);
                continue;
            }

            AddError(result, $"{file.Source}: {new Win32Exception(error).Message.TrimEnd()}");
            lock (result) result.FilesFailed++;
            Interlocked.Increment(ref state.FilesCompleted);
            return;
        }
    }

    /// <summary>Errors worth another attempt: another process holding the file, or a blip on a share.</summary>
    private static bool IsTransient(int error) =>
        error is ErrorSharingViolation or ErrorLockViolation or ErrorNetworkBusy or ErrorNetNameDeleted;

    private static void AddError(CopyResult result, string message)
    {
        lock (result.Errors) result.Errors.Add(message);
    }

    private static void Report(
        IProgress<CopyProgressEventArgs>? progress,
        CopyPhase phase,
        string currentFile,
        ProgressState state,
        SpeedMeter speed,
        Stopwatch stopwatch)
    {
        if (progress is null) return;

        progress.Report(new CopyProgressEventArgs
        {
            Phase = phase,
            CurrentFile = currentFile,
            TotalBytesCopied = Interlocked.Read(ref state.BytesCopied),
            TotalBytes = state.TotalBytes,
            FilesCompleted = Volatile.Read(ref state.FilesCompleted),
            TotalFiles = state.TotalFiles,
            BytesPerSecond = phase == CopyPhase.Copying ? speed.BytesPerSecond : 0,
            Elapsed = stopwatch.Elapsed,
        });
    }

    private static async Task<bool> VerifyAsync(string source, string destination, bool concurrent, CancellationToken token)
    {
        if (concurrent)
        {
            // On solid state, hashing both sides at once roughly halves verification wall time.
            var sourceHash = ComputeHashAsync(source, token);
            var destHash = ComputeHashAsync(destination, token);
            await Task.WhenAll(sourceHash, destHash).ConfigureAwait(false);
            return sourceHash.Result.AsSpan().SequenceEqual(destHash.Result);
        }

        // On spinning media, interleaved reads of two files just make the head seek; keep it sequential.
        byte[] a = await ComputeHashAsync(source, token).ConfigureAwait(false);
        byte[] b = await ComputeHashAsync(destination, token).ConfigureAwait(false);
        return a.AsSpan().SequenceEqual(b);
    }

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
    }

    private static void EnsureFreeSpace(List<FileCopyItem> plan)
    {
        // Path.GetPathRoot is purely lexical - it gives the wrong answer when a destination directory is
        // a mount point/junction onto a different physical volume. GetDiskFreeSpaceEx resolves reparse
        // points to whatever volume actually backs the directory you hand it, so query per destination
        // directory instead of per drive-letter root. Directories that turn out to share the same
        // physical volume are then merged by matching (total, free) byte counts from that same call -
        // two distinct volumes reporting identical totals at the same instant isn't a realistic
        // collision - so the aggregate check still catches combined overflow across sibling folders on
        // one disk, the way the old per-root grouping did for the non-junction case.
        var byVolume = new Dictionary<(ulong Total, ulong Free), (long Needed, string SampleDir)>();

        foreach (var dirGroup in plan.GroupBy(f => Path.GetDirectoryName(f.Destination)!, StringComparer.OrdinalIgnoreCase))
        {
            if (!GetDiskFreeSpaceEx(dirGroup.Key, out ulong free, out ulong total, out _))
                continue; // Can't measure (UNC share, mapped device) - let the copy try anyway.

            long needed = dirGroup.Sum(f => f.Size);
            var key = (total, free);
            byVolume[key] = byVolume.TryGetValue(key, out var existing)
                ? (existing.Needed + needed, existing.SampleDir)
                : (needed, dirGroup.Key);
        }

        foreach (var ((_, free), (needed, sampleDir)) in byVolume)
        {
            if ((ulong)needed > free)
            {
                throw new IOException(
                    $"Not enough space near {sampleDir} - needs {FormatBytes(needed)}, " +
                    $"{FormatBytes(free)} free.");
            }
        }
    }

    private static List<FileCopyItem> BuildPlan(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        CopyOptions options,
        CopyResult result,
        Action<int> onScanProgress,
        CancellationToken token)
    {
        var items = new List<FileCopyItem>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lastReported = 0;

        void Add(string source, string destination)
        {
            token.ThrowIfCancellationRequested();

            var info = new FileInfo(source);
            string? resolved = ResolveConflict(destination, info, options, claimed);
            if (resolved is null)
            {
                lock (result) result.FilesSkipped++;
                return;
            }

            claimed.Add(resolved);
            items.Add(new FileCopyItem(source, resolved, info.Length));

            if (items.Count - lastReported >= 256)
            {
                lastReported = items.Count;
                onScanProgress(items.Count);
            }
        }

        foreach (string source in sourcePaths)
        {
            token.ThrowIfCancellationRequested();

            if (File.Exists(source))
            {
                Add(source, Path.Combine(destinationDirectory, Path.GetFileName(source)));
            }
            else if (Directory.Exists(source))
            {
                string trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.Combine(destinationDirectory, Path.GetFileName(trimmed));

                foreach (string file in SafeEnumerateFiles(trimmed, result))
                    Add(file, Path.Combine(root, Path.GetRelativePath(trimmed, file)));
            }
            else
            {
                AddError(result, $"{source}: not found");
                lock (result) result.FilesFailed++;
            }
        }

        onScanProgress(items.Count);
        return items;
    }

    /// <summary>
    /// Walks a tree without letting one unreadable folder abort the whole scan - a permission-denied
    /// system directory partway through should cost you that directory, not the entire copy.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, CopyResult result)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AddError(result, $"{dir}: {ex.Message}");
                continue;
            }

            foreach (string sub in subdirectories) stack.Push(sub);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AddError(result, $"{dir}: {ex.Message}");
                continue;
            }

            foreach (string file in files) yield return file;
        }
    }

    /// <summary>Returns the destination to write, or null when the file should be skipped.</summary>
    private static string? ResolveConflict(
        string destination,
        FileInfo source,
        CopyOptions options,
        HashSet<string> claimed)
    {
        bool exists = File.Exists(destination) || claimed.Contains(destination);
        if (!exists) return destination;

        switch (options.ConflictAction)
        {
            case FileConflictAction.Skip:
                return null;

            case FileConflictAction.OverwriteIfNewer:
                var existing = new FileInfo(destination);
                bool newer = source.LastWriteTimeUtc > existing.LastWriteTimeUtc;
                bool differentSize = source.Length != existing.Length;
                return newer || differentSize ? destination : null;

            case FileConflictAction.KeepBoth:
                string dir = Path.GetDirectoryName(destination)!;
                string stem = Path.GetFileNameWithoutExtension(destination);
                string ext = Path.GetExtension(destination);
                for (int i = 2; i < int.MaxValue; i++)
                {
                    string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!File.Exists(candidate) && !claimed.Contains(candidate))
                        return candidate;
                }
                return null;

            default:
                return destination;
        }
    }

    internal static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.##} {units[i]}";
    }

    /// <summary>Mutable counters shared across the parallel copy tasks.</summary>
    private sealed class ProgressState
    {
        public long BytesCopied;
        public long TotalBytes;
        public int FilesCompleted;
        public int TotalFiles;
        public int VerifiedOk;
        public int VerifiedFailed;
    }

    private sealed record FileCopyItem(string Source, string Destination, long Size);
}
