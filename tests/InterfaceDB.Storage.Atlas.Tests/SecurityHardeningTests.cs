namespace Idb.Storage.AtlasCore.Tests;

using System.Diagnostics;

[TestClass]
public sealed class SecurityHardeningTests
{
    [TestMethod]
    public async Task OversizedWalTransactionIsRejectedBeforeWritesAndStoreRemainsUsable()
    {
        using var root = new HybridPartitionTests.Temp();
        var options = HybridPartitionTests.Options() with
        {
            MaximumTransactionLogRecordBytes = 1024 * 1024
        };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.SaveAsync("first", new byte[600 * 1024]);
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => transaction.SaveAsync("second", new byte[600 * 1024]).AsTask());
        Assert.AreEqual(1, transaction.PendingMutationCount);
        await transaction.RollbackAsync();

        Assert.IsNull(await store.GetAsync("first"));
        Assert.IsNull(await store.GetAsync("second"));
        await store.SaveAsync("after", new byte[] { 42 });
        CollectionAssert.AreEqual(new byte[] { 42 }, await store.GetAsync("after"));
    }

    [TestMethod]
    public async Task TransactionMutationCountIsBoundedWhileStaging()
    {
        using var root = new HybridPartitionTests.Temp();
        var options = HybridPartitionTests.Options() with { MaximumTransactionMutations = 2 };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.SaveAsync("one", new byte[] { 1 });
        await transaction.SaveAsync("two", new byte[] { 2 });

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => transaction.SaveAsync("three", new byte[] { 3 }).AsTask());
        Assert.AreEqual(2, transaction.PendingMutationCount);
        await transaction.RollbackAsync();

        await store.SaveAsync("after", new byte[] { 42 });
        CollectionAssert.AreEqual(new byte[] { 42 }, await store.GetAsync("after"));
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("nested/path")]
    [DataRow("nested\\path")]
    public void RepositoryPathSegmentsRejectTraversal(string value)
    {
        var repositoryOptions = new AtlasCoreRepositoryOptions { DirectoryName = value };
        _ = Assert.ThrowsExactly<ArgumentException>(repositoryOptions.Validate);
        _ = Assert.ThrowsExactly<ArgumentException>(
            () => AtlasCoreEnvelopeRepository<IAtlasProviderRecord>.ValidatePathSegment(value, "activeVersion"));
    }

    [TestMethod]
    public async Task RestoreRejectsUnknownManifestlessContent()
    {
        using var root = new HybridPartitionTests.Temp();
        var storePath = Path.Combine(root.Path, "store");
        var backup = Path.Combine(root.Path, "backup");
        var restore = Path.Combine(root.Path, "restore");
        await using (var store = await AtlasCoreStore.OpenAsync(storePath, HybridPartitionTests.Options()))
        {
            await store.SaveAsync("one", new byte[] { 1 });
            await store.BackupAsync(backup);
        }
        await File.WriteAllTextAsync(Path.Combine(backup, "unknown-extra.txt"), "must not publish");

        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => AtlasCoreStore.RestoreAsync(backup, restore, HybridPartitionTests.Options()).AsTask());
        Assert.IsFalse(Directory.Exists(restore));
    }

    [TestMethod]
    public async Task RestoreAdmissionRejectsFileCountBeforePublication()
    {
        using var root = new HybridPartitionTests.Temp();
        var storePath = Path.Combine(root.Path, "store");
        var backup = Path.Combine(root.Path, "backup");
        var restore = Path.Combine(root.Path, "restore");
        await using (var store = await AtlasCoreStore.OpenAsync(storePath, HybridPartitionTests.Options()))
        {
            await store.SaveAsync("one", new byte[] { 1 });
            await store.BackupAsync(backup);
        }

        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => AtlasCoreStore.RestoreAsync(
            backup,
            restore,
            HybridPartitionTests.Options(),
            new AtlasCoreRestoreOptions { MaximumFiles = 1 },
            CancellationToken.None).AsTask());
        Assert.IsFalse(Directory.Exists(restore));
    }

    [TestMethod]
    public async Task RestoreAdmissionRejectsInsufficientFreeSpaceBeforePublication()
    {
        using var root = new HybridPartitionTests.Temp();
        var storePath = Path.Combine(root.Path, "store");
        var backup = Path.Combine(root.Path, "backup");
        var restore = Path.Combine(root.Path, "restore");
        await using (var store = await AtlasCoreStore.OpenAsync(storePath, HybridPartitionTests.Options()))
        {
            await store.SaveAsync("one", new byte[] { 1 });
            await store.BackupAsync(backup);
        }

        _ = await Assert.ThrowsExactlyAsync<IOException>(() => AtlasCoreStore.RestoreAsync(
            backup,
            restore,
            HybridPartitionTests.Options(),
            new AtlasCoreRestoreOptions
            {
                RequireFreeSpaceAdmission = true,
                MinimumFreeSpaceReserveBytes = long.MaxValue
            },
            CancellationToken.None).AsTask());
        Assert.IsFalse(Directory.Exists(restore));
    }

    [TestMethod]
    public async Task RestoreRejectsDirectoryLinks()
    {
        using var root = new HybridPartitionTests.Temp();
        var storePath = Path.Combine(root.Path, "store");
        var backup = Path.Combine(root.Path, "backup");
        var restore = Path.Combine(root.Path, "restore");
        var outside = Path.Combine(root.Path, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "catalog-leak.idbsidx"), "secret");
        await using (var store = await AtlasCoreStore.OpenAsync(storePath, HybridPartitionTests.Options()))
        {
            await store.SaveAsync("one", new byte[] { 1 });
            await store.BackupAsync(backup);
        }

        var link = Path.Combine(backup, "search-indexes");
        if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows() || !TryCreateDirectoryJunction(link, outside))
            {
                Assert.Inconclusive($"Filesystem links are unavailable: {exception.Message}");
            }
        }

        try
        {
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => AtlasCoreStore.RestoreAsync(backup, restore, HybridPartitionTests.Options()).AsTask());
            Assert.IsFalse(Directory.Exists(restore));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task OversizedNewestCheckpointIsRejectedBeforeRead()
    {
        using var root = new HybridPartitionTests.Temp();
        var storePath = Path.Combine(root.Path, "store");
        var options = HybridPartitionTests.Options() with { MaximumRecoveryMetadataBytes = 1024 * 1024 };
        await using (var store = await AtlasCoreStore.OpenAsync(storePath, options))
        {
            await store.SaveAsync("one", new byte[] { 1 });
            _ = await store.CheckpointAsync();
        }

        var newestCheckpoint = Directory
            .GetFiles(storePath, "checkpoint-*.idbagi", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Last();
        await using (var stream = new FileStream(newestCheckpoint, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(2L * 1024 * 1024);
        }

        await using var reopened = await AtlasCoreStore.OpenAsync(storePath, options);
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => reopened.GetAsync("one").AsTask());
    }

    private static bool TryCreateDirectoryJunction(string link, string target)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(link);
        process.StartInfo.ArgumentList.Add(target);
        if (!process.Start()) return false;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
