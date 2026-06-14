using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class LockingIntegrationTests
{
    [TestMethod]
    public async Task SidecarLocking_AllIntegrationScenariosPass()
    {
        var harnessProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "InterfaceDb.IntegrationTests",
            "InterfaceDb.IntegrationTests.csproj"));

        Assert.IsTrue(File.Exists(harnessProject), $"Integration test harness project was not found at '{harnessProject}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(harnessProject);
        startInfo.ArgumentList.Add("--no-build");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start integration test harness.");

        var standardOutput = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Integration harness failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");

        StringAssert.Contains(standardOutput, "PASS single save cleans sidecar artifacts");
        StringAssert.Contains(standardOutput, "PASS two processes can write the same payload");
        StringAssert.Contains(standardOutput, "All integration tests passed.");
    }
}
