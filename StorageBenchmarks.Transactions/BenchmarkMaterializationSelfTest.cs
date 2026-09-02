using Idb.Libraries.Abstractions.Enums;

internal static class BenchmarkMaterializationSelfTest
{
    public static async Task RunAsync()
    {
        VerifyCodecAndDigest();
        VerifyQueryScheduleDigest();

        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbMaterializationSelfTest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        var source = Enumerable.Range(0, 64)
            .Select(static ordinal => GateRecord.Create(ordinal))
            .ToArray();
        var exactId = source[source.Length / 2].Id;

        var factories = new (string Name, Func<IProductionGateStore> Create)[]
        {
            (
                "InterfaceDB MetadataOnly",
                () => new InterfaceDbGateStore(
                    Path.Combine(runRoot, "interfacedb"),
                    StorageObjectCacheMode.MetadataOnly,
                    StorageDurabilityMode.Durable)),
            (
                "SQLite Microsoft.Data.Sqlite",
                () => new SqliteGateStore(
                    Path.Combine(runRoot, "sqlite-direct.db"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.MicrosoftDataSqlite)),
            (
                "SQLite Dapper",
                () => new SqliteGateStore(
                    Path.Combine(runRoot, "sqlite-dapper.db"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.Dapper))
        };

        try
        {
            foreach (var factory in factories)
            {
                await VerifyStoreAsync(factory.Create(), source, exactId).ConfigureAwait(false);
                Console.WriteLine($"Materialization contract passed: {factory.Name}");
            }
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void VerifyCodecAndDigest()
    {
        var sentinel = new GateRecord(
            "sentinel-id",
            "sentinel-name",
            47,
            "sentinel-role",
            "same-length-content-a",
            9_223_372_036_854_000L);
        var decoded = PhaseZeroCodec.Decode(PhaseZeroCodec.Encode(sentinel));
        if (decoded != sentinel)
        {
            throw new InvalidOperationException("PhaseZeroCodec did not materialize every GateRecord field.");
        }

        var sameLengthMutation = sentinel with { Content = "same-length-content-b" };
        if (sameLengthMutation.Content.Length != sentinel.Content.Length
            || sameLengthMutation.MaterializedChecksum == sentinel.MaterializedChecksum)
        {
            throw new InvalidOperationException(
                "The benchmark materialization digest did not detect a same-length field-content change.");
        }

        GC.KeepAlive(decoded);
    }

    private static void VerifyQueryScheduleDigest()
    {
        var first = new BenchmarkQueryDigest(2, 0x101UL, 0x202UL);
        var second = new BenchmarkQueryDigest(3, 0x303UL, 0x404UL);
        var forward = default(BenchmarkQueryDigest)
            .Combine(first, queryOrdinal: 0)
            .Combine(second, queryOrdinal: 1);
        var reversed = default(BenchmarkQueryDigest)
            .Combine(second, queryOrdinal: 0)
            .Combine(first, queryOrdinal: 1);

        if (forward == reversed)
        {
            throw new InvalidOperationException(
                "The benchmark query digest did not preserve per-query schedule boundaries.");
        }
    }

    private static async Task VerifyStoreAsync(
        IProductionGateStore store,
        IReadOnlyList<GateRecord> source,
        string exactId)
    {
        await using (store.ConfigureAwait(false))
        {
            await store.InitializeAsync().ConfigureAwait(false);
            await store.SeedAsync(source).ConfigureAwait(false);
            await VerifySnapshotAsync(store, source, "after seed").ConfigureAwait(false);
            await VerifyQueriesAsync(store, source, exactId, "after seed").ConfigureAwait(false);

            await store.ReopenAsync().ConfigureAwait(false);
            await VerifySnapshotAsync(store, source, "after reopen").ConfigureAwait(false);
            await VerifyQueriesAsync(store, source, exactId, "after reopen").ConfigureAwait(false);
        }
    }

    private static async Task VerifyQueriesAsync(
        IProductionGateStore store,
        IReadOnlyList<GateRecord> source,
        string exactId,
        string phase)
    {
        foreach (var queryKind in Enum.GetValues<GateQueryKind>())
        {
            var expected = ExpectedQuery(source, queryKind, exactId);
            var actual = await store.QueryAsync(queryKind, exactId).ConfigureAwait(false);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{store.EngineName} {queryKind} {phase} materialized {actual.Count}/{actual.Checksum}; "
                    + $"expected {expected.Count}/{expected.Checksum}.");
            }
        }
    }

    private static async Task VerifySnapshotAsync(
        IProductionGateStore store,
        IReadOnlyList<GateRecord> source,
        string phase)
    {
        var snapshot = await store.SnapshotAsync().ConfigureAwait(false);
        if (snapshot.Count != source.Count)
        {
            throw new InvalidOperationException(
                $"{store.EngineName} snapshot {phase} contained {snapshot.Count} objects; expected {source.Count}.");
        }

        foreach (var expected in source)
        {
            if (!snapshot.TryGetValue(expected.Id, out var actual) || actual != expected)
            {
                throw new InvalidOperationException(
                    $"{store.EngineName} snapshot {phase} did not materialize every field for '{expected.Id}'.");
            }
        }

        GC.KeepAlive(snapshot);
    }

    private static GateQueryResult ExpectedQuery(
        IEnumerable<GateRecord> source,
        GateQueryKind queryKind,
        string exactId)
    {
        var materialized = source.Where(record => queryKind switch
        {
            GateQueryKind.Exact => record.Id == exactId,
            GateQueryKind.Range => record.Age >= 75,
            GateQueryKind.Prefix => record.Name.StartsWith("customer-00000", StringComparison.Ordinal),
            GateQueryKind.Suffix => record.Name.EndsWith("special-tail", StringComparison.Ordinal),
            GateQueryKind.Contains => record.Content.Contains("rare-needle", StringComparison.Ordinal),
            GateQueryKind.BroadConjunction => record.Role == "student" && record.Age >= 40,
            _ => throw new ArgumentOutOfRangeException(nameof(queryKind), queryKind, null)
        }).ToArray();

        var checksum = materialized.Sum(static record => record.MaterializedChecksum);
        GC.KeepAlive(materialized);
        return new GateQueryResult(materialized.Length, checksum);
    }
}
