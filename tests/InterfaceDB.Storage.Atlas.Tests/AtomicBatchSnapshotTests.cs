namespace Idb.Storage.AtlasCore.Tests;

using System.Collections;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    public async Task WriteManyCopiesLyingReadOnlyListBeforePublishing()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repository = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var records = CreateBatchRecords(2);
        var source = new LyingReadOnlyList<IAtlasProviderRecord>(
            reportedCount: 1,
            records);

        var saved = await repository.WriteManyAsync(source);

        Assert.AreEqual(2, saved.Count);
        Assert.AreEqual(records[0].Id, saved[0].Id);
        Assert.AreEqual(records[1].Id, saved[1].Id);
        Assert.IsNotNull(await repository.GetPhysicalAsync(saved[0].Envelope!.EnvelopeCacheKey));
        Assert.IsNotNull(await repository.GetPhysicalAsync(saved[1].Envelope!.EnvelopeCacheKey));
    }

    [TestMethod]
    public async Task WriteManyEnforcesMaximumWhileEnumeratingLyingReadOnlyList()
    {
        using var root = new ProviderTemporaryDirectory();
        var defaults = ProviderOptions();
        var options = defaults with
        {
            StoreOptions = defaults.StoreOptions with { MaximumTransactionMutations = 2 }
        };
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, options);
        var repository = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var records = CreateBatchRecords(3);
        var source = new LyingReadOnlyList<IAtlasProviderRecord>(
            reportedCount: 1,
            records);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => repository.WriteManyAsync(source).AsTask());

        var persisted = 0;
        await foreach (var _ in repository.EnumerateAsync())
        {
            persisted++;
        }
        Assert.AreEqual(0, persisted);
    }

    private static IAtlasProviderRecord[] CreateBatchRecords(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (IAtlasProviderRecord)new AtlasProviderRecord
            {
                Id = $"lying-list-{index:D2}",
                Name = $"Record {index}",
                Score = index
            })
            .ToArray();

    private sealed class LyingReadOnlyList<T>(
        int reportedCount,
        IReadOnlyList<T> enumeratedItems) : IReadOnlyList<T>
    {
        public int Count => reportedCount;

        public T this[int index] =>
            throw new InvalidOperationException("The caller-owned list indexer must not be used after snapshotting.");

        public IEnumerator<T> GetEnumerator() => enumeratedItems.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
