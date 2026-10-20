using System.Runtime.InteropServices;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class DirectoryPublicationBoundaryTests
{
    [TestMethod]
    public void PublicationStopsAtExistingParentAndDoesNotReflushProvisionedPaths()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            var publication = new RecordingPublication();
            IFilePublication service = publication;
            var nested = Path.Combine(root, "new", "nested");
            service.EnsureDirectory(nested);
            CollectionAssert.AreEquivalent(new[] { root, Path.Combine(root, "new"), nested },
                publication.Flushed.Distinct().ToArray());
            publication.Flushed.Clear();
            service.EnsureDirectory(nested);
            Assert.IsEmpty(publication.Flushed, "An already provisioned path needs no ancestor publication.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FailedParentFlushIsRetriedBeforeAChildIsAcknowledged()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            var publication = new RecordingPublication { FailingPath = root };
            IFilePublication service = publication;
            var created = Path.Combine(root, "new");
            Assert.ThrowsExactly<IOException>(() => service.EnsureDirectory(created));
            publication.Flushed.Clear();
            Assert.ThrowsExactly<IOException>(() => service.EnsureDirectory(Path.Combine(created, "child")));
            Assert.IsFalse(Directory.Exists(Path.Combine(created, "child")));
            publication.FailingPath = null;
            service.EnsureDirectory(Path.Combine(created, "child"));
            Assert.IsTrue(publication.Flushed.Contains(root), "The failed link must not be forgotten on retry.");
            Assert.IsFalse(publication.Flushed.Contains(Path.GetDirectoryName(root)!));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false, false)] [DataRow(false, true, false)]
    [DataRow(true, false, false)] [DataRow(true, true, false)]
    [DataRow(false, false, true)] [DataRow(false, true, true)]
    [DataRow(true, false, true)] [DataRow(true, true, true)]
    public async Task StoreSavesAndReopensBelowEnterOnlyAncestor(bool segment, bool durable, bool existingStore)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() == 0)
        {
            Assert.Inconclusive("Requires Linux running as a non-root user to enforce directory permissions.");
            return;
        }
        var root = NewRoot();
        var opaque = Path.Combine(root, "enter-only");
        var provisioned = existingStore ? Path.Combine(opaque, "store") : Path.Combine(opaque, "writable");
        Directory.CreateDirectory(provisioned);
        var storeRoot = existingStore ? provisioned : Path.Combine(provisioned, "new-store");
        File.SetUnixFileMode(opaque, UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(opaque));
            var policy = StoragePolicy.Default with
            {
                DurabilityMode = durable ? StorageDurabilityMode.Durable : StorageDurabilityMode.Performance,
                WatchStorageChanges = false, ObjectCacheMode = StorageObjectCacheMode.MetadataOnly
            };
            var config = new Config(storeRoot, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy);
            ServiceProvider Open()
            {
                var services = new ServiceCollection();
                if (segment) services.AddSegmentCoreRepository(config);
                else services.AddFileRepository(config);
                return services.BuildServiceProvider();
            }
            string savedKey;
            await using (var provider = Open())
            {
                var repository = provider.GetRequiredService<IRepository<IRecord>>();
                await repository.InitializeAsync();
                var saved = await repository.SaveAsync(new Record { Id = "saved", Value = 42 });
                savedKey = saved.Envelope!.EnvelopeCacheKey;
            }
            await using (var provider = Open())
            {
                var repository = provider.GetRequiredService<IRepository<IRecord>>();
                await repository.InitializeAsync();
                var saved = await repository.QueryFirstByStorageKeyAsync(savedKey);
                Assert.IsNotNull(saved);
                Assert.AreEqual(42, saved.Value);
                await repository.SaveAsync(new Record { Id = "after-reopen", Value = 43 });
            }
        }
        finally
        {
            File.SetUnixFileMode(opaque, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "idb-directory-boundary-" + Guid.NewGuid().ToString("N"));
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();

    private sealed class RecordingPublication : IFilePublication
    {
        public List<string> Flushed { get; } = [];
        public string? FailingPath { get; set; }
        public void FlushDirectory(string directoryPath)
        {
            if (directoryPath == FailingPath) throw new IOException("Injected parent barrier failure.");
            Flushed.Add(directoryPath);
        }
        public void Replace(string stagedPath, string destinationPath) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
    }
}
