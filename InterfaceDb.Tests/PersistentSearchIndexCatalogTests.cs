using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Implementation.Indexes;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class PersistentSearchIndexCatalogTests
{
    [TestMethod]
    public async Task TryLoad_ResolvesRepeatedRuntimeTypeOnce()
    {
        var path = CreateTemporaryPath();
        try
        {
            var runtimeFingerprint = CreateFingerprint(0x31);
            var records = Enumerable.Range(0, 128)
                .Select(index => CreateRecord(
                    $"row-{index:D3}",
                    typeof(CatalogRuntimeRecord),
                    runtimeFingerprint,
                    [index],
                    [typeof(int)]))
                .ToArray();
            var identity = new SearchIndexStorageIdentity(records.Length, CreateFingerprint(0x11));
            var contractFingerprint = CreateFingerprint(0x21);
            await PersistentSearchIndexCatalog.WriteAsync(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                records);

            var resolutions = 0;
            var loaded = PersistentSearchIndexCatalog.TryLoad(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                runtimeTypeName =>
                {
                    resolutions++;
                    Assert.AreEqual(typeof(CatalogRuntimeRecord).AssemblyQualifiedName, runtimeTypeName);
                    return new RuntimeSearchIndexCatalogSchema(
                        typeof(CatalogRuntimeRecord),
                        runtimeFingerprint,
                        [typeof(int)]);
                });

            Assert.IsNotNull(loaded);
            Assert.HasCount(records.Length, loaded);
            Assert.AreEqual(1, resolutions);
            Assert.IsTrue(loaded.All(record => record.RuntimeType == typeof(CatalogRuntimeRecord)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TryLoad_ResolvesEachDistinctRuntimeTypeOnce()
    {
        var path = CreateTemporaryPath();
        try
        {
            var firstFingerprint = CreateFingerprint(0x41);
            var secondFingerprint = CreateFingerprint(0x51);
            SearchIndexCatalogRecord[] records =
            [
                CreateRecord("row-a1", typeof(CatalogRuntimeRecord), firstFingerprint, ["alpha"], [typeof(string)]),
                CreateRecord("row-b1", typeof(AlternateCatalogRuntimeRecord), secondFingerprint, [7L], [typeof(long)]),
                CreateRecord("row-a2", typeof(CatalogRuntimeRecord), firstFingerprint, ["beta"], [typeof(string)])
            ];
            var identity = new SearchIndexStorageIdentity(records.Length, CreateFingerprint(0x12));
            var contractFingerprint = CreateFingerprint(0x22);
            await PersistentSearchIndexCatalog.WriteAsync(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                records);

            var resolutions = new Dictionary<string, int>(StringComparer.Ordinal);
            var loaded = PersistentSearchIndexCatalog.TryLoad(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                runtimeTypeName =>
                {
                    resolutions.TryGetValue(runtimeTypeName, out var count);
                    resolutions[runtimeTypeName] = count + 1;
                    if (runtimeTypeName == typeof(CatalogRuntimeRecord).AssemblyQualifiedName)
                    {
                        return new RuntimeSearchIndexCatalogSchema(
                            typeof(CatalogRuntimeRecord),
                            firstFingerprint,
                            [typeof(string)]);
                    }

                    if (runtimeTypeName == typeof(AlternateCatalogRuntimeRecord).AssemblyQualifiedName)
                    {
                        return new RuntimeSearchIndexCatalogSchema(
                            typeof(AlternateCatalogRuntimeRecord),
                            secondFingerprint,
                            [typeof(long)]);
                    }

                    return null;
                });

            Assert.IsNotNull(loaded);
            Assert.HasCount(records.Length, loaded);
            Assert.AreEqual(2, resolutions.Count);
            Assert.IsTrue(resolutions.Values.All(count => count == 1));
            Assert.AreEqual(typeof(CatalogRuntimeRecord), loaded[0].RuntimeType);
            Assert.AreEqual(typeof(AlternateCatalogRuntimeRecord), loaded[1].RuntimeType);
            Assert.AreEqual(typeof(CatalogRuntimeRecord), loaded[2].RuntimeType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsUnknownRuntimeType()
    {
        var path = CreateTemporaryPath();
        try
        {
            var runtimeFingerprint = CreateFingerprint(0x61);
            var records = new[]
            {
                CreateRecord("row-unknown", typeof(CatalogRuntimeRecord), runtimeFingerprint, [1], [typeof(int)])
            };
            var identity = new SearchIndexStorageIdentity(records.Length, CreateFingerprint(0x13));
            var contractFingerprint = CreateFingerprint(0x23);
            await PersistentSearchIndexCatalog.WriteAsync(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                records);

            var loaded = PersistentSearchIndexCatalog.TryLoad(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                _ => null);

            Assert.IsNull(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TryLoad_RejectsRuntimeSchemaFingerprintMismatch()
    {
        var path = CreateTemporaryPath();
        try
        {
            var persistedFingerprint = CreateFingerprint(0x71);
            var records = new[]
            {
                CreateRecord("row-stale", typeof(CatalogRuntimeRecord), persistedFingerprint, [1], [typeof(int)])
            };
            var identity = new SearchIndexStorageIdentity(records.Length, CreateFingerprint(0x14));
            var contractFingerprint = CreateFingerprint(0x24);
            await PersistentSearchIndexCatalog.WriteAsync(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                records);

            var loaded = PersistentSearchIndexCatalog.TryLoad(
                path,
                identity,
                contractFingerprint,
                [typeof(int)],
                _ => new RuntimeSearchIndexCatalogSchema(
                    typeof(CatalogRuntimeRecord),
                    CreateFingerprint(0x72),
                    [typeof(int)]));

            Assert.IsNull(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SearchIndexCatalogRecord CreateRecord(
        string physicalKey,
        Type runtimeType,
        byte[] runtimeFingerprint,
        object?[] runtimeValues,
        IReadOnlyList<Type> runtimeValueTypes) =>
        new(
            physicalKey,
            runtimeType.AssemblyQualifiedName!,
            [physicalKey.Length],
            runtimeFingerprint,
            runtimeValueTypes,
            runtimeValues);

    private static byte[] CreateFingerprint(byte seed) =>
        Enumerable.Range(0, 32).Select(index => unchecked((byte)(seed + index))).ToArray();

    private static string CreateTemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"interface-db-catalog-{Guid.NewGuid():N}.idbsidx");

    private sealed class CatalogRuntimeRecord
    {
    }

    private sealed class AlternateCatalogRuntimeRecord
    {
    }
}
