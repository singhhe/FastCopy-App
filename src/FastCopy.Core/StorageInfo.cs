using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using static FastCopy.Core.NativeMethods;

namespace FastCopy.Core;

/// <summary>
/// Detects whether a volume sits on media with a seek penalty (i.e. a spinning disk).
/// This matters a lot for throughput: issuing many concurrent copies against an HDD makes the head
/// thrash between files and is routinely *slower* than copying one file at a time, while on an SSD
/// concurrency is the main way to saturate the queue depth. So parallelism is chosen per-media.
/// </summary>
internal static class StorageInfo
{
    private static readonly ConcurrentDictionary<string, bool?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true for spinning media, false for solid state, null if it cannot be determined.</summary>
    public static bool? HasSeekPenalty(string path)
    {
        string? volume = GetVolumeKey(path);
        if (volume is null) return null;

        return Cache.GetOrAdd(volume, QuerySeekPenalty);
    }

    /// <summary>True when both paths live on the same volume (so they contend for the same spindle/queue).</summary>
    public static bool SameVolume(string a, string b)
    {
        string? va = GetVolumeKey(a), vb = GetVolumeKey(b);
        return va is not null && vb is not null && string.Equals(va, vb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks a sensible concurrent-file count for this source/destination pair.
    /// Network paths get moderate concurrency to hide latency; HDDs get serialized; SSDs get the default.
    /// </summary>
    public static int RecommendParallelism(string source, string destination, int requested)
    {
        bool? sourceSpinning = HasSeekPenalty(source);
        bool? destSpinning = HasSeekPenalty(destination);

        // Unknown media (network shares, exotic drivers) - don't get clever, keep the caller's value.
        if (sourceSpinning is null && destSpinning is null)
            return requested;

        bool eitherSpinning = sourceSpinning == true || destSpinning == true;
        if (!eitherSpinning)
            return requested;

        // One spindle serving both ends of the copy: concurrency is pure seek thrash.
        if (SameVolume(source, destination))
            return 1;

        // Separate devices, at least one spinning: a little overlap helps hide latency, more just thrashes.
        return 2;
    }

    private static string? GetVolumeKey(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;

            // UNC paths have no drive letter to query; treat the share itself as the volume identity.
            if (root.StartsWith(@"\\")) return root.TrimEnd('\\');

            return root.TrimEnd('\\');
        }
        catch
        {
            return null;
        }
    }

    private static bool? QuerySeekPenalty(string volume)
    {
        // Only a local "X:" style root can be opened as a device for this query.
        if (volume.Length != 2 || volume[1] != ':') return null;

        SafeFileHandle? handle = null;
        try
        {
            // Zero desired access opens the device for metadata queries only, which needs no admin rights.
            handle = CreateFile($@"\\.\{volume}", 0, FileShareRead | FileShareWrite, IntPtr.Zero,
                OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
                // Required even though the query itself carries no extra data: the struct is declared
                // with a fixed-size ByValArray, and marshaling a null array for that throws.
                AdditionalParameters = new byte[1],
            };

            bool ok = DeviceIoControl(handle, IoctlStorageQueryProperty, ref query,
                System.Runtime.InteropServices.Marshal.SizeOf<StoragePropertyQuery>(),
                out DeviceSeekPenaltyDescriptor descriptor,
                System.Runtime.InteropServices.Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _, IntPtr.Zero);

            return ok ? descriptor.IncursSeekPenalty : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }
}
