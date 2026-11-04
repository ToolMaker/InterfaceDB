using System.Diagnostics;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FileLeaseProcessTests
{
    [TestMethod]
    [DataRow(false, "migration")] [DataRow(true, "migration")]
    [DataRow(false, "write")] [DataRow(true, "write")]
    public async Task RuntimeLockingSwitchCannotAllowUnprotectedFileOperations(bool disabled, string operation)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDBLeaseProcessTests"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var harness = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../InterfaceDb.IntegrationTests/bin",
                configuration, "net10.0/InterfaceDb.IntegrationTests.dll"));
            Assert.IsTrue(File.Exists(harness));
            var info = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = disabled ? "1" : "0";
            foreach (var argument in new[] { harness, "--lease-guard", operation, root }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var rejected = disabled && !OperatingSystem.IsWindows();
                Assert.AreEqual(rejected ? 3 : 0, process.ExitCode, await stdout + await stderr);
                StringAssert.Contains(await stdout, rejected ? "LEASE_REJECTED" : "LEASE_SUPPORTED");
                if (rejected) Assert.IsEmpty(Directory.GetFiles(root, "*.mpk", SearchOption.AllDirectories));
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }
}
