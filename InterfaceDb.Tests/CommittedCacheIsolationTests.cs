using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;
using static InterfaceDb.Tests.RetainedEditingTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class CommittedCacheIsolationTests
{
    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task RuntimeCacheOwnershipAndCommitOrderNeverEnterStoredPayload(bool segment)
    {
        await using var f = new EditFixture(segment);
        var saved = await f.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
        var envelope = saved.Envelope!;
        var serializer = new BinaryObjectSerializer<IRecord>();
        var before = serializer.Serialize(envelope);
        var state = (IEnvelopeCacheState<IRecord>)envelope;
        Assert.IsNotNull(state.CommittedSnapshot);
        state.CachedOwner = new Record { Id = "runtime-only", Value = 99 };
        var custom = new CustomEnvelope(envelope);
        EnvelopeCommitOrder.Stamp(custom);
        EnvelopeCommitOrder.Copy(custom, envelope);
        Assert.AreEqual(EnvelopeCommitOrder.Get(custom), EnvelopeCommitOrder.Get(envelope));
        var other = new CustomEnvelope(serializer.Deserialize(before));
        EnvelopeCommitOrder.Copy(envelope, other);
        Assert.AreEqual(EnvelopeCommitOrder.Get(envelope), EnvelopeCommitOrder.Get(other));
        CollectionAssert.AreEqual(before, serializer.Serialize(envelope));
        var restored = (IEnvelopeCacheState<IRecord>)serializer.Deserialize(before);
        Assert.IsNull(restored.CachedOwner);
        Assert.IsNull(restored.CommittedSnapshot);
        Assert.AreEqual(0L, restored.CommitOrder);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CustomEnvelopesKeepIsolatedReadsAndCollectibleWeakCache(bool segment)
    {
        using var serializer = new ProbeSerializer { UseCustomEnvelope = true };
        await using var f = new EditFixture(segment, cache: StorageObjectCacheMode.WeakObject, serializer: serializer);
        await f.Repository.InitializeAsync();
        var weak = HoldThenReleaseWeakCopy(f);
        Collect();
        Assert.IsFalse(weak.TryGetTarget(out _));
        var read = await f.Repository.SearchFirstAsync(r => r.Value == 1);
        Assert.IsNotNull(read);
        Assert.IsInstanceOfType<CustomEnvelope>(read.Envelope);
        read.Value = 99;
        Assert.IsNull(await f.Repository.SearchFirstAsync(r => r.Value == 99));
        Assert.AreEqual(1, (await f.Repository.QueryFirstAsync(r => r.Id == "one"))!.Value);
        GC.KeepAlive(read);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CachedReadsUseConfiguredSerializerWithoutEncodingAgain(bool segment)
    {
        using var serializer = new ProbeSerializer();
        await using var f = new EditFixture(segment, serializer: serializer);
        var saved = await f.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
        Assert.AreEqual(1, serializer.Serializations);
        for (var i = 0; i < 3; i++)
        {
            var read = await f.Repository.SearchFirstAsync(r => r.Value == 1);
            Assert.IsNotNull(read);
            read.Value = 99;
            Assert.IsNull(await f.Repository.SearchFirstAsync(r => r.Value == 99));
        }
        Assert.AreEqual(1, serializer.Serializations, "A cache hit materializes committed bytes; it must not encode the object again.");
        GC.KeepAlive(saved);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CacheMaterializationFailureRejectsWriteBeforeStorageChanges(bool segment)
    {
        using var serializer = new ProbeSerializer();
        await using var f = new EditFixture(segment, serializer: serializer);
        var saved = await f.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
        saved.Value = 2;
        serializer.RejectCopies = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Repository.SaveAsync(saved).AsTask());
        serializer.RejectCopies = false;
        Assert.AreEqual(1, (await f.Repository.QueryFirstAsync(r => r.Id == "one"))!.Value);
        Assert.IsNull(await f.Repository.SearchFirstAsync(r => r.Value == 2));
        await f.Repository.SaveAsync(saved);
        Assert.AreEqual(2, (await f.Repository.QueryFirstAsync(r => r.Id == "one"))!.Value);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task PendingSaveKeepsOriginalCommittedValueVisible(bool segment)
    {
        using var serializer = new ProbeSerializer();
        await using var f = new EditFixture(segment, serializer: serializer);
        var saved = await f.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
        saved.Value = 2;
        serializer.BlockNextWrite = true;
        var pending = Task.Run(async () => await f.Repository.SaveAsync(saved));
        try
        {
            await serializer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, (await f.Repository.QueryFirstAsync(r => r.Id == "one"))!.Value);
            Assert.IsNotNull(await f.Repository.SearchFirstAsync(r => r.Value == 1));
            Assert.IsNull(await f.Repository.SearchFirstAsync(r => r.Value == 2));
        }
        finally { serializer.Release.Set(); await pending; }
        Assert.AreEqual(2, (await f.Repository.SearchFirstAsync(r => r.Value == 2))!.Value);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CollectedWeakReadMaterializesCallerAndPrivateCopyWithoutEncodingAgain(bool segment)
    {
        using var serializer = new ProbeSerializer();
        await using var f = new EditFixture(segment, cache: StorageObjectCacheMode.WeakObject, serializer: serializer);
        await f.Repository.InitializeAsync();
        var weak = HoldThenReleaseWeakCopy(f);
        Collect();
        Assert.IsFalse(weak.TryGetTarget(out _), "The measured read must miss the collected private cache.");
        var before = serializer.Deserializations;
        var read = await f.Repository.SearchFirstAsync(r => r.Value == 1);
        Assert.IsNotNull(read);
        Assert.AreEqual(2, serializer.Deserializations - before,
            "A cache miss creates the caller object and its independent private cache copy.");
        read.Value = 99;
        var other = await f.Repository.SearchFirstAsync(r => r.Value == 1);
        Assert.IsNotNull(other);
        Assert.AreNotSame(read, other);
        Assert.AreEqual(1, other.Value);
        Assert.IsNull(await f.Repository.SearchFirstAsync(r => r.Value == 99));
        Assert.AreEqual(1, serializer.Serializations, "Reads must not encode the cached object again.");
        GC.KeepAlive(read);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task WeakCacheReleasesPrivateGraphWhenCallersReleaseTheirCopies(bool segment)
    {
        await using var f = new EditFixture(segment, cache: StorageObjectCacheMode.WeakObject);
        await f.Repository.InitializeAsync();
        var weak = HoldThenReleaseWeakCopy(f);
        Collect();
        Assert.IsFalse(weak.TryGetTarget(out _), "Neither snapshot metadata nor caller anchors may strongly retain a dead weak cache entry.");
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(async () =>
        {
            for (var i = 0; i < 100 && !stop.IsCancellationRequested; i++)
            {
                GC.Collect();
                await Task.Delay(1);
            }
        });
        try
        {
            for (var i = 0; i < 25; i++)
            {
                Assert.AreEqual(1, (await f.Repository.QueryFirstAsync(r => r.Id == "one"))!.Value);
                Assert.AreEqual(1, (await f.Repository.SearchFirstAsync(r => r.Value == 1))!.Value);
            }
        }
        finally { stop.Cancel(); await collector; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<IRecord> HoldThenReleaseWeakCopy(EditFixture f)
    {
        var saved = f.Repository.SaveAsync(new Record { Id = "one", Value = 1 }).AsTask().GetAwaiter().GetResult();
        var entries = f.Services.GetRequiredKeyedService<IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<IRecord>>>(f.Configuration.EnvelopeDICacheKey);
        var entry = entries[saved.Envelope!.EnvelopeCacheKey];
        Assert.IsNull(entry.ObjectInstance);
        var weak = entry.ObjectInstanceWeakReference!;
        Collect();
        Assert.IsTrue(weak.TryGetTarget(out _), "A caller copy should keep the private weak-cache object usable.");
        GC.KeepAlive(saved);
        return weak;
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class ProbeSerializer : IRevisionObjectSerializer<IRecord>, IDisposable
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        public int Serializations;
        public int Deserializations;
        public bool RejectCopies;
        public bool BlockNextWrite;
        public bool UseCustomEnvelope;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope)
        {
            Interlocked.Increment(ref Serializations);
            var payload = _inner.Serialize(envelope);
            if (BlockNextWrite)
            {
                BlockNextWrite = false; Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            }
            return payload;
        }
        public IObjectEnvelope<IRecord> Deserialize(byte[] payload)
        {
            Interlocked.Increment(ref Deserializations);
            if (RejectCopies) throw new InvalidDataException("Injected cache-copy failure.");
            var envelope = _inner.Deserialize(payload);
            return UseCustomEnvelope ? new CustomEnvelope(envelope) : envelope;
        }
        public long ReadRevision(ReadOnlyMemory<byte> payload) => _inner.ReadRevision(payload);
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => _inner.ReadRevisionAsync(source, cancellationToken);
        public async ValueTask SerializeAsync(IObjectEnvelope<IRecord> envelope, Stream destination, CancellationToken cancellationToken = default)
            => await destination.WriteAsync(Serialize(envelope), cancellationToken);
        public void Dispose() => Release.Dispose();
    }

    private sealed class CustomEnvelope(IObjectEnvelope<IRecord> inner) : IObjectEnvelope<IRecord>
    {
        public string EnvelopeCacheKey { get => inner.EnvelopeCacheKey; set => inner.EnvelopeCacheKey = value; }
        public string VersionTag { get => inner.VersionTag; set => inner.VersionTag = value; }
        public long RevisionTimestampUtcMicroseconds { get => inner.RevisionTimestampUtcMicroseconds; set => inner.RevisionTimestampUtcMicroseconds = value; }
        public IRecord? ObjectInstance { get => inner.ObjectInstance; set => inner.ObjectInstance = value; }
        public WeakReference<IRecord>? ObjectInstanceWeakReference { get => inner.ObjectInstanceWeakReference; set => inner.ObjectInstanceWeakReference = value; }
        public string? ImplementationTypeName { get => inner.ImplementationTypeName; set => inner.ImplementationTypeName = value; }
        public bool CacheObjectInstance { get => inner.CacheObjectInstance; set => inner.CacheObjectInstance = value; }
        public IDictionary<string, EncryptedFieldValue> EncryptedFields { get => inner.EncryptedFields; set => inner.EncryptedFields = value; }
        public byte[]? ExtensionMetadata { get => inner.ExtensionMetadata; set => inner.ExtensionMetadata = value; }
        public string? Location { get => inner.Location; set => inner.Location = value; }
        public StorageObjectState State { get => inner.State; set => inner.State = value; }
    }
}
