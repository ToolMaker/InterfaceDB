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

        StringAssert.Contains(standardOutput, "PASS single save cleans write artifacts");
        StringAssert.Contains(standardOutput, "PASS delete is idempotent and prevents stale object resurrection");
        StringAssert.Contains(standardOutput, "PASS initialization recovers an abandoned delete proposal");
        StringAssert.Contains(standardOutput, "PASS single cardinality rejects detached duplicates and permits loaded updates");
        StringAssert.Contains(standardOutput, "PASS single cardinality is atomic across repository instances");
        StringAssert.Contains(standardOutput, "PASS multiple cardinality creates distinct physical objects");
        StringAssert.Contains(standardOutput, "PASS repository query cardinality methods enforce result bounds");
        StringAssert.Contains(standardOutput, "PASS repository operations honor their public contracts");
        StringAssert.Contains(standardOutput, "PASS repository bulk save persists canonical objects in input order");
        StringAssert.Contains(standardOutput, "PASS repository registration storage policy overrides config policy");
        StringAssert.Contains(standardOutput, "PASS repository registration storage identity factory controls file identity");
        StringAssert.Contains(standardOutput, "PASS default identity factory uses guid v7 file keys");
        StringAssert.Contains(standardOutput, "PASS sequential identity factories create filename keys");
        StringAssert.Contains(standardOutput, "PASS watcher suppresses local notification echoes");
        StringAssert.Contains(standardOutput, "PASS watcher synchronizes external create update and delete");
        StringAssert.Contains(standardOutput, "PASS watcher converges after rename delete race");
        StringAssert.Contains(standardOutput, "PASS watcher rescan recovers missed storage changes");
        StringAssert.Contains(standardOutput, "PASS keyed DI isolates databases using the same contract");
        StringAssert.Contains(standardOutput, "PASS keyed DI duplicate registration is idempotent or rejected");
        StringAssert.Contains(standardOutput, "PASS two processes can write the same payload");
        StringAssert.Contains(standardOutput, "PASS cross-process write and delete use last-operation-wins ordering");
        StringAssert.Contains(standardOutput, "PASS delete lock timeout maps to storage concurrency exception");
        StringAssert.Contains(standardOutput, "PASS startup recovers migration proposal abandoned by a terminated process");
        StringAssert.Contains(standardOutput, "PASS losing proposal returns and caches canonical winner");
        StringAssert.Contains(standardOutput, "PASS initialization commits newest abandoned proposal");
        StringAssert.Contains(standardOutput, "PASS eager migration drains proposals before migrating");
        StringAssert.Contains(standardOutput, "PASS multi-step migration reaches the active version");
        StringAssert.Contains(standardOutput, "PASS strict migration rejects a missing chain link");
        StringAssert.Contains(standardOutput, "PASS migration registration rejects ambiguous source versions");
        StringAssert.Contains(standardOutput, "PASS migration registration rejects backward definitions");
        StringAssert.Contains(standardOutput, "PASS strict migration preserves legacy storage when migration code fails");
        StringAssert.Contains(standardOutput, "PASS tolerant migration preserves legacy storage when a link is missing");
        StringAssert.Contains(standardOutput, "PASS lazy migration upgrades raw storage on first query");
        StringAssert.Contains(standardOutput, "PASS encrypted storage round trips and rejects tampering");
        StringAssert.Contains(standardOutput, "PASS encrypted storage lazily rotates directly to the active key");
        StringAssert.Contains(standardOutput, "PASS forced encryption rotation rewrites the full store");
        StringAssert.Contains(standardOutput, "PASS legacy encrypted payloads use the configured historical key");
        StringAssert.Contains(standardOutput, "PASS Windows Credential Manager stores active and historical keys");
        StringAssert.Contains(standardOutput, "PASS encrypted eager migration drains and migrates proposals");
        StringAssert.Contains(standardOutput, "All integration tests passed.");
    }
}
