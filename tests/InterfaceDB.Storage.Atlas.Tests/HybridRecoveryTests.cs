using System.Diagnostics;
using System.Security.Cryptography;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class HybridRecoveryTests
{
    private static async Task Seed(AtlasCoreStore store)
    {
        for (var i = 0; i < 3; i++)
        { await store.SaveAsync($"ABCF{i:D3}", new byte[] { 1 }); await store.SaveAsync($"ABCG{i:D3}", new byte[] { 2 }); }
    }

    [TestMethod]
    [DataRow("children-durable", 1)] [DataRow("ownership-published", 3)]
    public async Task ProcessDeathAtSplitCommitCutsRecoversWithoutLosingAcknowledgedWrites(string stage, int groups)
    {
        using var root = new HybridPartitionTests.Temp(); var options = HybridPartitionTests.Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options)) { await Seed(store); }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InterfaceDB.sln"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(directory.FullName, "tests", "InterfaceDB.Storage.Atlas.CrashHost", "bin", config, "net10.0", "InterfaceDB.Storage.Atlas.CrashHost.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(host); start.ArgumentList.Add(root.Path); start.ArgumentList.Add(stage);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        StringAssert.Contains(await output, "CUT:" + stage, await errors);
        Assert.AreNotEqual(0, process.ExitCode);
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(groups, reopened.Partitions.Count);
        Assert.AreEqual(6, (await reopened.ReadAllAsync()).Count);
        Assert.AreEqual((byte)2, (await reopened.GetAsync("ABCG000"))![0]);
        if (groups == 1) Assert.IsTrue((await reopened.SplitBucketAsync("ABCF000")).Published);
        await reopened.SaveAsync("ABCF000", new byte[] { 9 }); await reopened.CheckpointAsync();
        Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
    }

    [TestMethod]
    public async Task UncertainOwnershipPublishFaultsUntilReopen()
    {
        using var root = new HybridPartitionTests.Temp(); var fs = new UncertainMapFileSystem();
        var options = HybridPartitionTests.Options() with { DurableFileSystem = fs };
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        {
            await Seed(store); fs.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => store.SplitBucketAsync("ABCF000").AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("ABCF000").AsTask());
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(3, reopened.Partitions.Count); Assert.AreEqual(6, (await reopened.ReadAllAsync()).Count);
    }

    [TestMethod]
    [DataRow("missing-map")] [DataRow("missing-child")]
    public async Task MissingCommittedOwnershipOrSplitDataIsNotTreatedAsAnEmptyStore(string damage)
    {
        using var root = new HybridPartitionTests.Temp(); var options = HybridPartitionTests.Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        { await Seed(store); Assert.IsTrue((await store.SplitBucketAsync("ABCF000")).Published); }
        if (damage == "missing-map")
        {
            foreach (var file in Directory.GetFiles(Path.Combine(root.Path, "hybrid-placement"), "map-*.idbmap")) File.Delete(file);
        }
        else
        {
            var child = Directory.GetDirectories(root.Path, "bucket-00000001", SearchOption.AllDirectories).Single();
            // Individual known test files only; leave the directory so the check cannot rely on existence alone.
            foreach (var file in Directory.GetFiles(Path.Combine(child, "segments"), "segment-*.idbag")) File.Delete(file);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    [TestMethod]
    public async Task LegacyFormatAndChangedPrefixDepthAreRejectedRatherThanReinterpreted()
    {
        using var root = new HybridPartitionTests.Temp(); var options = HybridPartitionTests.Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options)) { await Seed(store); }
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path,
            options with { Partitioning = options.Partitioning with { InitialPrefixBytes = 4 } }).AsTask());
        var path = Path.Combine(root.Path, "storage-format.idbatlas"); var bytes = await File.ReadAllBytesAsync(path);
        "IDBOLD!!"u8.CopyTo(bytes); SHA256.HashData(bytes.AsSpan(0, 24), bytes.AsSpan(24)); await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    private sealed class UncertainMapFileSystem : IDurableFileSystem
    {
        private readonly IDurableFileSystem _inner = PlatformDurableFileSystem.Instance;
        public bool Armed;
        public string DirectoryFlushStrategy => _inner.DirectoryFlushStrategy;
        public void FlushFile(SafeFileHandle handle) => _inner.FlushFile(handle);
        public void FlushDirectory(string path) => _inner.FlushDirectory(path);
        public void ReplaceFile(string temporary, string final) => _inner.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final)
        {
            _inner.PublishFile(temporary, final);
            if (Armed && final.EndsWith(".idbmap", StringComparison.Ordinal))
            { Armed = false; throw new IOException("Injected uncertain map publication."); }
        }
    }
}
