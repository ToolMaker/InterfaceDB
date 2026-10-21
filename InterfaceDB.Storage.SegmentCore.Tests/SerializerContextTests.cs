using System.Collections.Concurrent;
using System.Security.Cryptography;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

// Regression coverage for serializer capability checks, authenticated storage context,
// committed cache copies and single-pass encrypted key rotation.
public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    [DataRow(StorageObjectCacheMode.MetadataOnly)]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    public async Task ContextualSerializer_ReceivesTheExactStorageContextOnEveryPath(StorageObjectCacheMode mode)
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with { ObjectCacheMode = mode, HydrateOnInitialize = false });
        var encryptor = new ContextCheckingEncryptor(currentVersion: 1);
        var keys = Enumerable.Range(0, 60).Select(i => $"ctx{i:D3}").ToArray();
        var physicalKeys = new List<string>();
        await using (var provider = BuildProvider(config, ProviderOptions(), encryptor: encryptor))
        {
            var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            foreach (var key in keys)
            {
                var saved = await repo.SaveAsync(new ProviderRecord { Id = key, Name = "n-" + key, Score = 1 });
                physicalKeys.Add(saved.Envelope!.EnvelopeCacheKey);
            }
            // Updates read the stored revision; deletes resolve the stored record.
            for (var i = 0; i < 20; i++)
            {
                var found = await repo.SearchFirstAsync(r => r.Name == "n-" + keys[i]);
                Assert.IsNotNull(found);
                found.Score = 2;
                await repo.SaveAsync(found);
            }
            for (var i = 50; i < 60; i++)
            {
                var found = await repo.SearchFirstAsync(r => r.Name == "n-" + keys[i]);
                await repo.DeleteAsync(found!);
            }
        }

        await using (var reopened = BuildProvider(config, ProviderOptions(), encryptor: encryptor))
        {
            var repo = reopened.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            var all = new List<IProviderRecord>();
            await foreach (var record in repo.SearchManyAsync(r => r.Score >= 1)) all.Add(record);
            Assert.HasCount(50, all);
            Assert.AreEqual(20, all.Count(r => r.Score == 2));
        }

        var expected = physicalKeys.ToDictionary(
            key => Convert.ToHexString(StorageEncryptionContext.Create(config, "segment-core", key)), key => key);
        Assert.IsGreaterThan(0, encryptor.ObservedContexts.Count);
        foreach (var context in encryptor.ObservedContexts.Keys)
            Assert.IsTrue(expected.ContainsKey(context), "A serializer call received a context that is not the storage context of any key.");
        Assert.AreEqual(keys.Length, encryptor.ObservedContexts.Count, "Every stored key should have been encrypted and read with its own context.");
        Assert.AreEqual(0, encryptor.RejectedContexts);
    }

    [TestMethod]
    public async Task ContextualSerializer_RejectsAPayloadReadUnderAnotherKeysContext()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var encryptor = new ContextCheckingEncryptor(currentVersion: 1);
        await using var provider = BuildProvider(config, ProviderOptions(), encryptor: encryptor);
        var serializer = (IContextualObjectSerializer<IProviderRecord>)provider.GetRequiredService<IObjectSerializer<IProviderRecord>>();
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repo.InitializeAsync();
        var saved = await repo.SaveAsync(new ProviderRecord { Id = "a", Name = "a", Score = 1 });
        var payload = serializer.Serialize(saved.Envelope!, StorageEncryptionContext.Create(config, "segment-core", "a"));
        Assert.ThrowsExactly<CryptographicException>(
            () => serializer.DeserializeForRead(payload, StorageEncryptionContext.Create(config, "segment-core", "b")));
    }

    [TestMethod]
    public async Task BulkReadOfRotatedPayloads_DecryptsEachRecordOnce_AndRewritesEachOnce()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        const int count = 300;
        await using (var writer = BuildProvider(config, ProviderOptions(), encryptor: new ContextCheckingEncryptor(currentVersion: 1)))
        {
            var repo = writer.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            await repo.SaveManyAsync(Enumerable.Range(0, count)
                .Select(i => (IProviderRecord)new ProviderRecord { Id = $"rot{i:D4}", Name = "r" + i, Score = i }).ToList());
        }

        // Initialization rebuilds the in-memory indexes of an encrypted store by reading every record,
        // so the key version changes only after it: the bulk search below is the first rotated read.
        var rotated = new ContextCheckingEncryptor(currentVersion: 1);
        await using (var reader = BuildProvider(config, ProviderOptions(), encryptor: rotated))
        {
            var repo = reader.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            Assert.AreEqual(0, rotated.RotatedReads);
            rotated.CurrentVersion = 2;
            rotated.ResetCounters();
            var seen = new HashSet<int>();
            await foreach (var record in repo.SearchManyAsync(r => r.Score >= 0)) Assert.IsTrue(seen.Add(record.Score));
            Assert.HasCount(count, seen);
            Assert.AreEqual(count, rotated.Decryptions, "Each rotated record must be decrypted exactly once by the bulk read.");
            Assert.AreEqual(count, rotated.RotatedReads);
            Assert.AreEqual(count, rotated.Encryptions, "Each rotated record must be re-encrypted exactly once.");
        }

        var settled = new ContextCheckingEncryptor(currentVersion: 2);
        await using (var again = BuildProvider(config, ProviderOptions(), encryptor: settled))
        {
            var repo = again.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            Assert.AreEqual(0, settled.RotatedReads, "Replacement payloads must have been committed under the current key version.");
            var total = 0;
            await foreach (var _ in repo.SearchManyAsync(r => r.Score >= 0)) total++;
            Assert.AreEqual(count, total);
            Assert.AreEqual(0, settled.RotatedReads, "Replacement payloads must have been committed under the current key version.");
        }
    }

    // Test-only authenticated format: [version][SHA-256(associated data)][plaintext XOR 0x5A].
    private sealed class ContextCheckingEncryptor(byte currentVersion) : IContextualEncryptor
    {
        public byte CurrentVersion { get; set; } = currentVersion;
        public readonly ConcurrentDictionary<string, byte> ObservedContexts = new();
        private int _decryptions, _encryptions, _rotatedReads, _rejected;
        public int Decryptions => Volatile.Read(ref _decryptions);
        public int Encryptions => Volatile.Read(ref _encryptions);
        public int RotatedReads => Volatile.Read(ref _rotatedReads);
        public int RejectedContexts => Volatile.Read(ref _rejected);
        public void ResetCounters() { _decryptions = 0; _encryptions = 0; _rotatedReads = 0; _rejected = 0; }

        public byte[] Encrypt(ReadOnlySpan<byte> plain) => throw new InvalidOperationException("A storage context is required.");
        public byte[] Decrypt(ReadOnlySpan<byte> cipher) => throw new InvalidOperationException("A storage context is required.");

        public byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associatedData)
        {
            Interlocked.Increment(ref _encryptions);
            ObservedContexts.TryAdd(Convert.ToHexString(associatedData), 0);
            var result = new byte[1 + 32 + plain.Length];
            result[0] = CurrentVersion;
            SHA256.HashData(associatedData, result.AsSpan(1, 32));
            for (var i = 0; i < plain.Length; i++) result[33 + i] = (byte)(plain[i] ^ 0x5A);
            return result;
        }

        public byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData)
            => DecryptWithMetadata(cipher, associatedData).Plaintext;

        public EncryptionDecryptionResult DecryptWithMetadata(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData)
        {
            Interlocked.Increment(ref _decryptions);
            ObservedContexts.TryAdd(Convert.ToHexString(associatedData), 0);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(associatedData, hash);
            if (cipher.Length < 33 || !cipher.Slice(1, 32).SequenceEqual(hash))
            {
                Interlocked.Increment(ref _rejected);
                throw new CryptographicException("Associated data does not match.");
            }
            var plain = new byte[cipher.Length - 33];
            for (var i = 0; i < plain.Length; i++) plain[i] = (byte)(cipher[33 + i] ^ 0x5A);
            var rotated = cipher[0] != CurrentVersion;
            if (rotated) Interlocked.Increment(ref _rotatedReads);
            return new EncryptionDecryptionResult(plain, "v" + cipher[0], RequiresReencryption: rotated, UsesCurrentEncryption: !rotated);
        }
    }
}
