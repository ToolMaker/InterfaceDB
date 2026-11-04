using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Checks the runtime setting and actual exclusive-open behavior. This does not
/// qualify cross-host network filesystems or replace device durability testing.</summary>
public sealed class PlatformFileLeaseValidation : IFileLeaseValidation
{
    public static PlatformFileLeaseValidation Instance { get; } = new();

    public void RequireExclusiveHandles(string directory)
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        var disabled = AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var appSetting)
            ? appSetting : configured == "1" || bool.TryParse(configured, out var environmentSetting) && environmentSetting;
        if (!OperatingSystem.IsWindows() && disabled)
            throw new NotSupportedException("File leases require .NET file locking. DisableFileLocking must be off.");

        // Use only a uniquely owned probe, never an application file or an existing lease.
        var path = Path.Combine(Path.GetFullPath(directory), ".idb-lock-probe-" + Guid.NewGuid().ToString("N"));
        using var owner = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            using var contender = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException error) when (OperatingSystem.IsWindows()
            ? (error.HResult & 0xffff) == 32 // ERROR_SHARING_VIOLATION
            : error.HResult == (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 11 : 35)) // EWOULDBLOCK
        {
            // The file exists and is writable by this process, but its owner excludes a second handle.
            return;
        }
        throw new NotSupportedException("This storage directory does not enforce exclusive file handles; safe file lease recovery is unavailable.");
    }
}
