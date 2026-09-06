using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class StorageCacheAttributeTests
{
    [TestMethod]
    public async Task CachedAttributeDecisionPreservesInheritancePolicyAndMutableEnvelopeOverrides()
    {
        var plain = new Record(); var attributed = new AttributedRecord(); var inherited = new InheritedRecord();
        var metadata = StoragePolicy.Default with { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly };
        foreach (var mode in new[] { StorageObjectCacheMode.MetadataOnly, StorageObjectCacheMode.WeakObject, StorageObjectCacheMode.StrongObject })
        {
            var policy = metadata with { ObjectCacheMode = mode };
            Assert.AreEqual(mode, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(plain, policy));
            Assert.AreEqual(StorageObjectCacheMode.StrongObject, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(attributed, policy));
            Assert.AreEqual(StorageObjectCacheMode.StrongObject, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(inherited, policy));
        }
        plain.Envelope = new FileObjectEnvelope<Record> { EnvelopeCacheKey = "one", VersionTag = "1", ObjectInstance = plain };
        for (int i = 0; i < 10; i++)
        {
            plain.Envelope.CacheObjectInstance = true;
            Assert.AreEqual(StorageObjectCacheMode.StrongObject, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(plain, metadata));
            plain.Envelope.CacheObjectInstance = false;
            Assert.AreEqual(StorageObjectCacheMode.MetadataOnly, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(plain, metadata));
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 256; i++)
            {
                Assert.AreEqual(StorageObjectCacheMode.MetadataOnly, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(plain, metadata));
                Assert.AreEqual(StorageObjectCacheMode.StrongObject, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(inherited, metadata));
            }
        })));
    }

    [TestMethod]
    public void AttributeCacheDoesNotKeepCollectibleRuntimeTypeOrEntityAlive()
    {
        var references = CreateCollectibleRecord();
        for (int i = 0; i < 10 && (references.Type.IsAlive || references.Entity.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.IsFalse(references.Entity.IsAlive);
        Assert.IsFalse(references.Type.IsAlive, "The type-level reflection cache must not pin collectible assemblies.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Type, WeakReference Entity) CreateCollectibleRecord()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CacheCollectible" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var builder = assembly.DefineDynamicModule("main").DefineType("Derived", TypeAttributes.Public, typeof(Record));
        var type = builder.CreateType()!;
        var record = (Record)Activator.CreateInstance(type)!;
        var policy = StoragePolicy.Default with { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly };
        Assert.AreEqual(StorageObjectCacheMode.MetadataOnly, StorageObjectCachePolicy.ResolveObjectCacheMode<Record>(record, policy));
        return (new WeakReference(type), new WeakReference(record));
    }

    public class Record : IStoredContract<Record> { public IObjectEnvelope<Record>? Envelope { get; set; } }
    [StorageCached] public class AttributedRecord : Record;
    public sealed class InheritedRecord : AttributedRecord;
}
