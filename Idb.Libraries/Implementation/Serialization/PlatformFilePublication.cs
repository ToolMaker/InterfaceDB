using System.ComponentModel;
using System.Runtime.InteropServices;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>OS publication barriers; durability still depends on the filesystem and device honouring flushes.</summary>
public sealed class PlatformFilePublication : IFilePublication
{
    public static PlatformFilePublication Instance { get; } = new();

    public void Replace(string stagedPath, string destinationPath)
    {
        if (OperatingSystem.IsWindows()) MoveWindows(stagedPath, destinationPath, replace: true);
        else
        {
            File.Move(stagedPath, destinationPath, overwrite: true);
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        }
    }

    public void Delete(string path)
    {
        if (!File.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            // Publish disappearance with a write-through rename. A crash can leave only an
            // ignored tombstone, never a live payload whose deletion was acknowledged.
            var tombstone = path + ".idb-deleted-" + Guid.NewGuid().ToString("N");
            MoveWindows(path, tombstone, replace: false);
            File.Delete(tombstone);
        }
        else
        {
            File.Delete(path);
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    public void FlushDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows()) return; // Publication uses MOVEFILE_WRITE_THROUGH.
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()))
            throw new PlatformNotSupportedException("Durable directory publication is unsupported on this platform.");
        var descriptor = Open(directoryPath, 0);
        if (descriptor < 0) throw Error("open directory for durability");
        try { if (Fsync(descriptor) != 0) throw Error("flush directory"); }
        finally { _ = Close(descriptor); }
    }

    private static void MoveWindows(string source, string destination, bool replace)
    {
        if (!MoveFileEx(Normalize(source), Normalize(destination), 8u | (replace ? 1u : 0u)))
            throw Error("publish file");
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static IOException Error(string operation) => new($"Unable to {operation} durably.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string source, string destination, uint flags);
}
