using System.Buffers.Binary;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCheckpointCaptureTests
{
    [TestMethod]
    public async Task DeltaRejectsImpossibleCountBeforeAllocatingTheDeclaredDictionary()
    {
        using var root = new TemporaryRoot();
        var path = Path.Combine(root.Path, "invalid-count.idbdlt");
        await PrimaryIndexDeltaCheckpoint.WriteAsync(path + ".tmp", path, 2, 1, 0,
            [new(1, 64, 0, 0)], new Dictionary<string, PrimaryIndexChange>(), PlatformDurableFileSystem.Instance, default);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), 100_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), Crc32C.Compute(bytes.AsSpan(0, bytes.Length - 4)));
        File.WriteAllBytes(path, bytes);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<ObjectLogCorruptionException>(() => PrimaryIndexDeltaCheckpoint.Read(path));
        Assert.IsLessThan(1_000_000L, GC.GetAllocatedBytesForCurrentThread() - before,
            "A tiny corrupt checkpoint must not allocate its declared record count.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DetachedCapturePreservesConcurrentUpdatesAndRestoresFailedChanges(bool failPublication)
    {
        using var root = new TemporaryRoot();
        using var fileSystem = new CheckpointGate();
        var options = new SegmentCoreOptions
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            CheckpointGenerationsToKeep = 2,
            DurableFileSystem = fileSystem,
            LogOptions = new StripedObjectLogProbeOptions { StripeCount = 1 }
        };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync("seed", new byte[] { 1 });
            await store.CheckpointAsync();
            await store.SaveAsync("seed", new byte[] { 2 });
            await store.CheckpointAsync();
            await store.SaveAsync("same", new byte[] { 2 });
            await store.SaveAsync("gone", new byte[] { 2 });
            await store.SaveAsync("restore-only", new byte[] { 2 });
            fileSystem.Arm(failPublication);
            var checkpoint = Task.Run(async () => await store.CheckpointAsync());
            try
            {
                await fileSystem.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await store.SaveAsync("same", new byte[] { 3 });
                await store.DeleteAsync("gone");
                await store.SaveAsync("new", new byte[] { 3 });
            }
            finally
            {
                fileSystem.Release();
            }

            if (failPublication)
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () => await checkpoint);
                Assert.AreEqual(4, store.Statistics.PendingCheckpointChanges);
            }
            else
            {
                await checkpoint;
                Assert.AreEqual(3, store.Statistics.PendingCheckpointChanges);
            }

            await store.CheckpointAsync();
            Assert.AreEqual(0, store.Statistics.PendingCheckpointChanges);
        }

        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        CollectionAssert.AreEqual(new byte[] { 3 }, await reopened.GetAsync("same"));
        CollectionAssert.AreEqual(new byte[] { 3 }, await reopened.GetAsync("new"));
        CollectionAssert.AreEqual(new byte[] { 2 }, await reopened.GetAsync("restore-only"));
        Assert.IsNull(await reopened.GetAsync("gone"));
        Assert.AreEqual(4, reopened.Count);
    }

    [TestMethod]
    public async Task DeltaEntryEncodingMatchesOriginalFormatAndRoundTripsDeletesUnicodeAndBufferGrowth()
    {
        using var root = new TemporaryRoot();
        var final = Path.Combine(root.Path, "checkpoint-00000000000000000002.idbdlt");
        ObjectLogWritePosition[] positions = [new(1, 256, 4, 12)];
        var changes = new Dictionary<string, PrimaryIndexChange>
        {
            ["short"] = new(false, new(0, new(1, 64, 96, 1))),
            ["delete-😀"] = new(true, default),
            [new string('ü', 5000)] = new(false, new(0, new(1, 160, 96, 4)))
        };
        await PrimaryIndexDeltaCheckpoint.WriteAsync(final + ".creating", final, 2, 1, 4,
            positions, changes, PlatformDurableFileSystem.Instance, default);
        var bytes = File.ReadAllBytes(final);
        using var expected = new MemoryStream();
        using (var writer = new BinaryWriter(expected, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("IDBDEL01"u8);
            writer.Write(1);
            writer.Write(64);
            writer.Write(2L);
            writer.Write(1L);
            writer.Write(1);
            writer.Write(changes.Count);
            writer.Write(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)));
            writer.Write(4L);
            writer.Write(0L);
            writer.Write(1L);
            writer.Write(256L);
            writer.Write(4L);
            writer.Write(12u);
            writer.Write(0u);
            foreach (var pair in changes)
            {
                var key = Encoding.UTF8.GetBytes(pair.Key);
                writer.Write(key.Length);
                writer.Write(key);
                writer.Write(pair.Value.IsDelete ? -1 : pair.Value.Address.Stripe);
                var address = pair.Value.Address.Address;
                writer.Write(address.SegmentId);
                writer.Write(address.Offset);
                writer.Write(address.FrameLength);
                writer.Write(address.Lsn);
            }

            writer.Write(Crc32C.Compute(expected.GetBuffer().AsSpan(0, (int)expected.Length)));
        }

        CollectionAssert.AreEqual(expected.ToArray(), bytes);
        var loaded = PrimaryIndexDeltaCheckpoint.Read(final);
        foreach (var pair in changes) { Assert.AreEqual(pair.Value, loaded.Changes[pair.Key]); }
    }

    private sealed class CheckpointGate : IDurableFileSystem, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(true);
        private int _armed;
        private bool _fail;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DirectoryFlushStrategy => "checkpoint capture test";
        public void Arm(bool fail) { _fail = fail; _release.Reset(); Volatile.Write(ref _armed, 1); }
        public void Release() => _release.Set();
        public void FlushFile(SafeFileHandle handle) => PlatformDurableFileSystem.Instance.FlushFile(handle);
        public void FlushDirectory(string directory) => PlatformDurableFileSystem.Instance.FlushDirectory(directory);
        public void ReplaceFile(string temporary, string final) => PlatformDurableFileSystem.Instance.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final)
        {
            if (Path.GetExtension(final) is ".idbidx" or ".idbdlt" && Interlocked.Exchange(ref _armed, 0) != 0)
            {
                Entered.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Checkpoint gate timed out."); }
                if (_fail) { throw new IOException("Injected checkpoint publication failure."); }
            }

            PlatformDurableFileSystem.Instance.PublishFile(temporary, final);
        }
        public void Dispose() => _release.Dispose();
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("InterfaceDbCheckpointCapture-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
