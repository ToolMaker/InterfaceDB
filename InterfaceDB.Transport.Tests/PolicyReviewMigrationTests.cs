using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private const string ReviewClockKey = ".idb-remote-repository-clock-v1";
    private static readonly string ReviewNestedLogicalKey = DataAccessScope.Partitioned("nested-key", "ordinary").Identity + "item";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolicyReview_MigrationPreservesMixedScopesClocksAndFailedUpgradeRecovery(bool adoptLegacy)
    {
        var root = Path.Combine(_root, "mixed-master");
        Dictionary<string, byte[]> original;
        if (adoptLegacy)
        {
            var legacyPath = Path.Combine(_root, "mixed-legacy");
            await using (var legacy = await SegmentCoreEncodedEnvelopeStore.OpenAsync(legacyPath))
            {
                await SeedReviewMigrationScopesAsync(legacy);
                original = await ReadReviewPhysicalRecordsAsync(legacy);
            }
            await using var adopted = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1()],
                legacyStoreDirectory: legacyPath, legacyManifest: Manifest());
            AssertReviewRecordsEqual(original, await ReadReviewPhysicalRecordsAsync(adopted.Registration.Store));
        }
        else
        {
            await using var master = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1()]);
            await SeedReviewMigrationScopesAsync(master.Registration.Store);
            original = await ReadReviewPhysicalRecordsAsync(master.Registration.Store);
        }

        Assert.AreEqual(7, original.Count, "Four domain records and three independent repository clocks must exist.");
        Assert.AreEqual(3, original.Keys.Count(key => key.EndsWith(ReviewClockKey, StringComparison.Ordinal)));
        await using (var reopened = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1()]))
            AssertReviewRecordsEqual(original, await ReadReviewPhysicalRecordsAsync(reopened.Registration.Store));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var failed = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1(), StoreV2(fail: true)]);
        });
        await using (var unchanged = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1()]))
        {
            Assert.AreEqual("1.0.0", unchanged.RunningVersion);
            AssertReviewRecordsEqual(original, await ReadReviewPhysicalRecordsAsync(unchanged.Registration.Store));
        }

        await using (var upgraded = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1(), StoreV2()]))
        {
            Assert.AreEqual("2.0.0", upgraded.RunningVersion);
            var records = await ReadReviewPhysicalRecordsAsync(upgraded.Registration.Store);
            CollectionAssert.AreEquivalent(original.Keys.ToArray(), records.Keys.ToArray());
            foreach (var (key, beforeBytes) in original)
            {
                if (key.EndsWith(ReviewClockKey, StringComparison.Ordinal))
                {
                    CollectionAssert.AreEqual(beforeBytes, records[key], "Migration must preserve each scope's revision clock.");
                    continue;
                }
                var before = Serializer().Deserialize(beforeBytes);
                var after = Serializer().Deserialize(records[key]);
                Assert.AreEqual("2.0.0", after.VersionTag);
                Assert.AreEqual(before.ObjectInstance!.Score + 10, after.ObjectInstance!.Score);
                Assert.AreEqual(before.RevisionTimestampUtcMicroseconds, after.RevisionTimestampUtcMicroseconds);
            }
        }

        await using var active = await VersionedTcpStore.OpenAsync(root, "people", [StoreV1(), StoreV2()]);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(active.Registration.Store) with
            { Stores = [active.Registration], DefaultAccessPolicy = ReviewMigrationPolicy() });
        foreach (var (subject, score) in new[] { ("shared", 11), ("alice", 12), ("bob", 13) })
        {
            var options = Options(host, Token(subject));
            await using var client = new TcpRepository<IRemotePerson>(options with
            {
                Contract = new VersionPersonConfig("2.0.0"),
                Connection = options.Connection with { Manifest = Manifest2() }
            });
            Assert.AreEqual(score, (await client.QuerySingleAsync(person => person.Name == "same-key")).Score);
            var records = new List<IRemotePerson>();
            await foreach (var record in client.QueryManyAsync(_ => true)) records.Add(record);
            Assert.AreEqual(subject == "alice" ? 2 : 1, records.Count);
            if (subject == "alice")
                Assert.AreEqual(14, (await client.QuerySingleAsync(person => person.Name == ReviewNestedLogicalKey)).Score);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolicyReview_ReopenStillValidatesTenantPayloadsAndClocks(bool corruptClock)
    {
        var root = Path.Combine(_root, "tenant-validation");
        var strict = new TcpStoreVersion(Manifest(), null, (_, bytes) =>
        {
            if (bytes.Length != 1 || bytes[0] != 1) throw new InvalidDataException("Invalid version payload.");
        });
        await using (var master = await VersionedTcpStore.OpenAsync(root, "people", [strict])) { }

        var generation = Directory.GetDirectories(root).Single();
        var partition = DataAccessScope.Partitioned(Issuer, "alice").Partition;
        await using (var raw = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(generation, "data")))
            await raw.ExecuteAsync(new()
            {
                Command = EncodedStoreCommand.Put, Partition = partition,
                Key = partition + (corruptClock ? ReviewClockKey : "persisted-invalid"), Payload = [2]
            });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var invalid = await VersionedTcpStore.OpenAsync(root, "people", [strict]);
        });
    }

    private static IDataAccessPolicy ReviewMigrationPolicy()
    {
        var subject = new SubjectDataAccessPolicy();
        return new DelegateDataAccessPolicy(context => context.Identity?.Principal.FindFirst("sub")?.Value == "shared"
            ? DataAccessScope.Unrestricted : subject.Authorize(context));
    }

    private async Task SeedReviewMigrationScopesAsync(IEncodedEnvelopeStore store)
    {
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = ReviewMigrationPolicy() });
        foreach (var (subject, score) in new[] { ("shared", 1), ("alice", 2), ("bob", 3) })
        {
            await using var client = Client(host, subject);
            await Seed(client, "same-key", score);
            if (subject == "alice") await Seed(client, ReviewNestedLogicalKey, 4);
        }
    }

    private static async Task<Dictionary<string, byte[]>> ReadReviewPhysicalRecordsAsync(IEncodedEnvelopeStore store)
    {
        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            // Trusted offline lifecycle enumeration must remain distinct from a TCP client's view.
            var page = await store.EnumerateAsync(1, after);
            foreach (var record in page.Records!) records.Add(record.Key, record.Payload);
            after = page.NextAfterKey;
        } while (after is not null);
        return records;
    }

    private static void AssertReviewRecordsEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var (key, bytes) in expected) CollectionAssert.AreEqual(bytes, actual[key]);
    }
}
