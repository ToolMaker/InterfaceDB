using System.Buffers;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using MessagePack.Formatters;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class BinarySerializerBorrowedMemoryTests
{
    [TestMethod]
    public void BorrowedMemoryIsDetachedBeforeCustomTypeFormatterCanRetainIt()
    {
        var serializer = new BinaryObjectSerializer<BorrowingRecord>();
        var bytes = serializer.Serialize(CreateEnvelope(new BorrowingRecord { Data = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }) }));
        var result = serializer.DeserializeMemory(bytes).ObjectInstance!;
        Array.Clear(bytes);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result.Data.ToArray());
        Assert.IsFalse(typeof(IMemoryObjectSerializer<BorrowingRecord>).IsAssignableFrom(serializer.GetType()),
            "A binary serializer cannot promise a zero-copy borrowed read for arbitrary custom formatters.");
    }

    [TestMethod]
    public void NestedCustomFormatterCannotRetainBorrowedBackingStorage()
    {
        var serializer = new BinaryObjectSerializer<HolderRecord>();
        var bytes = serializer.Serialize(CreateEnvelope(new HolderRecord
        {
            Payload = new BorrowingPayload { Data = new ReadOnlySequence<byte>(new byte[] { 4, 5, 6 }) }
        }));
        var result = serializer.DeserializeMemory(bytes).ObjectInstance!;
        Array.Fill(bytes, (byte)99);

        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, result.Payload.Data.ToArray());
    }

    [TestMethod]
    public async Task RealFileReadRetainsCustomFormatterPayloadAfterReaderReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDbBorrowedFormatter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var serializer = new BinaryObjectSerializer<BorrowingRecord>();
            var file = Path.Combine(root, "custom.mpk");
            await File.WriteAllBytesAsync(file, serializer.Serialize(CreateEnvelope(new BorrowingRecord
            {
                Data = new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 })
            })));
            var policy = StoragePolicy.Default with { WatchStorageChanges = false };
            var config = new Config(root);
            var coordinator = new CoalescingFileWriteCoordinator<BorrowingRecord>(new SidecarFileWriteLock<BorrowingRecord>(policy), config, policy);
            var reader = new FileObjectReader<BorrowingRecord>(serializer, new Lifecycle(),
                new EnvelopeConcurrentDictionary<string, IObjectEnvelope<BorrowingRecord>>(), config, policy, coordinator, new ChangeSource());
            var result = await reader.ReadAsync(new FileInfo(file));

            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, result.Data.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitSafeMemorySerializerReceivesOnlyTheBoundedFilePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDbSafeMemorySerializer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var serializer = new SafeMemorySerializer();
            var payload = serializer.Serialize(CreateEnvelope(new BorrowingRecord
            {
                Data = new ReadOnlySequence<byte>(new byte[] { 10, 11, 12 })
            }));
            var file = Path.Combine(root, "custom.mpk");
            await File.WriteAllBytesAsync(file, payload);
            var policy = StoragePolicy.Default with { WatchStorageChanges = false };
            var config = new Config(root);
            var coordinator = new CoalescingFileWriteCoordinator<BorrowingRecord>(
                new SidecarFileWriteLock<BorrowingRecord>(policy), config, policy);
            var reader = new FileObjectReader<BorrowingRecord>(serializer, new Lifecycle(),
                new EnvelopeConcurrentDictionary<string, IObjectEnvelope<BorrowingRecord>>(),
                config, policy, coordinator, new ChangeSource());

            var result = await reader.ReadAsync(new FileInfo(file));

            Assert.AreEqual(payload.Length, serializer.ReceivedLength);
            CollectionAssert.AreEqual(new byte[] { 10, 11, 12 }, result.Data.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SafeMemorySerializer : IObjectSerializer<BorrowingRecord>, IMemoryObjectSerializer<BorrowingRecord>
    {
        private readonly BinaryObjectSerializer<BorrowingRecord> _inner = new();
        internal int ReceivedLength { get; private set; }
        public byte[] Serialize(IObjectEnvelope<BorrowingRecord> envelope) => _inner.Serialize(envelope);
        public IObjectEnvelope<BorrowingRecord> Deserialize(byte[] data) => throw new AssertFailedException("Expected the memory capability.");
        public IObjectEnvelope<BorrowingRecord> DeserializeMemory(ReadOnlyMemory<byte> data)
        {
            ReceivedLength = data.Length;
            return _inner.DeserializeMemory(data);
        }
        public ValueTask SerializeAsync(IObjectEnvelope<BorrowingRecord> envelope, Stream destination, CancellationToken cancellationToken = default)
            => _inner.SerializeAsync(envelope, destination, cancellationToken);
        public ValueTask<IObjectEnvelope<BorrowingRecord>> DeserializeAsync(Stream source, CancellationToken cancellationToken = default)
            => _inner.DeserializeAsync(source, cancellationToken);
    }

    private static IObjectEnvelope<T> CreateEnvelope<T>(T entity) where T : class, IStoredContract<T>
        => new FileObjectEnvelope<T> { EnvelopeCacheKey = "custom", VersionTag = "1.0.0", ObjectInstance = entity };

    [MessagePackFormatter(typeof(BorrowingRecordFormatter))]
    public sealed class BorrowingRecord : IStoredContract<BorrowingRecord>
    {
        public ReadOnlySequence<byte> Data { get; set; }
        public IObjectEnvelope<BorrowingRecord>? Envelope { get; private set; }
    }

    public sealed class BorrowingRecordFormatter : IMessagePackFormatter<BorrowingRecord?>
    {
        public void Serialize(ref MessagePackWriter writer, BorrowingRecord? value, MessagePackSerializerOptions options)
        {
            if (value is null) { writer.WriteNil(); }
            else { writer.Write(value.Data.ToArray()); }
        }
        public BorrowingRecord? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            => reader.TryReadNil() ? null : new() { Data = reader.ReadBytes()!.Value };
    }

    public sealed class HolderRecord : IStoredContract<HolderRecord>
    {
        public BorrowingPayload Payload { get; set; } = new();
        public IObjectEnvelope<HolderRecord>? Envelope { get; private set; }
    }

    [MessagePackFormatter(typeof(BorrowingPayloadFormatter))]
    public sealed class BorrowingPayload { public ReadOnlySequence<byte> Data { get; set; } }

    public sealed class BorrowingPayloadFormatter : IMessagePackFormatter<BorrowingPayload?>
    {
        public void Serialize(ref MessagePackWriter writer, BorrowingPayload? value, MessagePackSerializerOptions options)
        {
            if (value is null) { writer.WriteNil(); }
            else { writer.Write(value.Data.ToArray()); }
        }
        public BorrowingPayload? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            => reader.TryReadNil() ? null : new() { Data = reader.ReadBytes()!.Value };
    }

    private sealed class Lifecycle : IEnvelopeLifecycle<BorrowingRecord>
    {
        public ValueTask<IObjectEnvelope<BorrowingRecord>> WrapAsync(BorrowingRecord entity) => throw new NotSupportedException();
        public BorrowingRecord Unwrap(IObjectEnvelope<BorrowingRecord> envelope) => envelope.ObjectInstance!;
    }

    private sealed class ChangeSource : IEnvelopeStorageChangeSource<BorrowingRecord>
    {
        public event Func<EnvelopeStorageChange, ValueTask>? Changed { add { } remove { } }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void AcknowledgeLocalChange(string providerLocator) { }
        public ValueTask RescanAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Config(string root) : IDbContractConfigDto<BorrowingRecord>
    {
        public string EnvelopeDICacheKey => root;
        public string ContractName => "custom-formatter";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => null;
    }
}
