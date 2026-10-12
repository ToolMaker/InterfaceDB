using System.Security.Cryptography;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private const string MarkerFormat = "test-marker-envelope-v1";

    [TestMethod]
    public async Task Serializer_CustomFormatSurvivesReopenAndPreservesRevisionChecks()
    {
        var serializer = new MarkerSerializer();
        var manifest = Manifest() with { EnvelopeFormat = MarkerFormat };
        var path = Path.Combine(_root, "serializer-reopen");
        string key;
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(store) with
            { Stores = [new("people", manifest, store)] }))
        {
            var options = WithSerializer(host, serializer, manifest);
            await using var first = new TcpRepository<IRemotePerson>(options);
            await using var second = new TcpRepository<IRemotePerson>(options);
            var saved = await first.SaveAsync(new RemotePerson { Name = "custom", Score = 1 });
            key = saved.Envelope!.EnvelopeCacheKey;
            var stale = await second.QuerySingleAsync(_ => true);
            saved.Score = 2;
            await first.SaveAsync(saved);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await second.SaveAsync(stale));
            var records = (await store.EnumerateAsync(20)).Records!;
            Assert.IsTrue(records.Any(record => record.Payload.AsSpan().StartsWith("CUSTOM:"u8)));
        }
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(store) with
            { Stores = [new("people", manifest, store)] }))
        await using (var reopened = new TcpRepository<IRemotePerson>(WithSerializer(host, serializer, manifest)))
        {
            var saved = await reopened.QuerySingleAsync(_ => true);
            Assert.AreEqual(2, saved.Score);
            Assert.AreEqual(key, saved.Envelope!.EnvelopeCacheKey);
            Assert.IsTrue(saved.Envelope.RevisionTimestampUtcMicroseconds > 0);
            await reopened.DeleteAsync(saved);
            Assert.IsNull(await reopened.QueryFirstAsync(_ => true));
        }
        Assert.IsTrue(serializer.Writes > 0);
        Assert.IsTrue(serializer.Reads > 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Serializer_DiReusesSegmentCoreRegistrationIncludingEncryption(bool encrypted)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var manifest = Manifest() with { EnvelopeFormat = MarkerFormat };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { Stores = [new("people", manifest, store)] });
        var inner = new MarkerSerializer();
        IEncryptor encryptor = encrypted ? new AesGcmEncryptor(new SerializerKey()) : new EncryptionNone();
        IObjectSerializer<IRemotePerson> serializer = encrypted
            ? new EncryptingObjectSerializer<IRemotePerson>(inner, encryptor, 4 * 1024 * 1024) : inner;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IObjectSerializer<IRemotePerson>>("local", serializer);
        services.AddKeyedSingleton<IObjectSerializer<IRemotePerson>>("unused-format", new NonRevisionSerializer());
        services.AddSegmentCoreRepository<IRemotePerson>(new CacheConfig(Path.Combine(_root, "local"), "local"),
            encryptor: encryptor, runtimeTypeAllowlist: new(typeof(RemotePerson)));
        var options = Options(host, Token("parent-a")) with
        {
            Encryptor = encryptor, SerializerEnvelopeFormat = MarkerFormat,
            Connection = Options(host, Token("parent-a")).Connection with { Manifest = manifest }
        };
        services.AddTcpRepository(options, databaseKey: "remote", serializerKey: "local");
        await using var provider = services.BuildServiceProvider();
        Assert.AreSame(serializer, provider.GetRequiredKeyedService<IObjectSerializer<IRemotePerson>>("local"));
        var local = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("local");
        await local.InitializeAsync();
        await local.SaveAsync(new RemotePerson { Name = "local", Score = 10 });
        int localWrites = inner.Writes;
        Assert.IsTrue(localWrites > 0);
        var remote = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("remote");
        await remote.SaveAsync(new RemotePerson { Name = "remote", Score = 20 });
        Assert.IsTrue(inner.Writes > localWrites, "TCP must use the instance registered for storage.");
        Assert.AreEqual(20, (await remote.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(10, (await local.QuerySingleAsync(_ => true)).Score);
        var records = (await store.EnumerateAsync(20)).Records!;
        Assert.IsTrue(records.Any(record => record.Payload.AsSpan().StartsWith(encrypted ? "IDBE"u8 : "CUSTOM:"u8)));
    }

    [TestMethod]
    public async Task Serializer_OwnKeyAndDefaultRegistrationsRemainUsable()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var services = new ServiceCollection();
        // Counting wrapper uses the existing wire format, so no format change is necessary.
        var custom = new MarkerSerializer(useMarker: false);
        services.AddKeyedSingleton<IObjectSerializer<IRemotePerson>>("selected", custom);
        services.AddTcpRepository(Options(host, Token("parent-a")), "selected");
        services.AddTcpRepository(Options(host, Token("parent-b")), "default");
        await using var provider = services.BuildServiceProvider();
        var selected = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("selected");
        await selected.SaveAsync(new RemotePerson { Name = "a" });
        Assert.IsTrue(custom.Writes > 0);
        var defaultRepository = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("default");
        await defaultRepository.SaveAsync(new RemotePerson { Name = "b" });
        Assert.AreEqual("b", (await defaultRepository.QuerySingleAsync(_ => true)).Name);
        Assert.AreSame(selected, provider.GetRequiredService<IRepository<IRemotePerson>>());
    }

    [TestMethod]
    public async Task Serializer_RejectsAmbiguousMissingOrNonRevisionRegistrations()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        Assert.ThrowsExactly<ArgumentException>(() => new TcpRepository<IRemotePerson>(options with { Serializer = new NonRevisionSerializer() }));
        var missing = new ServiceCollection();
        missing.AddTcpRepository(options, "remote", "missing");
        await using (var provider = missing.BuildServiceProvider())
            Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("remote"));
        var duplicate = new ServiceCollection();
        duplicate.AddKeyedSingleton<IObjectSerializer<IRemotePerson>>("format", new MarkerSerializer());
        duplicate.AddKeyedSingleton<IObjectSerializer<IRemotePerson>>("format", new MarkerSerializer());
        duplicate.AddTcpRepository(options, "remote", "format");
        await using (var provider = duplicate.BuildServiceProvider())
            Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("remote"));
        Assert.ThrowsExactly<ArgumentException>(() => duplicate.AddTcpRepository(options with { Serializer = new MarkerSerializer() }, "other", "format"));
    }

    [TestMethod]
    public async Task Serializer_RejectsFormatMismatchAndMissingCustomImplementation()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        Assert.ThrowsExactly<ArgumentException>(() => new TcpRepository<IRemotePerson>(options with
            { Serializer = new MarkerSerializer(), SerializerEnvelopeFormat = MarkerFormat }));
        var manifest = Manifest() with { EnvelopeFormat = MarkerFormat };
        options = options with { SerializerEnvelopeFormat = MarkerFormat, Connection = options.Connection with { Manifest = manifest } };
        Assert.ThrowsExactly<ArgumentException>(() => new TcpRepository<IRemotePerson>(options));
        await using var wrongServer = new TcpRepository<IRemotePerson>(options with { Serializer = new MarkerSerializer() });
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await wrongServer.InitializeAsync());
        Assert.AreEqual(RemoteErrorCode.IncompatibleManifest, error.Code);
        Assert.AreEqual(0, (await store.EnumerateAsync(20)).Records!.Length);
    }

    [TestMethod]
    public async Task Serializer_RejectsEncryptionDowngradeOrConflictingEncryptionPolicy()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var encryption = new AesGcmEncryptor(new SerializerKey());
        var options = Options(host, Token("parent-a")) with { Encryptor = encryption, Serializer = new MarkerSerializer() };
        Assert.ThrowsExactly<ArgumentException>(() => new TcpRepository<IRemotePerson>(options));
        options = options with { Serializer = new EncryptingObjectSerializer<IRemotePerson>(
            new MarkerSerializer(), new AesGcmEncryptor(new SerializerKey()), 4096) };
        Assert.ThrowsExactly<ArgumentException>(() => new TcpRepository<IRemotePerson>(options));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Serializer_CustomWritesRespectPolicyAndNegotiatedLimits(bool negotiated)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var hostOptions = HostOptions(store);
        if (negotiated) hostOptions = hostOptions with { Limits = new() { MaximumPayloadBytes = 2048 } };
        await using var host = await TcpStoreHost.StartAsync(hostOptions);
        var options = Options(host, Token("parent-a")) with { Serializer = new MarkerSerializer(false) };
        if (!negotiated) options = options with { StoragePolicy = options.StoragePolicy with { MaximumStoredObjectBytes = 2048 } };
        await using var repository = new TcpRepository<IRemotePerson>(options);
        var saved = await repository.SaveAsync(new RemotePerson { Name = "original" });
        saved.Name = new string('x', 8192);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await repository.SaveAsync(saved));
        Assert.AreEqual("original", (await repository.QuerySingleAsync(_ => true)).Name);
    }

    [TestMethod]
    public async Task Serializer_OversizedReadsAreRejectedBeforeCustomDeserialization()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using (var writer = Client(host, "parent-a"))
            await writer.SaveAsync(new RemotePerson { Name = new string('x', 8192) });
        var serializer = new MarkerSerializer(false);
        var options = Options(host, Token("parent-a")) with { Serializer = serializer };
        await using var reader = new TcpRepository<IRemotePerson>(options with
            { StoragePolicy = options.StoragePolicy with { MaximumStoredObjectBytes = 2048 } });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reader.InitializeAsync());
        Assert.AreEqual(0, serializer.Reads);
    }


    [TestMethod]
    public async Task Serializer_CacheIdentitySeparatesFormatsAndPreservesDefaultIdentity()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        await using var original = new TcpRepository<IRemotePerson>(options);
        await using var reused = new TcpRepository<IRemotePerson>(options with { Serializer = new MarkerSerializer(false) });
        await using var changed = new TcpRepository<IRemotePerson>(WithSerializer(host, new MarkerSerializer(),
            Manifest() with { EnvelopeFormat = MarkerFormat }));
        Assert.AreEqual(await original.GetCacheIdentityAsync(), await reused.GetCacheIdentityAsync());
        Assert.AreNotEqual(await original.GetCacheIdentityAsync(), await changed.GetCacheIdentityAsync());
    }

    private TcpRepositoryOptions<IRemotePerson> WithSerializer(TcpStoreHost host, IObjectSerializer<IRemotePerson> serializer,
        CompatibilityManifest manifest)
    {
        var options = Options(host, Token("parent-a"));
        return options with { Serializer = serializer, SerializerEnvelopeFormat = manifest.EnvelopeFormat,
            Connection = options.Connection with { Manifest = manifest } };
    }

    // A deliberately different test format proves the transport does not silently reconstruct MessagePack.
    private sealed class MarkerSerializer(bool useMarker = true) : IRevisionObjectSerializer<IRemotePerson>
    {
        private readonly BinaryObjectSerializer<IRemotePerson> _inner = new(runtimeTypeAllowlist: new(typeof(RemotePerson)));
        private int _writes, _reads;
        public int Writes => Volatile.Read(ref _writes);
        public int Reads => Volatile.Read(ref _reads);
        public byte[]? LastPayload { get; private set; }
        public void AssertAndReset(int writes, int reads)
        {
            Assert.AreEqual(writes, Writes, "Unexpected envelope serialization count.");
            Assert.AreEqual(reads, Reads, "Unexpected envelope deserialization count.");
            Interlocked.Exchange(ref _writes, 0);
            Interlocked.Exchange(ref _reads, 0);
        }
        public byte[] Serialize(IObjectEnvelope<IRemotePerson> envelope)
        {
            Interlocked.Increment(ref _writes);
            var payload = _inner.Serialize(envelope);
            return LastPayload = useMarker ? [.. "CUSTOM:"u8, .. payload] : payload;
        }
        public IObjectEnvelope<IRemotePerson> Deserialize(byte[] data)
        {
            Interlocked.Increment(ref _reads);
            return _inner.Deserialize(Unwrap(data).ToArray());
        }
        private ReadOnlyMemory<byte> Unwrap(ReadOnlyMemory<byte> payload)
        {
            if (!useMarker) return payload;
            if (!payload.Span.StartsWith("CUSTOM:"u8)) throw new InvalidDataException("Wrong test serializer.");
            return payload[7..];
        }
        public long ReadRevision(ReadOnlyMemory<byte> payload) => _inner.ReadRevision(Unwrap(payload));
        public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return ReadRevision(buffer.ToArray());
        }
    }
    private sealed class NonRevisionSerializer : IObjectSerializer<IRemotePerson>
    {
        public byte[] Serialize(IObjectEnvelope<IRemotePerson> envelope) => throw new AssertFailedException("Unselected serializer was used.");
        public IObjectEnvelope<IRemotePerson> Deserialize(byte[] data) => throw new AssertFailedException("Unselected serializer was used.");
    }
    private sealed class SerializerKey : IKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
}
