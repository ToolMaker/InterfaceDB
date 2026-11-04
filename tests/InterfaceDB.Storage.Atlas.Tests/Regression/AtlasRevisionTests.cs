// Regression copied from experiments/Atlas.RevisionTests/AtlasRevisionTests.cs; only provider configuration is adapted for the promoted hybrid.
using AtlasQualification;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Storage.AtlasCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class AtlasRevisionTests
{
    [TestMethod]
    [DataRow(0)]

    public async Task IndependentCallersConflictAndAtomicBatchRollsBack(int variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-revisions-" + Guid.NewGuid());
        try
        {
            await using var di = Setup.Create(root, variant);
            var repo = di.GetRequiredService<IRepository<IRecord>>();
            await repo.InitializeAsync();
            var a = await repo.SaveAsync(Setup.New(0, 10));
            var b = await repo.SaveAsync(Setup.New(1, 10));
            var snapshots = (ICommittedEnvelopeSnapshotSource<IRecord>)di.GetRequiredService<IEnvelopeRepository<IRecord>>();
            var snapshot = (await snapshots.CaptureCommittedAsync(a.Envelope!, int.MaxValue))!;
            var candidates = Enumerable.Range(0, 16).Select(i => { var item = snapshot.Materialize(); item.Value = i + 1; return item; }).ToArray();
            async Task<IRecord?> Try(IRecord item)
            {
                try { return await repo.SaveAsync(item); }
                catch (WriteConflictException<IRecord>) { return null; }
            }
            var result = await Task.WhenAll(candidates.Select(Try));
            Assert.AreEqual(1, result.Count(x => x is not null));
            var winner = result.Single(x => x is not null)!;
            Assert.AreEqual(winner.Value, (await repo.QueryFirstAsync(x => x.Id == a.Id))!.Value);
            var old = snapshot.Materialize(); b.Value = 99;
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveManyAsync([b, old]).AsTask());
            Assert.AreEqual(0, (await repo.QueryFirstAsync(x => x.Id == b.Id))!.Value);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.DeleteAsync(old).AsTask());
            var current = (await repo.QueryFirstAsync(x => x.Id == a.Id))!;
            await repo.DeleteAsync(current);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveAsync(winner).AsTask());
            var recreated = await repo.SaveAsync(Setup.New(0, 10));
            Assert.AreEqual(old.Envelope!.EnvelopeCacheKey, recreated.Envelope!.EnvelopeCacheKey);
            Assert.IsTrue(recreated.Envelope.RevisionTimestampUtcMicroseconds > winner.Envelope!.RevisionTimestampUtcMicroseconds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(0)]

    public async Task RevisionSurvivesAcknowledgementCheckpointCompactionBackupAndReopen(int variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-revision-recovery-" + Guid.NewGuid());
        var backup = root + "-backup";
        const long future = 1_000_000_000_000_000_000;
        try
        {
            await using (var di = Setup.Create(root, variant))
            {
                var repo = di.GetRequiredService<IEnvelopeMetadataRepository<IRecord>>();
                var imported = await repo.SaveWithExtensionMetadataAsync(Setup.New(1, 10), null, [1],
                    new EnvelopeWriteCondition(0, EnvelopeRevisionAction.Import, future));
                var key = imported.Envelope!.EnvelopeCacheKey;
                var acknowledged = await repo.SaveWithExtensionMetadataAsync(Setup.New(1, 10), key, [2],
                    new EnvelopeWriteCondition(future, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
                Assert.AreEqual(future, acknowledged.Envelope!.RevisionTimestampUtcMicroseconds);
                Assert.AreEqual(key, acknowledged.Envelope.EnvelopeCacheKey);
                CollectionAssert.AreEqual(new byte[] { 2 }, acknowledged.Envelope.ExtensionMetadata);
                var maintenance = di.GetRequiredService<IAtlasCoreRepositoryMaintenance<IRecord>>();
                await maintenance.CheckpointAsync(); await maintenance.CompactAsync();
                await maintenance.BackupAsync(backup);
                Assert.IsTrue(File.Exists(Path.Combine(backup, ".idb-revision-clock")));
            }
            await using var reopened = Setup.Create(root, variant);
            var repository = reopened.GetRequiredService<IRepository<IRecord>>();
            await repository.InitializeAsync();
            var read = (await repository.QueryFirstAsync(x => x.Value == 0))!;
            Assert.AreEqual(future, read.Envelope!.RevisionTimestampUtcMicroseconds);
            CollectionAssert.AreEqual(new byte[] { 2 }, read.Envelope.ExtensionMetadata);
            await repository.DeleteAsync(read);
            var replacement = await repository.SaveAsync(Setup.New(1, 10));
            Assert.IsTrue(replacement.Envelope!.RevisionTimestampUtcMicroseconds > future);
        }
        finally { foreach (var path in new[] { root, backup }) if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
