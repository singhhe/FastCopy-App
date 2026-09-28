using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastCopy.Core;

internal static class NativeMethods
{
    internal delegate CopyProgressResult CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        CopyProgressCallbackReason callbackReason,
        IntPtr hSourceFile,
        IntPtr hDestinationFile,
        IntPtr lpData);

    internal enum CopyProgressResult : uint
    {
        Continue = 0,
        Cancel = 1,
        Stop = 2,
        Quiet = 3,
    }

    internal enum CopyProgressCallbackReason : uint
    {
        ChunkFinished = 0,
        StreamSwitch = 1,
    }

    [Flags]
    internal enum CopyFileFlags : uint
    {
        None = 0,
        FailIfExists = 0x00000001,
        Restartable = 0x00000002,
        OpenSourceForWrite = 0x00000004,
        AllowDecryptedDestination = 0x00000008,
        CopySymlink = 0x00000800,

        /// <summary>
        /// Bypasses the system file cache. Recommended for very large copies: caching a file that will
        /// not be read again just evicts everything else the machine cares about, and for multi-GB
        /// transfers the memory pressure costs more than the cache ever wins back.
        /// </summary>
        NoBuffering = 0x00001000,
    }

    internal const int ErrorRequestAborted = 1235;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorLockViolation = 33;
    internal const int ErrorNetworkBusy = 54;
    internal const int ErrorNetNameDeleted = 64;
    internal const int ErrorFileExists = 80;
    internal const int ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        CopyProgressRoutine lpProgressRoutine,
        IntPtr lpData,
        ref int pbCancel,
        CopyFileFlags dwCopyFlags);

    // --- Storage device queries (used to tell spinning disks from solid state) ---

    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const uint StorageDeviceSeekPenaltyProperty = 7;
    internal const uint PropertyStandardQuery = 0;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        int nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    // --- Free space (mount-point/junction aware, unlike a lexical drive-letter lookup) ---

    /// <summary>
    /// Unlike deriving a drive letter with <c>Path.GetPathRoot</c> and looking it up via
    /// <see cref="DriveInfo"/>, this resolves reparse points (mount points/junctions) to whatever
    /// physical volume actually backs the given directory.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
