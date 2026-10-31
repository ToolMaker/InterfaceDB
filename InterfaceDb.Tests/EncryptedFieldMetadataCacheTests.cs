using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class EncryptedFieldMetadataCacheTests
{
    [TestMethod]
    public void SharedMetadataKeepsEachProxyCiphertextAndValuesIndependent()
    {
        var processor = new EncryptedStorageFieldProcessor<ISecretRecord>(new TestEncryptor(),
            key => System.Text.Encoding.UTF8.GetBytes(key), EncryptedFieldWriteMode.LegacyContextless);
        var first = new SecretRecord { Secret = "first", Number = 5 };
        var second = new SecretRecord { Secret = "second", Number = 8 };
        var firstEnvelope = CreateEnvelope(first);
        var secondEnvelope = CreateEnvelope(second);
        processor.PrepareForStorage(first, firstEnvelope);
        processor.PrepareForStorage(second, secondEnvelope);
        Assert.IsNull(first.Secret);
        Assert.IsNull(second.Secret);
        Assert.AreEqual(0, first.Number);
        Assert.AreEqual(0, second.Number);

        var firstProxy = processor.WrapForRead(first, firstEnvelope);
        var secondProxy = processor.WrapForRead(second, secondEnvelope);
        Assert.AreEqual("first", firstProxy.Secret);
        Assert.AreEqual("second", secondProxy.Secret);
        Assert.AreEqual(5, firstProxy.Number);
        Assert.AreEqual(8, secondProxy.Number);
        Assert.AreEqual(Idb.Libraries.Abstractions.Enums.StorageObjectCacheMode.StrongObject,
            StorageObjectCachePolicy.ResolveObjectCacheMode(firstProxy, Idb.Libraries.Abstractions.Options.StoragePolicy.Default),
            "An encryption wrapper must preserve the implementation's cache attribute.");

        firstProxy.Secret = "changed";
        firstProxy.Number = 12;
        Assert.AreEqual("changed", firstProxy.Secret);
        Assert.AreEqual(12, firstProxy.Number);
        Assert.AreEqual("second", secondProxy.Secret);
        Assert.IsNull(first.Secret);
        Assert.AreEqual(0, first.Number);

        var metadata = typeof(EncryptedStorageDispatchProxy<ISecretRecord>)
            .GetField("_definitions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.AreSame(metadata.GetValue(firstProxy), metadata.GetValue(secondProxy));
        Assert.AreSame(first, processor.PrepareForStorage(firstProxy, firstEnvelope));
        Assert.AreEqual("changed", firstProxy.Secret);
    }

    [TestMethod]
    public void DifferentRuntimeTypesRetainExplicitInterfacePropertyResolution()
    {
        var processor = new EncryptedStorageFieldProcessor<ISecretRecord>(new TestEncryptor(),
            key => System.Text.Encoding.UTF8.GetBytes(key), EncryptedFieldWriteMode.LegacyContextless);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            foreach (ISecretRecord entity in new ISecretRecord[] { new SecretRecord(), new ExplicitSecretRecord() })
            {
                entity.Secret = "value-" + iteration;
                entity.Number = iteration;
                var envelope = CreateEnvelope(entity);
                processor.PrepareForStorage(entity, envelope);
                Assert.IsNull(entity.Secret);
                var proxy = processor.WrapForRead(entity, envelope);
                Assert.AreEqual("value-" + iteration, proxy.Secret);
                Assert.AreEqual(iteration, proxy.Number);
            }
        }
    }

    private static IObjectEnvelope<ISecretRecord> CreateEnvelope(ISecretRecord instance)
        => new FileObjectEnvelope<ISecretRecord> { EnvelopeCacheKey = Guid.NewGuid().ToString(), VersionTag = "1.0.0", ObjectInstance = instance };

    public interface ISecretRecord : IStoredContract<ISecretRecord>
    {
        [EncryptedStorageField]
        string? Secret { get; set; }
        [EncryptedStorageField]
        int Number { get; set; }
    }

    [StorageCached]
    private sealed class SecretRecord : ISecretRecord
    {
        public string? Secret { get; set; }
        public int Number { get; set; }
        public IObjectEnvelope<ISecretRecord>? Envelope { get; private set; }
    }

    private sealed class ExplicitSecretRecord : ISecretRecord
    {
        string? ISecretRecord.Secret { get; set; }
        int ISecretRecord.Number { get; set; }
        public IObjectEnvelope<ISecretRecord>? Envelope { get; private set; }
    }

    private sealed class TestEncryptor : IEncryptor
    {
        public byte[] Encrypt(ReadOnlySpan<byte> plain) => Transform(plain);
        public byte[] Decrypt(ReadOnlySpan<byte> cipher) => Transform(cipher);
        private static byte[] Transform(ReadOnlySpan<byte> value)
        {
            var result = value.ToArray();
            for (var index = 0; index < result.Length; index++) { result[index] ^= 0x5a; }
            return result;
        }
    }
}
