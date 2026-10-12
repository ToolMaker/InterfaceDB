using System.Security.Cryptography;
using Idb.Transport;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;

namespace InterfaceDB.Transport.DockerTests;

[TestClass]
[TestCategory("Docker")]
public sealed class DockerStorageTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcknowledgedWritesUpdatesDeletesAndEmptyValuesSurviveContainerReplacement(bool crash)
    {
        await using var fixture = await DockerStoreFixture.StartAsync(Manifest());
        var expected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < 240; index++)
        {
            var bytes = new byte[1024 + index * 13];
            new Random(index).NextBytes(bytes);
            expected.Add($"record-{index:D4}", bytes);
        }
        expected.Add("empty", []);
        expected.Add("unicode-\u03bb-\u4e2d", [0, 255, 193]);

        await using (var client = await fixture.ConnectAsync())
        {
            Assert.IsTrue(client.Capabilities.HasFlag(EncodedStoreCapabilities.DurableCommits));
            foreach (var batch in expected.Chunk(40))
                Assert.AreEqual(batch.Length, (await client.ApplyBatchAsync(batch.Select(pair => new EncodedMutation(pair.Key, pair.Value)).ToArray())).AffectedCount);

            var updates = new List<EncodedMutation>();
            for (var index = 0; index < 40; index++)
            {
                var key = $"record-{index:D4}";
                if (index % 2 == 0)
                {
                    var replacement = new byte[] { checked((byte)index), 0, 255 };
                    expected[key] = replacement;
                    updates.Add(new(key, replacement));
                }
                else
                {
                    expected.Remove(key);
                    updates.Add(new(key, null));
                }
            }
            Assert.AreEqual(40, (await client.ApplyBatchAsync(updates.ToArray())).AffectedCount);
            Assert.IsFalse(await client.DeleteAsync("never-stored"));

            // Keep the session open through SIGKILL to test acknowledged journal durability,
            // without an explicit checkpoint or a normal client/host shutdown first.
            await fixture.RecreateAsync(crash: crash);
        }

        await using var reopened = await fixture.ConnectAsync();
        var actual = await ReadAllAsync(reopened, 17);
        CollectionAssert.AreEqual(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var pair in expected) CollectionAssert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
        var empty = await reopened.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Key = "empty" });
        Assert.IsTrue(empty.Found);
        Assert.HasCount(0, empty.Payload!);
        Assert.IsNull(await reopened.GetAsync("record-0001"));
        var many = await reopened.ExecuteAsync(new()
        {
            Command = EncodedStoreCommand.GetMany, Keys = ["empty", "never-stored", "record-0000", "record-0000"]
        });
        CollectionAssert.AreEqual(new[] { "empty", "record-0000", "record-0000" }, many.Records!.Select(record => record.Key).ToArray());
        await reopened.ExecuteAsync(new() { Command = EncodedStoreCommand.Checkpoint });
    }

    [TestMethod]
    public async Task PlainAndContextEncryptedCanonicalEnvelopesRemainByteIdenticalAcrossDockerRecreation()
    {
        await using var fixture = await DockerStoreFixture.StartAsync(Manifest());
        var binary = new BinaryObjectSerializer<StoredDocument>();
        var plain = new EncodedEnvelopeSerializer<StoredDocument>(binary);
        var encrypted = new EncodedEnvelopeSerializer<StoredDocument>(
            new EncryptingObjectSerializer<StoredDocument>(binary, new AesGcmEncryptor(new TestKeyProvider())));
        byte[] context = "docker-test/contract/1.0/stable-storage-context"u8.ToArray();
        byte[] CreatePayload(string key, EncodedEnvelopeSerializer<StoredDocument> serializer) => serializer.Serialize(key,
            new FileObjectEnvelope<StoredDocument>
            {
                EnvelopeCacheKey = key, VersionTag = "1.0.0", RevisionTimestampUtcMicroseconds = 987654321,
                ObjectInstance = new() { Name = "persisted document", Number = 42 }, ExtensionMetadata = [4, 5, 6]
            }, context);
        var plainBytes = CreatePayload("plain", plain);
        var encryptedBytes = CreatePayload("encrypted", encrypted);
        Assert.IsTrue(plainBytes.AsSpan(0, 4).SequenceEqual("IDB6"u8));
        Assert.IsTrue(encryptedBytes.AsSpan(0, 4).SequenceEqual("IDBE"u8));
        await using (var client = await fixture.ConnectAsync())
            await client.ApplyBatchAsync([new("plain", plainBytes), new("encrypted", encryptedBytes)]);
        await fixture.RecreateAsync();
        await using var reopened = await fixture.ConnectAsync();
        foreach (var (key, original, serializer) in new[] { ("plain", plainBytes, plain), ("encrypted", encryptedBytes, encrypted) })
        {
            var bytes = await reopened.GetAsync(key);
            CollectionAssert.AreEqual(original, bytes);
            var result = serializer.Deserialize(key, bytes!, context);
            Assert.AreEqual("1.0.0", result.VersionTag);
            Assert.AreEqual(987654321L, result.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual("persisted document", result.ObjectInstance!.Name);
            Assert.AreEqual(42, result.ObjectInstance.Number);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, result.ExtensionMetadata);
        }
        Assert.ThrowsExactly<CryptographicException>(() => encrypted.Deserialize("encrypted", encryptedBytes, "wrong-context"u8));
    }

    [TestMethod]
    public async Task InvalidAndOversizedBatchesLeavePersistedDataIntactAndSessionUsable()
    {
        await using var fixture = await DockerStoreFixture.StartAsync(Manifest());
        await using (var client = await fixture.ConnectAsync())
        {
            await client.PutAsync("original", [1, 2, 3]);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await client.ApplyBatchAsync([new("original", [9]), new(" ", [8])]));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await client.ApplyBatchAsync([new("original", [9]), new("original", null)]));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await client.ApplyBatchAsync([new("original", [9]), new("oversize", new byte[client.Limits.MaximumPayloadBytes + 1])]));
            var full = new byte[client.Limits.MaximumPayloadBytes];
            var frameError = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
                await client.ApplyBatchAsync([new("original", full), new("new", full)]));
            Assert.AreEqual(RemoteErrorCode.LimitExceeded, frameError.Code);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await client.GetAsync("original"));
            Assert.IsNull(await client.GetAsync("new"));
            // An encode rejection must not consume the correlation ID or break later requests.
            await client.PutAsync("valid-after-rejections", [7]);
        }
        await fixture.RecreateAsync(crash: true);
        await using var reopened = await fixture.ConnectAsync();
        var records = await ReadAllAsync(reopened, 10);
        CollectionAssert.AreEqual(new[] { "original", "valid-after-rejections" }, records.Keys.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, records["original"]);
        CollectionAssert.AreEqual(new byte[] { 7 }, records["valid-after-rejections"]);
    }

    private static async Task<SortedDictionary<string, byte[]>> ReadAllAsync(IEncodedEnvelopeStore store, int pageSize)
    {
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await store.EnumerateAsync(pageSize, cursor);
            foreach (var record in page.Records!) Assert.IsTrue(result.TryAdd(record.Key, record.Payload), $"Repeated key {record.Key}");
            cursor = page.NextAfterKey;
        } while (cursor is not null);
        return result;
    }

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1", EnvelopeFormat = "IDB6-or-IDBE-v3",
        ContractIdentity = "DockerStorageTest", ContractVersion = "1.0.0", ContractSchemaHash = "storage-v1",
        ImplementationManifestHash = "storage-test", IndexDefinitionHash = "none"
    };

    [MessagePackObject(true)]
    public sealed class StoredDocument : IStoredContract<StoredDocument>
    {
        public string Name { get; set; } = "";
        public int Number { get; set; }
        [IgnoreMember] public IObjectEnvelope<StoredDocument>? Envelope { get; set; }
    }

    private sealed class TestKeyProvider : IKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
}
