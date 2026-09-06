using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    public async Task ScanRevisionBookkeeping_MaterializationSeedsRevisionWithoutSecondPayloadParse()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        Dictionary<string, long> revisions;
        await using (var writer = BuildProvider(config, options))
        {
            var saved = await writer.GetRequiredService<IEnvelopeRepository<IProviderRecord>>()
                .WriteManyAsync(CreateRecords(8));
            revisions = saved.ToDictionary(x => x.Id, x => x.Envelope!.RevisionTimestampUtcMicroseconds);
        }

        var serializer = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, options, serializer: serializer);
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
        await envelopes.InitializeAsync();
        var readsBefore = serializer.ReadRevisionCount;
        var materializedBefore = serializer.DeserializeCount;
        var scanned = new List<IProviderRecord>();
        await foreach (var envelope in envelopes.EnumerateAsync())
        {
            var entity = envelope.ObjectInstance!;
            Assert.AreSame(envelope, entity.Envelope);
            Assert.AreEqual(revisions[entity.Id], envelope.RevisionTimestampUtcMicroseconds);
            scanned.Add(entity);
        }
        Assert.AreEqual(8, scanned.Count);
        Assert.AreEqual(materializedBefore + 8, serializer.DeserializeCount);
        Assert.AreEqual(readsBefore, serializer.ReadRevisionCount);

        var current = scanned[0];
        var stale = (await envelopes.GetPhysicalAsync(current.Envelope!.EnvelopeCacheKey))!.ObjectInstance!;
        Assert.AreNotSame(current, stale, "Metadata-only reads still create independent objects.");
        current.Score = 99;
        var savedCurrent = await envelopes.WriteAsync(current);
        Assert.IsTrue(savedCurrent.Envelope!.RevisionTimestampUtcMicroseconds > revisions[current.Id]);
        Assert.AreEqual(readsBefore, serializer.ReadRevisionCount, "The scan seeded the committed revision for the write guard.");
        stale.Score = 100;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IProviderRecord>>(() => envelopes.WriteAsync(stale).AsTask());
        await Assert.ThrowsExactlyAsync<WriteConflictException<IProviderRecord>>(() => envelopes.DeleteAsync(stale).AsTask());
        var reread = (await envelopes.GetPhysicalAsync(current.Envelope.EnvelopeCacheKey))!.ObjectInstance!;
        Assert.AreEqual(99, reread.Score);
        Assert.IsTrue(await envelopes.DeleteAsync(reread));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IProviderRecord>>(() => envelopes.WriteAsync(savedCurrent).AsTask());
    }
}
