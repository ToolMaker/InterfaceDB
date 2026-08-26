using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

public sealed class PlatformDurableFileSystem : IDurableFileSystem
{
    private const int OpenReadOnly = 0;
    private const int OpenDirectoryLinux = 0x10000;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const uint MoveFileReplaceExisting = 0x00000001;

    public static PlatformDurableFileSystem Instance { get; } = new();

    private PlatformDurableFileSystem()
    {
    }

    public string DirectoryFlushStrategy => OperatingSystem.IsLinux()
        ? "POSIX fsync(directory)"
        : OperatingSystem.IsWindows()
            ? "Windows file FlushFileBuffers + MoveFileEx(MOVEFILE_WRITE_THROUGH) publication"
            : "File flush only; directory flush unsupported by Phase 0";

    public void FlushFile(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        RandomAccess.FlushToDisk(handle);
    }

    public void FlushDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = Open(directoryPath, OpenReadOnly | OpenDirectoryLinux);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Unable to open directory '{directoryPath}' for a durability flush.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"Unable to flush directory '{directoryPath}' to durable storage.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    public void PublishFile(string temporaryPath, string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(temporaryPath, finalPath, MoveFileWriteThrough))
            {
                throw new IOException(
                    $"Unable to durably publish '{temporaryPath}' as '{finalPath}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return;
        }

        File.Move(temporaryPath, finalPath, overwrite: false);
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPath))!);
    }

    public void ReplaceFile(string temporaryPath, string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(
                    temporaryPath,
                    finalPath,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new IOException(
                    $"Unable to durably replace '{finalPath}' with '{temporaryPath}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return;
        }

        File.Move(temporaryPath, finalPath, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPath))!);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
