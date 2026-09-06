using System.Diagnostics;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

if (args is ["--child", var dataRoot, var gatePath, var readyPath, var writerName])
{
    await RunChildWriterAsync(dataRoot, gatePath, readyPath, writerName).ConfigureAwait(false);
    return Environment.ExitCode;
}

if (args is ["--child-operation", var operationRoot, var operationGate, var operationReady, var operation])
{
    await RunChildOperationAsync(
        operationRoot,
        operationGate,
        operationReady,
        operation).ConfigureAwait(false);
    return Environment.ExitCode;
}

if (args is ["--child-abandon-migration", var migrationRoot, var migrationReady])
{
    await RunChildAbandonedMigrationProposalAsync(migrationRoot, migrationReady).ConfigureAwait(false);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("single save cleans write artifacts", SingleSaveCleansSidecarArtifactsAsync),
    ("delete is idempotent and prevents stale object resurrection", DeleteIsIdempotentAndPreventsResurrectionAsync),
    ("initialization recovers an abandoned delete proposal", InitializationRecoversAbandonedDeleteAsync),
    ("single cardinality rejects detached duplicates and permits loaded updates", SingleCardinalityRejectsDetachedDuplicatesAsync),
    ("single cardinality is atomic across repository instances", SingleCardinalityIsAtomicAcrossRepositoriesAsync),
    ("multiple cardinality creates distinct physical objects", MultipleCardinalityCreatesDistinctObjectsAsync),
    ("multiple cardinality allocates incremented cache keys", MultipleCardinalityAllocatesIncrementedCacheKeysAsync),
    ("repository storage-key equality queries use envelope groups", RepositoryStorageKeyEqualityQueriesUseEnvelopeGroupsAsync),
    ("repository index manager selection follows contract search metadata", RepositoryIndexManagerSelectionFollowsContractMetadataAsync),
    ("repository search indexes support exact range prefix suffix contains and maintenance", RepositorySearchIndexesSupportQueriesAsync),
    ("repository query cardinality methods enforce result bounds", RepositoryQueryCardinalityMethodsAsync),
    ("repository operations honor their public contracts", RepositoryOperationsHonorContractsAsync),
    ("repository bulk save persists canonical objects in input order", RepositoryBulkSavePersistsInInputOrderAsync),
    ("repository bulk save reports partial failures", RepositoryBulkSaveReportsPartialFailuresAsync),
    ("repository keeps envelope metadata but does not cache objects by default", RepositoryDoesNotCacheObjectInstancesByDefaultAsync),
    ("repository weak object cache reuses live objects without strong retention", RepositoryWeakObjectCacheUsesWeakReferenceAsync),
    ("repository caches objects when concrete type is marked", RepositoryCachesStorageCachedObjectsAsync),
    ("repository caches objects when envelope flag is persisted", RepositoryCachesEnvelopeFlaggedObjectsAsync),
    ("repository registration storage policy overrides config policy", RepositoryRegistrationStoragePolicyOverridesConfigAsync),
    ("repository registration storage identity factory controls file identity", RepositoryRegistrationStorageIdentityFactoryControlsFileIdentityAsync),
    ("default identity factory uses guid v7 file keys", DefaultIdentityFactoryUsesGuidV7KeysAsync),
    ("sequential identity factories create filename keys", SequentialIdentityFactoriesCreateFilenameKeysAsync),
    ("watcher delivers local notifications at least once", WatcherDeliversLocalNotificationsAtLeastOnceAsync),
    ("watcher synchronizes external create update and delete", WatcherSynchronizesExternalChangesAsync),
    ("watcher converges after rename delete race", WatcherConvergesAfterRenameDeleteRaceAsync),
    ("watcher rescan recovers missed storage changes", WatcherRescanRecoversMissedChangesAsync),
    ("keyed DI isolates databases using the same contract", KeyedDependencyInjectionIsolatesDatabasesAsync),
    ("keyed DI duplicate registration is idempotent or rejected", KeyedDependencyInjectionHandlesDuplicatesAsync),
    ("two processes can write the same payload", TwoProcessesWriteSamePayloadAsync),
    ("cross-process write and delete reject stale revisions", CrossProcessWriteDeleteRejectsStaleRevisionsAsync),
    ("delete lock timeout maps to storage concurrency exception", DeleteLockTimeoutMapsToStorageConcurrencyExceptionAsync),
    ("startup recovers migration proposal abandoned by a terminated process", StartupRecoversTerminatedMigrationProposalAsync),
    ("stale writes reject a recovered legacy proposal", LosingProposalReturnsAndCachesCanonicalWinnerAsync),
    ("initialization commits newest abandoned proposal", InitializationCommitsNewestAbandonedProposalAsync),
    ("eager migration drains proposals before migrating", EagerMigrationDrainsProposalsBeforeMigratingAsync),
    ("multi-step migration reaches the active version", MultiStepMigrationReachesActiveVersionAsync),
    ("file migration rejects oversized extension metadata", FileMigrationRejectsOversizedExtensionMetadataAsync),
    ("strict migration rejects a missing chain link", StrictMigrationRejectsMissingLinkAsync),
    ("migration registration rejects ambiguous source versions", MigrationRegistrationRejectsAmbiguousSourcesAsync),
    ("migration registration rejects backward definitions", MigrationRegistrationRejectsBackwardDefinitionsAsync),
    ("strict migration preserves legacy storage when migration code fails", StrictMigrationPreservesLegacyStorageWhenCodeFailsAsync),
    ("tolerant migration preserves legacy storage when a link is missing", TolerantMigrationPreservesLegacyStorageAsync),
    ("lazy migration upgrades raw storage on first query", LazyMigrationUpgradesOnFirstQueryAsync),
    ("encrypted storage round trips and rejects tampering", EncryptedStorageRoundTripsAndRejectsTamperingAsync),
    ("encrypted storage lazily rotates directly to the active key", EncryptedStorageLazilyRotatesToActiveKeyAsync),
    ("encrypted storage fields decrypt through the contract proxy", EncryptedStorageFieldsDecryptThroughProxyAsync),
    ("forced encryption rotation rewrites the full store", ForcedEncryptionRotationRewritesStoreAsync),
    ("legacy encrypted payloads use the configured historical key", LegacyEncryptedPayloadUsesConfiguredKeyAsync),
    ("Windows Credential Manager stores active and historical keys", WindowsCredentialManagerStoresKeyRingAsync),
    ("encrypted eager migration drains and migrates proposals", EncryptedEagerMigrationDrainsAndMigratesProposalsAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex}");
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} test(s) failed.");
    return 1;
}

Console.WriteLine("All integration tests passed.");
return 0;

static async Task SingleSaveCleansSidecarArtifactsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var record = new LockTestRecord { Id = "single", Value = "first-write" };
        await repository.SaveAsync(record).ConfigureAwait(false);

        Assert(record.Envelope is not null, "Saved record should have an envelope.");
        Assert(File.Exists(record.Envelope!.Location), "Payload file should exist after save.");
        AssertNoSidecarArtifacts(testRoot);

        var reloaded = await repository.QueryAsync(x => x.Id == "single").ConfigureAwait(false);
        reloaded = AssertNotNull(reloaded, "Saved record should be queryable.");
        Assert(reloaded.Value == "first-write", "Reloaded value should match saved value.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task DeleteIsIdempotentAndPreventsResurrectionAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var record = await repository.SaveAsync(
            new LockTestRecord { Id = "delete", Value = "stored" }).ConfigureAwait(false);
        var payloadPath = AssertNotNull(record.Envelope?.Location, "Saved record should have a provider locator.");

        var deleted = await repository.DeleteAsync(record).ConfigureAwait(false);
        Assert(deleted, "Deleting an active storage object should return true.");
        Assert(!File.Exists(payloadPath), "The canonical payload should be removed.");
        Assert(record.Envelope?.State == StorageObjectState.Deleted, "The envelope should be marked deleted.");
        Assert(
            await repository.QueryFirstAsync(x => x.Id == "delete").ConfigureAwait(false) is null,
            "A deleted object should no longer be queryable.");

        var deletedAgain = await repository.DeleteAsync(record).ConfigureAwait(false);
        Assert(!deletedAgain, "Repeated deletion should be idempotent and return false.");

        await AssertThrowsAsync<StorageObjectDeletedException>(
            async () => await repository.SaveAsync(record).ConfigureAwait(false),
            "A deleted in-memory object should not be able to recreate its storage implicitly.").ConfigureAwait(false);

        await AssertThrowsAsync<StorageObjectNotPersistedException>(
            async () => await repository.DeleteAsync(
                new LockTestRecord { Id = "detached", Value = "none" }).ConfigureAwait(false),
            "An envelope-less object should not be deletable.").ConfigureAwait(false);

        var invalid = await repository.SaveAsync(
            new LockTestRecord { Id = "invalid-delete", Value = "stored" }).ConfigureAwait(false);
        var validLocation = AssertNotNull(invalid.Envelope?.Location, "Saved record should have a provider locator.");
        invalid.Envelope!.Location = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mpk");
        await AssertThrowsAsync<InvalidStorageEnvelopeException>(
            async () => await repository.DeleteAsync(invalid).ConfigureAwait(false),
            "The file provider should reject a locator outside its configured storage root.").ConfigureAwait(false);
        invalid.Envelope.Location = validLocation;
        await repository.DeleteAsync(invalid).ConfigureAwait(false);

        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task InitializationRecoversAbandonedDeleteAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string payloadPath;
        await using (var writerProvider = BuildProvider(testRoot))
        {
            var repository = writerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);
            var record = await repository.SaveAsync(
                new LockTestRecord { Id = "recover-delete", Value = "stored" }).ConfigureAwait(false);
            payloadPath = AssertNotNull(record.Envelope?.Location, "Saved record should have a provider locator.");
        }

        var ticks = DateTimeOffset.UtcNow.UtcTicks.ToString("D19");
        var deleteProposal = payloadPath + ".pending." + ticks + "." + Guid.NewGuid().ToString("N") + ".delete.ready";
        await File.WriteAllBytesAsync(deleteProposal, Array.Empty<byte>()).ConfigureAwait(false);

        await using var recoveryProvider = BuildProvider(testRoot);
        var recoveryRepository = recoveryProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await recoveryRepository.InitializeAsync().ConfigureAwait(false);

        Assert(!File.Exists(payloadPath), "Startup recovery should commit the abandoned delete proposal.");
        Assert(!File.Exists(deleteProposal), "Startup recovery should consume the delete proposal.");
        Assert(
            await recoveryRepository.QueryFirstAsync(x => true).ConfigureAwait(false) is null,
            "Deleted storage should not be loaded into the recovered repository.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task SingleCardinalityRejectsDetachedDuplicatesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildSingleProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var first = await repository.SaveAsync(
            new LockTestRecord { Id = "singleton", Value = "first" }).ConfigureAwait(false);
        Assert(first.Envelope?.EnvelopeCacheKey == "singleton", "The custom factory should own the single storage key.");

        await AssertThrowsAsync<StorageObjectAlreadyExistsException>(
            async () => await repository.SaveAsync(
                new LockTestRecord { Id = "singleton", Value = "duplicate" }).ConfigureAwait(false),
            "A detached duplicate single object should be rejected.").ConfigureAwait(false);

        first.Value = "updated";
        var updated = await repository.SaveAsync(first).ConfigureAwait(false);
        Assert(updated.Value == "updated", "A loaded single object should update its existing payload.");

        var payloadFiles = Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories);
        Assert(payloadFiles.Length == 1, $"Single cardinality should have one payload, found {payloadFiles.Length}.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task SingleCardinalityIsAtomicAcrossRepositoriesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var providerA = BuildSingleProvider(testRoot);
        await using var providerB = BuildSingleProvider(testRoot);
        var repositoryA = providerA.GetRequiredService<IRepository<ILockTestRecord>>();
        var repositoryB = providerB.GetRequiredService<IRepository<ILockTestRecord>>();
        await repositoryA.InitializeAsync().ConfigureAwait(false);
        await repositoryB.InitializeAsync().ConfigureAwait(false);

        var attempts = await Task.WhenAll(
            TrySaveAsync(repositoryA, new LockTestRecord { Id = "shared-single", Value = "a" }),
            TrySaveAsync(repositoryB, new LockTestRecord { Id = "shared-single", Value = "b" })).ConfigureAwait(false);

        Assert(attempts.Count(result => result) == 1, "Exactly one concurrent single-object creation should succeed.");
        var payloadFiles = Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories);
        Assert(payloadFiles.Length == 1, $"Concurrent single creation should leave one payload, found {payloadFiles.Length}.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task MultipleCardinalityCreatesDistinctObjectsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var first = await repository.SaveAsync(
            new LockTestRecord { Id = "same-domain-value", Value = "first" }).ConfigureAwait(false);
        var second = await repository.SaveAsync(
            new LockTestRecord { Id = "same-domain-value", Value = "second" }).ConfigureAwait(false);

        Assert(
            !string.Equals(first.Envelope?.EnvelopeCacheKey, second.Envelope?.EnvelopeCacheKey, StringComparison.Ordinal),
            "Multiple cardinality should resolve a unique envelope identity for each new object.");
        var payloadFiles = Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories);
        Assert(payloadFiles.Length == 2, $"Multiple cardinality should create two payloads, found {payloadFiles.Length}.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task MultipleCardinalityAllocatesIncrementedCacheKeysAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var options = new LockTestOptions(testRoot);
        var services = new ServiceCollection();
        services.AddRepository<ILockTestRecord>(
            options,
            storageIdentityFactory: new PrefixStorageIdentityFactory(options, "person"));

        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var first = await repository.SaveAsync(
            new LockTestRecord { Id = "john-smith", Value = "first" }).ConfigureAwait(false);
        var second = await repository.SaveAsync(
            new LockTestRecord { Id = "john-smith", Value = "second" }).ConfigureAwait(false);

        Assert(first.Envelope?.EnvelopeCacheKey == "person-john-smith-0",
            $"Expected first cache key 'person-john-smith-0', found '{first.Envelope?.EnvelopeCacheKey}'.");
        Assert(second.Envelope?.EnvelopeCacheKey == "person-john-smith-1",
            $"Expected second cache key 'person-john-smith-1', found '{second.Envelope?.EnvelopeCacheKey}'.");
        Assert(File.Exists(first.Envelope!.Location), "First physical payload should exist.");
        Assert(File.Exists(second.Envelope!.Location), "Second physical payload should exist.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryStorageKeyEqualityQueriesUseEnvelopeGroupsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var options = new LockTestOptions(testRoot);
        var services = new ServiceCollection();
        services.AddRepository<ILockTestRecord>(
            options,
            storageIdentityFactory: new PrefixStorageIdentityFactory(options, "person"));

        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        await repository.SaveAsync(new LockTestRecord { Id = "john-smith", Value = "first" }).ConfigureAwait(false);
        await repository.SaveAsync(new LockTestRecord { Id = "john-smith", Value = "second" }).ConfigureAwait(false);
        await repository.SaveAsync(new LockTestRecord { Id = "jane-smith", Value = "other" }).ConfigureAwait(false);

        var matches = new List<ILockTestRecord>();
        await foreach (var record in repository.QueryManyByStorageKeyAsync("person-john-smith"))
        {
            matches.Add(record);
        }

        Assert(matches.Count == 2, $"Expected two records in the john-smith storage-key group, found {matches.Count}.");
        Assert(matches.All(x => x.Id == "john-smith"), "Storage-key equality lookup should only return the requested logical group.");

        var firstFromLogicalKey = await repository
            .QueryFirstByStorageKeyAsync("person-john-smith")
            .ConfigureAwait(false);
        firstFromLogicalKey = AssertNotNull(firstFromLogicalKey, "Storage-key equality lookup should return the first group member.");
        Assert(firstFromLogicalKey.Envelope?.EnvelopeCacheKey == "person-john-smith-0",
            $"Expected first physical key 'person-john-smith-0', found '{firstFromLogicalKey.Envelope?.EnvelopeCacheKey}'.");

        var fromPhysicalKey = await repository
            .QueryFirstByStorageKeyAsync("person-john-smith-1")
            .ConfigureAwait(false);
        fromPhysicalKey = AssertNotNull(fromPhysicalKey, "Physical key input should resolve to the logical equality group.");
        Assert(fromPhysicalKey.Id == "john-smith", "Physical key normalization should still resolve the logical group.");

        var missing = await repository
            .QueryFirstByStorageKeyAsync("person-missing")
            .ConfigureAwait(false);
        Assert(missing is null, "Missing storage-key equality lookup should return null.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryIndexManagerSelectionFollowsContractMetadataAsync()
{
    var plainRoot = CreateTestRoot();
    var indexedRoot = CreateTestRoot();
    try
    {
        await using (var plainProvider = BuildProvider(plainRoot))
        {
            var manager = plainProvider.GetRequiredService<IIndexManager<ILockTestRecord>>();
            Assert(
                manager is PassthroughIndexManager<ILockTestRecord>,
                $"Non-indexed contracts should use {nameof(PassthroughIndexManager<ILockTestRecord>)}.");
        }

        var services = new ServiceCollection();
        services.AddRepository<IIndexedTestRecord>(new IndexedTestOptions(indexedRoot));
        await using (var indexedProvider = services.BuildServiceProvider())
        {
            var manager = indexedProvider.GetRequiredService<IIndexManager<IIndexedTestRecord>>();
            Assert(
                manager is SearchIndexManager<IIndexedTestRecord>,
                $"Indexed contracts should use {nameof(SearchIndexManager<IIndexedTestRecord>)}.");
        }
    }
    finally
    {
        TryDeleteDirectory(plainRoot);
        TryDeleteDirectory(indexedRoot);
    }
}

static async Task RepositorySearchIndexesSupportQueriesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var services = new ServiceCollection();
        services.AddRepository<IIndexedTestRecord>(new IndexedTestOptions(testRoot));

        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IRepository<IIndexedTestRecord>>();
        var indexManager = provider.GetRequiredService<IIndexManager<IIndexedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var records = new IIndexedTestRecord[]
        {
            new IndexedTestRecord { Id = "a", Name = "Alice", Age = 81, Country = "ZA", Phone = "+27820000001", Address = new IndexedAddress { City = "Cape Town", Code = "8001" } },
            new IndexedTestRecord { Id = "b", Name = "Bob", Age = 40, Country = "ZA", Phone = "+27830000002", Address = new IndexedAddress { City = "Durban", Code = "4001" } },
            new IndexedTestRecord { Id = "c", Name = "Charlie", Age = 90, Country = "US", Phone = "+12020000003", Address = new IndexedAddress { City = "Austin", Code = "73301" } },
            new IndexedTestRecord { Id = "d", Name = "Alicia", Age = 32, Country = "UK", Phone = "+447700000004", Address = new IndexedAddress { City = "London", Code = "SW1" } }
        };

        await repository.SaveManyAsync(records).ConfigureAwait(false);

        var explanation = indexManager.ExplainSearch(
            x => x.Country == "ZA" && x.Age > 80);
        Assert(explanation.UsesIndexes,
            "ExplainSearch should report index use for indexed predicates.");
        Assert(explanation.EstimatedCandidateCount.HasValue,
            "ExplainSearch should provide a candidate estimate for indexed predicates.");
        Assert(explanation.PlanText.Contains("index", StringComparison.OrdinalIgnoreCase),
            "ExplainSearch should identify the selected index access path.");

        var exact = await CollectAsync(repository.SearchManyAsync(x => x.Country == "ZA")).ConfigureAwait(false);
        Assert(exact.Count == 2, $"Exact index should return two ZA records, found {exact.Count}.");

        var range = await CollectAsync(repository.SearchManyAsync(x => x.Age > 80)).ConfigureAwait(false);
        Assert(range.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["a", "c"]),
            "Range index should return records older than 80.");

        var inclusiveRange = await CollectAsync(repository.SearchManyAsync(x => x.Age >= 81)).ConfigureAwait(false);
        Assert(inclusiveRange.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["a", "c"]),
            "Inclusive range index should include the lower boundary.");

        var upperRange = await CollectAsync(repository.SearchManyAsync(x => x.Age <= 40)).ConfigureAwait(false);
        Assert(upperRange.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["b", "d"]),
            "Range index should support inclusive upper boundaries.");

        var reversedRange = await CollectAsync(repository.SearchManyAsync(x => 80 < x.Age)).ConfigureAwait(false);
        Assert(reversedRange.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["a", "c"]),
            "Range index should reverse comparisons with the indexed member on the right.");

        var boundedRange = await CollectAsync(repository.SearchManyAsync(x => x.Age >= 40 && x.Age < 90)).ConfigureAwait(false);
        Assert(boundedRange.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["a", "b"]),
            "Range index should combine lower and upper bounds on the same indexed member.");

        var prefix = await CollectAsync(repository.SearchManyAsync(x => x.Phone.StartsWith("+27", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert(prefix.Count == 2 && prefix.All(x => x.Country == "ZA"),
            "Prefix index should return South African phone numbers.");

        var suffix = await CollectAsync(repository.SearchManyAsync(x => x.Phone.EndsWith("000001", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert(suffix.Count == 1 && suffix[0].Id == "a",
            "Suffix index should return the record whose phone number has the requested ending.");

        var contains = await CollectAsync(repository.SearchManyAsync(x => x.Name.Contains("lic"))).ConfigureAwait(false);
        Assert(contains.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["a", "d"]),
            "Contains index should return exact ordinal substring matches.");

        var ordinalContains = await CollectAsync(repository.SearchManyAsync(
            x => x.Name.Contains("har", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert(ordinalContains.Count == 1 && ordinalContains[0].Id == "c",
            "Contains index should support the explicit ordinal overload.");

        var negativeContains = await CollectAsync(repository.SearchManyAsync(
            x => x.Country == "ZA" && !x.Name.Contains("Alice"))).ConfigureAwait(false);
        Assert(negativeContains.Count == 1 && negativeContains[0].Id == "b",
            "Negative contains should use a positive index candidate and remain a residual predicate.");

        var nested = await CollectAsync(repository.SearchManyAsync(x => x.Address.City == "Cape Town")).ConfigureAwait(false);
        Assert(nested.Count == 1 && nested[0].Id == "a",
            "Nested indexed property should resolve Address.City.");

        var andQuery = await CollectAsync(repository.SearchManyAsync(x => x.Age > 80 && x.Country == "ZA")).ConfigureAwait(false);
        Assert(andQuery.Count == 1 && andQuery[0].Id == "a",
            "AND query should intersect indexed results.");

        var orQuery = await CollectAsync(repository.SearchManyAsync(x => x.Country == "UK" || x.Age > 85)).ConfigureAwait(false);
        Assert(orQuery.Select(x => x.Id).OrderBy(static x => x).SequenceEqual(["c", "d"]),
            "OR query should union indexed results.");

        records[0].Country = "NL";
        records[0].Name = "Eve";
        records[0].Phone = "+31299999999";
        await repository.SaveAsync(records[0]).ConfigureAwait(false);
        var afterUpdate = await CollectAsync(repository.SearchManyAsync(x => x.Country == "ZA")).ConfigureAwait(false);
        Assert(afterUpdate.Count == 1 && afterUpdate[0].Id == "b",
            "Index update should remove old values when an existing object is saved.");

        var prefixAfterUpdate = await CollectAsync(repository.SearchManyAsync(x => x.Phone.StartsWith("+27"))).ConfigureAwait(false);
        Assert(prefixAfterUpdate.Count == 1 && prefixAfterUpdate[0].Id == "b",
            "Radix prefix updates should remove the old prefix and index the new value.");

        var suffixAfterUpdate = await CollectAsync(repository.SearchManyAsync(x => x.Phone.EndsWith("000001", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert(suffixAfterUpdate.Count == 0,
            "Radix suffix updates should remove the old reversed key.");

        var containsAfterUpdate = await CollectAsync(repository.SearchManyAsync(x => x.Name.Contains("lic"))).ConfigureAwait(false);
        Assert(containsAfterUpdate.Count == 1 && containsAfterUpdate[0].Id == "d",
            "Contains index updates should remove the previous searchable text.");

        await repository.DeleteAsync(records[1]).ConfigureAwait(false);
        var afterDelete = await CollectAsync(repository.SearchManyAsync(x => x.Country == "ZA")).ConfigureAwait(false);
        Assert(afterDelete.Count == 0, "Index delete should remove deleted objects.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryQueryCardinalityMethodsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        await repository.SaveAsync(new LockTestRecord { Id = "group", Value = "one" }).ConfigureAwait(false);
        await repository.SaveAsync(new LockTestRecord { Id = "group", Value = "two" }).ConfigureAwait(false);
        await repository.SaveAsync(new LockTestRecord { Id = "unique", Value = "only" }).ConfigureAwait(false);

        var many = new List<ILockTestRecord>();
        await foreach (var record in repository.QueryManyAsync(x => x.Id == "group"))
        {
            many.Add(record);
        }
        Assert(many.Count == 2, $"QueryManyAsync should return two matches, found {many.Count}.");

        var first = await repository.QueryFirstAsync(x => x.Id == "group").ConfigureAwait(false);
        Assert(first is not null && first.Id == "group", "QueryFirstAsync should return the first available match.");

        var missingFirst = await repository.QueryFirstAsync(x => x.Id == "missing").ConfigureAwait(false);
        Assert(missingFirst is null, "QueryFirstAsync should return null when there are no matches.");

        var single = await repository.QuerySingleAsync(x => x.Id == "unique").ConfigureAwait(false);
        Assert(single.Value == "only", "QuerySingleAsync should return the only match.");

        await AssertThrowsAsync<RepositoryQueryCardinalityException>(
            async () => await repository.QuerySingleAsync(x => x.Id == "missing").ConfigureAwait(false),
            "QuerySingleAsync should reject no matches.").ConfigureAwait(false);
        await AssertThrowsAsync<RepositoryQueryCardinalityException>(
            async () => await repository.QuerySingleAsync(x => x.Id == "group").ConfigureAwait(false),
            "QuerySingleAsync should reject multiple matches.").ConfigureAwait(false);

        var optional = await repository.QuerySingleOrDefaultAsync(x => x.Id == "missing").ConfigureAwait(false);
        Assert(optional is null, "QuerySingleOrDefaultAsync should return null when there are no matches.");

        await AssertThrowsAsync<CustomQueryException>(
            async () => await repository.QuerySingleAsync(
                x => x.Id == "missing",
                cancellationToken: default,
                noneExceptionFactory: () => new CustomQueryException("none"),
                multipleExceptionFactory: null).ConfigureAwait(false),
            "QuerySingleAsync should use the supplied no-match exception factory.").ConfigureAwait(false);

        await AssertThrowsAsync<CustomQueryException>(
            async () => await repository.QuerySingleOrDefaultAsync(
                x => x.Id == "group",
                cancellationToken: default,
                multipleExceptionFactory: () => new CustomQueryException("many")).ConfigureAwait(false),
            "QuerySingleOrDefaultAsync should use the supplied multiple-match exception factory.").ConfigureAwait(false);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryOperationsHonorContractsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        var envelopeRepository = provider.GetRequiredService<IEnvelopeRepository<ILockTestRecord>>();

        Assert(
            repository.RepositoryType == ContractRepositoryType.FileSystem,
            "The repository should expose its configured provider type.");

        await repository.InitializeAsync().ConfigureAwait(false);
        await repository.InitializeAsync().ConfigureAwait(false);

        var detached = new LockTestRecord { Id = "detached", Value = "not-stored" };
        Assert(
            !await repository.ExistsAsync(detached).ConfigureAwait(false),
            "ExistsAsync should return false for an envelope-less object.");

        var stored = await repository.SaveAsync(
            new LockTestRecord { Id = "operations", Value = "initial" }).ConfigureAwait(false);
        Assert(
            await repository.ExistsAsync(stored).ConfigureAwait(false),
            "ExistsAsync should return true for a cached persisted object.");

        var optional = await repository
            .QuerySingleOrDefaultAsync(x => x.Id == "operations")
            .ConfigureAwait(false);
        Assert(
            ReferenceEquals(optional, stored),
            "QuerySingleOrDefaultAsync should return the single matching stored instance.");

        var missingCompatibilityQuery = await repository
            .QueryAsync(x => x.Id == "missing")
            .ConfigureAwait(false);
        Assert(
            missingCompatibilityQuery is null,
            "The compatibility QueryAsync method should return null when no object matches.");

        var originalPath = AssertNotNull(stored.Envelope?.Location, "Stored object should have a provider locator.");
        stored.Value = "updated";
        var updated = await repository.SaveAsync(stored).ConfigureAwait(false);
        Assert(updated.Value == "updated", "Saving a storage-aware object should update its current payload.");
        Assert(
            string.Equals(updated.Envelope?.Location, originalPath, StringComparison.OrdinalIgnoreCase),
            "Updating a storage-aware object should retain its provider locator.");
        Assert(
            Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories).Length == 1,
            "Updating a multiple-cardinality storage object should not create a second payload.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            async () =>
            {
                await foreach (var _ in repository.QueryManyAsync(x => true, cancelled.Token))
                {
                }
            },
            "Query enumeration should honor cancellation.").ConfigureAwait(false);

        var deletedNotifications = 0;
        using var subscription = envelopeRepository.SubscribeToDeleted(_ =>
        {
            Interlocked.Increment(ref deletedNotifications);
            return ValueTask.CompletedTask;
        });

        Assert(
            await repository.DeleteAsync(stored).ConfigureAwait(false),
            "Deleting active storage should return true.");
        Assert(
            !await repository.ExistsAsync(stored).ConfigureAwait(false),
            "ExistsAsync should return false after deletion.");
        Assert(deletedNotifications == 1, "A successful local delete should publish one deleted notification.");

        Assert(
            !await repository.DeleteAsync(stored).ConfigureAwait(false),
            "Repeated deletion should return false.");
        Assert(deletedNotifications == 1, "Repeated deletion should not publish another deleted notification.");

        await AssertThrowsAsync<ArgumentNullException>(
            async () => await repository.SaveAsync(null!).ConfigureAwait(false),
            "SaveAsync should reject null objects.").ConfigureAwait(false);
        await AssertThrowsAsync<ArgumentNullException>(
            async () => await repository.DeleteAsync(null!).ConfigureAwait(false),
            "DeleteAsync should reject null objects.").ConfigureAwait(false);
        await AssertThrowsAsync<ArgumentNullException>(
            async () => await repository.ExistsAsync(null!).ConfigureAwait(false),
            "ExistsAsync should reject null objects.").ConfigureAwait(false);
        AssertThrows<ArgumentNullException>(
            () => repository.QueryManyAsync(null!),
            "QueryManyAsync should reject a null predicate.");

        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryBulkSavePersistsInInputOrderAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var records = Enumerable.Range(0, 64)
            .Select(index => new LockTestRecord
            {
                Id = $"bulk-{index:D2}",
                Value = $"value-{index:D2}"
            })
            .ToArray();

        var saved = await repository.SaveManyAsync(records).ConfigureAwait(false);

        Assert(saved.Count == records.Length, "SaveManyAsync should return one canonical object per input object.");
        for (var index = 0; index < records.Length; index++)
        {
            Assert(
                saved[index].Id == records[index].Id,
                "SaveManyAsync should preserve result order.");
            Assert(
                saved[index].Envelope?.Location is not null && File.Exists(saved[index].Envelope!.Location),
                "Each bulk-saved object should have an active provider locator.");
        }

        var queried = new List<ILockTestRecord>();
        await foreach (var record in repository.QueryManyAsync(x => x.Id.StartsWith("bulk-", StringComparison.Ordinal)))
        {
            queried.Add(record);
        }

        Assert(queried.Count == records.Length, "Every bulk-saved object should be queryable.");
        Assert(
            FileObjectWriter<ILockTestRecord>.RetainedIdentitySemaphoreCount == 0,
            "Completed writes should not retain per-identity semaphores.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryBulkSaveReportsPartialFailuresAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildSingleProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var records = new[]
        {
            new LockTestRecord { Id = "bulk-duplicate", Value = "winner" },
            new LockTestRecord { Id = "bulk-duplicate", Value = "duplicate" }
        };

        StorageBulkOperationException? bulkException = null;
        try
        {
            await repository.SaveManyAsync(records).ConfigureAwait(false);
        }
        catch (StorageBulkOperationException ex)
        {
            bulkException = ex;
        }

        bulkException = AssertNotNull(
            bulkException,
            "Bulk duplicate save should report an aggregate storage failure.");
        Assert(bulkException.Failures.Count == 1, "Exactly one duplicate write should fail.");
        Assert(bulkException.SucceededIndexes.Count == 1, "Exactly one duplicate write should succeed.");

        var queried = new List<ILockTestRecord>();
        await foreach (var record in repository.QueryManyAsync(x => x.Id == "bulk-duplicate"))
        {
            queried.Add(record);
        }

        Assert(queried.Count == 1, "A partial bulk failure should leave the successful write committed.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryDoesNotCacheObjectInstancesByDefaultAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildSelectiveObjectCacheProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var saved = await repository.SaveAsync(
            new LockTestRecord { Id = "metadata-only", Value = "stored" }).ConfigureAwait(false);
        var storageKey = AssertNotNull(saved.Envelope?.EnvelopeCacheKey, "Saved record should have a storage key.");
        var envelopes = provider.GetRequiredKeyedService<
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<ILockTestRecord>>>("ObjectCacheTest");

        Assert(envelopes.TryGetValue(storageKey, out var cached), "Envelope metadata should be cached after save.");
        Assert(cached!.ObjectInstance is null, "Default cache should retain envelope metadata without the object instance.");
        Assert(cached.ObjectInstanceWeakReference is null, "Metadata-only cache should not retain a weak object reference.");

        var queried = await repository.QueryFirstAsync(x => x.Id == "metadata-only").ConfigureAwait(false);
        AssertNotNull(queried, "Record should still be queryable from storage.");
        Assert(cached.ObjectInstance is null, "Default query hydration should not promote the object into the envelope cache.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryWeakObjectCacheUsesWeakReferenceAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildSelectiveObjectCacheProvider(testRoot, StorageObjectCacheMode.WeakObject);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var saved = await repository.SaveAsync(
            new LockTestRecord { Id = "weak-cache", Value = "stored" }).ConfigureAwait(false);
        var storageKey = AssertNotNull(saved.Envelope?.EnvelopeCacheKey, "Saved record should have a storage key.");
        var envelopes = provider.GetRequiredKeyedService<
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<ILockTestRecord>>>("ObjectCacheTest");

        Assert(envelopes.TryGetValue(storageKey, out var cached), "Envelope metadata should be cached after save.");
        Assert(cached!.ObjectInstance is null, "Weak cache should not retain a strong object reference.");
        var weakReference = AssertNotNull(cached.ObjectInstanceWeakReference, "Weak cache should store a weak object reference.");
        Assert(weakReference.TryGetTarget(out var weakTarget), "Weak target should be alive while the caller still holds the saved object.");
        Assert(ReferenceEquals(saved, weakTarget), "Weak cache should point at the saved object while it remains alive.");

        var queried = await repository.QueryFirstAsync(x => x.Id == "weak-cache").ConfigureAwait(false);
        queried = AssertNotNull(queried, "Weak-cached record should still be queryable.");
        Assert(cached.ObjectInstance is null, "Weak-cache query should not promote the object to a strong cache reference.");
        Assert(cached.ObjectInstanceWeakReference?.TryGetTarget(out _) == true, "Weak cache should keep a weak target after query while the result is alive.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryCachesStorageCachedObjectsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildSelectiveObjectCacheProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var saved = await repository.SaveAsync(
            new CachedLockTestRecord { Id = "type-cache", Value = "stored" }).ConfigureAwait(false);
        var storageKey = AssertNotNull(saved.Envelope?.EnvelopeCacheKey, "Saved record should have a storage key.");
        var envelopes = provider.GetRequiredKeyedService<
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<ILockTestRecord>>>("ObjectCacheTest");

        Assert(envelopes.TryGetValue(storageKey, out var cached), "Envelope should be cached after save.");
        Assert(ReferenceEquals(saved, cached!.ObjectInstance), "StorageCached concrete types should retain the object instance.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryCachesEnvelopeFlaggedObjectsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string storageKey;
        await using (var writerProvider = BuildSelectiveObjectCacheProvider(testRoot))
        {
            var repository = writerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var saved = await repository.SaveAsync(
                new LockTestRecord { Id = "envelope-cache", Value = "stored" }).ConfigureAwait(false);
            storageKey = AssertNotNull(saved.Envelope?.EnvelopeCacheKey, "Saved record should have a storage key.");
            saved.Envelope!.CacheObjectInstance = true;
            saved = await repository.SaveAsync(saved).ConfigureAwait(false);

            var envelopes = writerProvider.GetRequiredKeyedService<
                IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<ILockTestRecord>>>("ObjectCacheTest");
            Assert(envelopes.TryGetValue(storageKey, out var cached), "Envelope should be cached after flagged save.");
            Assert(ReferenceEquals(saved, cached!.ObjectInstance), "Envelope cache flag should retain the current object instance.");
        }

        await using (var readerProvider = BuildSelectiveObjectCacheProvider(testRoot))
        {
            var repository = readerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var envelopes = readerProvider.GetRequiredKeyedService<
                IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<ILockTestRecord>>>("ObjectCacheTest");
            Assert(envelopes.TryGetValue(storageKey, out var coldEnvelope), "Cold start should seed envelope metadata.");
            Assert(coldEnvelope!.ObjectInstance is null, "Cold start should not hydrate the object before query.");

            var queried = await repository.QueryFirstAsync(x => x.Id == "envelope-cache").ConfigureAwait(false);
            queried = AssertNotNull(queried, "Flagged record should be queryable after restart.");
            Assert(coldEnvelope.CacheObjectInstance, "Persisted envelope cache flag should be restored from storage.");
            Assert(ReferenceEquals(queried, coldEnvelope.ObjectInstance), "Persisted envelope flag should retain the queried object instance.");
        }
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryRegistrationStoragePolicyOverridesConfigAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var options = new LockTestOptions(testRoot);
        var registrationPolicy = StoragePolicy.Default with
        {
            MaxConcurrency = 2,
            UseSingleWriteBuffer = false,
            WatchStorageChanges = false
        };

        var services = new ServiceCollection();
        services.AddRepository<ILockTestRecord>(
            options,
            storagePolicy: registrationPolicy);

        await using var provider = services.BuildServiceProvider();
        var effectivePolicy = provider.GetRequiredKeyedService<StoragePolicy>("LockTest");
        var unkeyedPolicy = provider.GetRequiredService<StoragePolicy>();

        Assert(
            ReferenceEquals(effectivePolicy, registrationPolicy),
            "The keyed effective storage policy should be the registration override.");
        Assert(
            ReferenceEquals(unkeyedPolicy, registrationPolicy),
            "The unkeyed compatibility storage policy should resolve to the effective policy.");
        Assert(
            options.StoragePolicy?.MaxConcurrency != effectivePolicy.MaxConcurrency,
            "The registration policy should override the config policy.");

        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);
        var saved = await repository.SaveManyAsync(
            Enumerable.Range(0, 4).Select(index => new LockTestRecord
            {
                Id = $"policy-{index}",
                Value = "stored"
            })).ConfigureAwait(false);
        Assert(saved.Count == 4, "Repository should remain usable with a registration-level storage policy.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task RepositoryRegistrationStorageIdentityFactoryControlsFileIdentityAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var options = new LockTestOptions(testRoot);
        var services = new ServiceCollection();
        services.AddRepository<ILockTestRecord>(
            options,
            storageIdentityFactory: new PrefixStorageIdentityFactory(options, "repository"));

        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var saved = await repository.SaveAsync(
            new LockTestRecord { Id = "factory", Value = "stored" }).ConfigureAwait(false);
        var fileName = Path.GetFileNameWithoutExtension(
            AssertNotNull(saved.Envelope?.Location, "Saved object should have a provider locator."));

        Assert(
            fileName == "repository-factory-0",
            $"Repository-level storage identity factory should control file identity, found '{fileName}'.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task DefaultIdentityFactoryUsesGuidV7KeysAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var saved = await repository.SaveAsync(
            new LockTestRecord { Id = "guid-v7", Value = "stored" }).ConfigureAwait(false);
        var fileName = Path.GetFileNameWithoutExtension(
            AssertNotNull(saved.Envelope?.Location, "Saved object should have a provider locator."));

        Assert(
            fileName.EndsWith("-0", StringComparison.Ordinal),
            $"Default multiple-cardinality key should use zero-based suffix, found '{fileName}'.");
        var guidPart = fileName[..^2];
        Assert(
            guidPart.Length == 32,
            $"Default key should contain a 32-character GUID N-format value, found '{fileName}'.");
        Assert(
            guidPart[12] == '7',
            $"Default key should be a GUID v7 value, found '{fileName}'.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task SequentialIdentityFactoriesCreateFilenameKeysAsync()
{
    var intRoot = CreateTestRoot();
    try
    {
        var intOptions = new LockTestOptions(intRoot);
        var intServices = new ServiceCollection();
        intServices.AddRepository<ILockTestRecord>(
            intOptions,
            storageIdentityFactory: new FlatFileStorageIdentityFactory<ILockTestRecord, int>(
                intOptions,
                new InMemorySequentialIntEnvelopeKeyFactory<ILockTestRecord>()));

        await using var intProvider = intServices.BuildServiceProvider();
        var intRepository = intProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await intRepository.InitializeAsync().ConfigureAwait(false);

        var first = await intRepository.SaveAsync(
            new LockTestRecord { Id = "int-1", Value = "stored" }).ConfigureAwait(false);
        var second = await intRepository.SaveAsync(
            new LockTestRecord { Id = "int-2", Value = "stored" }).ConfigureAwait(false);

        Assert(
            Path.GetFileNameWithoutExtension(first.Envelope?.Location) == "1-0",
            "Sequential int key factory should start at 1.");
        Assert(
            Path.GetFileNameWithoutExtension(second.Envelope?.Location) == "2-0",
            "Sequential int key factory should increment per new object.");
        AssertNoSidecarArtifacts(intRoot);
    }
    finally
    {
        TryDeleteDirectory(intRoot);
    }

    var bigIntegerRoot = CreateTestRoot();
    try
    {
        var bigIntegerOptions = new LockTestOptions(bigIntegerRoot);
        var bigIntegerServices = new ServiceCollection();
        bigIntegerServices.AddRepository<ILockTestRecord>(
            bigIntegerOptions,
            storageIdentityFactory: new FlatFileStorageIdentityFactory<ILockTestRecord, BigInteger>(
                bigIntegerOptions,
                new InMemorySequentialBigIntegerEnvelopeKeyFactory<ILockTestRecord>()));

        await using var bigIntegerProvider = bigIntegerServices.BuildServiceProvider();
        var bigIntegerRepository = bigIntegerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await bigIntegerRepository.InitializeAsync().ConfigureAwait(false);

        var saved = await bigIntegerRepository.SaveAsync(
            new LockTestRecord { Id = "big-int", Value = "stored" }).ConfigureAwait(false);

        Assert(
            Path.GetFileNameWithoutExtension(saved.Envelope?.Location) == "1-0",
            "Sequential BigInteger key factory should produce file-name-safe numeric keys.");
        AssertNoSidecarArtifacts(bigIntegerRoot);
    }
    finally
    {
        TryDeleteDirectory(bigIntegerRoot);
    }
}

static async Task WatcherDeliversLocalNotificationsAtLeastOnceAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildWatcherProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        var envelopeRepository = provider.GetRequiredService<IEnvelopeRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var added = 0;
        var stored = 0;
        var deleted = 0;
        using var addedSubscription = envelopeRepository.SubscribeToAdded(_ =>
        {
            Interlocked.Increment(ref added);
            return ValueTask.CompletedTask;
        });
        using var storedSubscription = envelopeRepository.SubscribeToStored(_ =>
        {
            Interlocked.Increment(ref stored);
            return ValueTask.CompletedTask;
        });
        using var deletedSubscription = envelopeRepository.SubscribeToDeleted(_ =>
        {
            Interlocked.Increment(ref deleted);
            return ValueTask.CompletedTask;
        });

        var record = await repository.SaveAsync(
            new LockTestRecord { Id = "watch-local", Value = "one" }).ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);
        Assert(added >= 1, $"Local save should publish at least one added event, found {added}.");
        Assert(stored >= 1, $"Local save should publish at least one stored event, found {stored}.");

        var storedAfterCreate = stored;
        record.Value = "two";
        await repository.SaveAsync(record).ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);
        Assert(
            stored > storedAfterCreate,
            $"Local update should publish at least one additional stored event, found {stored} total.");

        await repository.DeleteAsync(record).ConfigureAwait(false);
        await Task.Delay(1200).ConfigureAwait(false);
        Assert(deleted >= 1, $"Local delete should publish at least one deleted event, found {deleted}.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task WatcherSynchronizesExternalChangesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var observerProvider = BuildWatcherProvider(testRoot);
        await using var writerProvider = BuildProvider(testRoot);
        var observer = observerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        var writer = writerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await observer.InitializeAsync().ConfigureAwait(false);
        await writer.InitializeAsync().ConfigureAwait(false);

        var record = await writer.SaveAsync(
            new LockTestRecord { Id = "external", Value = "created" }).ConfigureAwait(false);
        await WaitUntilAsync(
            () => observer.QueryFirstAsync(x => x.Id == "external").AsTask().GetAwaiter().GetResult()?.Value == "created",
            TimeSpan.FromSeconds(10),
            "Observer did not receive external creation.").ConfigureAwait(false);

        record.Value = "updated";
        await writer.SaveAsync(record).ConfigureAwait(false);
        await WaitUntilAsync(
            () => observer.QueryFirstAsync(x => x.Id == "external").AsTask().GetAwaiter().GetResult()?.Value == "updated",
            TimeSpan.FromSeconds(10),
            "Observer did not receive external update.").ConfigureAwait(false);

        await writer.DeleteAsync(record).ConfigureAwait(false);
        await WaitUntilAsync(
            () => observer.QueryFirstAsync(x => x.Id == "external").AsTask().GetAwaiter().GetResult() is null,
            TimeSpan.FromSeconds(10),
            "Observer did not receive external deletion.").ConfigureAwait(false);
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task WatcherConvergesAfterRenameDeleteRaceAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var observerProvider = BuildWatcherProvider(testRoot);
        await using var writerProvider = BuildProvider(testRoot);
        var observer = observerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        var writer = writerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await observer.InitializeAsync().ConfigureAwait(false);
        await writer.InitializeAsync().ConfigureAwait(false);

        var record = await writer.SaveAsync(
            new LockTestRecord { Id = "rename-delete", Value = "stored" }).ConfigureAwait(false);
        await WaitUntilAsync(
            () => observer.QueryFirstAsync(x => x.Id == "rename-delete").AsTask().GetAwaiter().GetResult() is not null,
            TimeSpan.FromSeconds(10),
            "Observer did not receive the seed object.").ConfigureAwait(false);

        var originalPath = AssertNotNull(record.Envelope?.Location, "Stored object should have a locator.");
        var renamedPath = Path.Combine(
            Path.GetDirectoryName(originalPath)!,
            "renamed-" + Path.GetFileName(originalPath));
        File.Move(originalPath, renamedPath);
        File.Delete(renamedPath);

        await WaitUntilAsync(
            () => observer.QueryFirstAsync(x => x.Id == "rename-delete").AsTask().GetAwaiter().GetResult() is null,
            TimeSpan.FromSeconds(10),
            "Observer retained stale cache after rename/delete.").ConfigureAwait(false);
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task WatcherRescanRecoversMissedChangesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var observerProvider = BuildRescanOnlyProvider(testRoot);
        await using var writerProvider = BuildProvider(testRoot);
        var observer = observerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        var changeSource = observerProvider.GetRequiredService<IEnvelopeStorageChangeSource<ILockTestRecord>>();
        var writer = writerProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await observer.InitializeAsync().ConfigureAwait(false);
        await writer.InitializeAsync().ConfigureAwait(false);

        var record = await writer.SaveAsync(
            new LockTestRecord { Id = "rescan", Value = "created" }).ConfigureAwait(false);
        Assert(
            await observer.QueryFirstAsync(x => x.Id == "rescan").ConfigureAwait(false) is null,
            "Observer should not update while OS watching is disabled.");

        await changeSource.RescanAsync().ConfigureAwait(false);
        var created = await observer.QuerySingleAsync(x => x.Id == "rescan").ConfigureAwait(false);
        Assert(created.Value == "created", "Rescan should discover a missed creation.");

        await writer.DeleteAsync(record).ConfigureAwait(false);
        await changeSource.RescanAsync().ConfigureAwait(false);
        Assert(
            await observer.QueryFirstAsync(x => x.Id == "rescan").ConfigureAwait(false) is null,
            "Rescan should discover a missed deletion.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task KeyedDependencyInjectionIsolatesDatabasesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var localRoot = Path.Combine(testRoot, "local");
        var secureRoot = Path.Combine(testRoot, "secure");
        var localOptions = new NamedLockTestOptions("SharedPeopleOptions", localRoot);
        var secureOptions = new NamedLockTestOptions("SecurePeople", secureRoot);

        var services = new ServiceCollection();
        services.AddInterfaceDbAesGcmEncryption(
            "SecurePeople",
            new FixedTestKeyProvider(),
            new AesGcmEncryptorOptions());
        services.AddRepository<ILockTestRecord>(localOptions, "LocalPeople");
        services.AddRepository<ILockTestRecord>(secureOptions);

        await using var provider = services.BuildServiceProvider();
        var local = provider.GetInterfaceDbRepository<ILockTestRecord>("LocalPeople");
        var secure = provider.GetRequiredKeyedService<IRepository<ILockTestRecord>>("SecurePeople");
        var unkeyed = provider.GetRequiredService<IRepository<ILockTestRecord>>();

        Assert(!ReferenceEquals(local, secure), "Keyed databases should resolve different repository instances.");
        Assert(ReferenceEquals(local, unkeyed), "Unkeyed compatibility should resolve the first database.");

        await local.InitializeAsync().ConfigureAwait(false);
        await secure.InitializeAsync().ConfigureAwait(false);
        var localSaved = await local.SaveAsync(
            new LockTestRecord { Id = "local", Value = "plain" }).ConfigureAwait(false);
        var secureSaved = await secure.SaveAsync(
            new LockTestRecord { Id = "secure", Value = "encrypted" }).ConfigureAwait(false);

        Assert(
            localSaved.Envelope?.Location?.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase) == true,
            "Local database should write beneath its own root.");
        Assert(
            secureSaved.Envelope?.Location?.StartsWith(secureRoot, StringComparison.OrdinalIgnoreCase) == true,
            "Secure database should write beneath its own root.");

        var localBytes = await File.ReadAllBytesAsync(localSaved.Envelope!.Location!).ConfigureAwait(false);
        var secureBytes = await File.ReadAllBytesAsync(secureSaved.Envelope!.Location!).ConfigureAwait(false);
        Assert(HasPlaintextEnvelopeHeader(localBytes), "Local database should use plaintext serialization.");
        Assert(secureBytes.AsSpan(0, 4).SequenceEqual("IDBE"u8), "Secure database should use keyed AES encryption.");

        Assert(await local.QueryFirstAsync(x => x.Id == "secure").ConfigureAwait(false) is null,
            "Local database should not observe secure database records.");
        Assert(await secure.QueryFirstAsync(x => x.Id == "local").ConfigureAwait(false) is null,
            "Secure database should not observe local database records.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static Task KeyedDependencyInjectionHandlesDuplicatesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var options = new NamedLockTestOptions("DuplicateDb", testRoot);
        var services = new ServiceCollection();
        services.AddRepository<ILockTestRecord>(options);
        services.AddRepository<ILockTestRecord>(options);

        AssertThrows<InvalidOperationException>(
            () => services.AddRepository<ILockTestRecord>(
                new NamedLockTestOptions("DuplicateDb", Path.Combine(testRoot, "other"))),
            "A different configuration for the same contract/database key should be rejected.");

        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredKeyedService<IRepository<ILockTestRecord>>("DuplicateDb");
        Assert(repository is not null, "Idempotent duplicate registration should retain the keyed repository.");
        return Task.CompletedTask;
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task<bool> TrySaveAsync(
    IRepository<ILockTestRecord> repository,
    ILockTestRecord record)
{
    try
    {
        await repository.SaveAsync(record).ConfigureAwait(false);
        return true;
    }
    catch (StorageObjectAlreadyExistsException) { return false; }
    catch (WriteConflictException<ILockTestRecord>) { return false; }
}

static async Task TwoProcessesWriteSamePayloadAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string payloadPath;
        await using (var seedProvider = BuildProvider(testRoot))
        {
            var seedRepository = seedProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await seedRepository.InitializeAsync().ConfigureAwait(false);

            var seed = new LockTestRecord { Id = "shared", Value = "seed" };
            await seedRepository.SaveAsync(seed).ConfigureAwait(false);
            payloadPath = seed.Envelope?.Location ?? throw new InvalidOperationException("Seed envelope location was not initialized.");
        }

        Assert(File.Exists(payloadPath), "Seed payload should exist before child writers start.");

        var gatePath = Path.Combine(testRoot, "start.gate");
        var readyA = Path.Combine(testRoot, "writer-a.ready");
        var readyB = Path.Combine(testRoot, "writer-b.ready");

        using var childA = StartChild(testRoot, gatePath, readyA, "writer-a");
        using var childB = StartChild(testRoot, gatePath, readyB, "writer-b");

        await WaitUntilAsync(
            () => File.Exists(readyA) && File.Exists(readyB),
            TimeSpan.FromSeconds(15),
            "Child writers did not become ready.").ConfigureAwait(false);

        await File.WriteAllTextAsync(gatePath, "go").ConfigureAwait(false);

        var outputA = await childA.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var errorA = await childA.StandardError.ReadToEndAsync().ConfigureAwait(false);
        var outputB = await childB.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var errorB = await childB.StandardError.ReadToEndAsync().ConfigureAwait(false);

        await childA.WaitForExitAsync().ConfigureAwait(false);
        await childB.WaitForExitAsync().ConfigureAwait(false);

        Assert(new[] { childA.ExitCode, childB.ExitCode }.Order().SequenceEqual(new[] { 0, 3 }),
            $"Exactly one same-revision writer must commit. Exit codes {childA.ExitCode}/{childB.ExitCode}. {errorA} {errorB}");
        AssertNoSidecarArtifacts(testRoot);

        await using var verifyProvider = BuildProvider(testRoot);
        var verifyRepository = verifyProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await verifyRepository.InitializeAsync().ConfigureAwait(false);

        var reloaded = await verifyRepository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        reloaded = AssertNotNull(reloaded, "Shared record should still be readable after concurrent child writes.");
        Assert(
            reloaded.Value.StartsWith("writer-a:", StringComparison.Ordinal) ||
            reloaded.Value.StartsWith("writer-b:", StringComparison.Ordinal),
            $"Unexpected final value '{reloaded.Value}'.");

        var payloadFiles = Directory.GetFiles(testRoot, "*.mpk", SearchOption.AllDirectories);
        Assert(payloadFiles.Length == 1, $"Expected one payload file, found {payloadFiles.Length}.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task CrossProcessWriteDeleteRejectsStaleRevisionsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await SeedSharedRecordAsync(testRoot, "seed-one").ConfigureAwait(false);

        var writeGate = Path.Combine(testRoot, "write-first.gate");
        var deleteGate = Path.Combine(testRoot, "delete-second.gate");
        var writeReady = Path.Combine(testRoot, "write-first.ready");
        var deleteReady = Path.Combine(testRoot, "delete-second.ready");
        using var writer = StartChildOperation(testRoot, writeGate, writeReady, "write");
        using var deleter = StartChildOperation(testRoot, deleteGate, deleteReady, "delete");

        await WaitUntilAsync(
            () => File.Exists(writeReady) && File.Exists(deleteReady),
            TimeSpan.FromSeconds(15),
            "Write/delete child processes did not load the shared object.").ConfigureAwait(false);

        await File.WriteAllTextAsync(writeGate, "go").ConfigureAwait(false);
        await WaitForSuccessfulExitAsync(writer, "write-first").ConfigureAwait(false);
        await File.WriteAllTextAsync(deleteGate, "go").ConfigureAwait(false);
        await deleter.WaitForExitAsync().ConfigureAwait(false);
        Assert(deleter.ExitCode == 3, "A delete loaded before the committed update must conflict.");
        await AssertSharedRecordValueAsync(testRoot, "child-write").ConfigureAwait(false);
        var deleteFirstGate = Path.Combine(testRoot, "delete-first.gate");
        var writeSecondGate = Path.Combine(testRoot, "write-second.gate");
        var deleteFirstReady = Path.Combine(testRoot, "delete-first.ready");
        var writeSecondReady = Path.Combine(testRoot, "write-second.ready");
        using var deleteFirst = StartChildOperation(testRoot, deleteFirstGate, deleteFirstReady, "delete");
        using var writeSecond = StartChildOperation(testRoot, writeSecondGate, writeSecondReady, "write");

        await WaitUntilAsync(
            () => File.Exists(deleteFirstReady) && File.Exists(writeSecondReady),
            TimeSpan.FromSeconds(15),
            "Second write/delete child pair did not load the shared object.").ConfigureAwait(false);

        await File.WriteAllTextAsync(deleteFirstGate, "go").ConfigureAwait(false);
        await WaitForSuccessfulExitAsync(deleteFirst, "delete-first").ConfigureAwait(false);
        await File.WriteAllTextAsync(writeSecondGate, "go").ConfigureAwait(false);
        await writeSecond.WaitForExitAsync().ConfigureAwait(false);
        Assert(writeSecond.ExitCode == 3, "An update loaded before deletion must conflict.");
        await AssertSharedRecordValueAsync(testRoot, expectedValue: null).ConfigureAwait(false);
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task DeleteLockTimeoutMapsToStorageConcurrencyExceptionAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildShortTimeoutProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);
        var record = await repository.SaveAsync(
            new LockTestRecord { Id = "timeout", Value = "stored" }).ConfigureAwait(false);
        var payloadPath = AssertNotNull(record.Envelope?.Location, "Stored object should have a locator.");
        var lockPath = payloadPath + ".lock";

        await using (var heldLock = new FileStream(
            lockPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough))
        {
            await AssertThrowsAsync<StorageConcurrencyException>(
                async () => await repository.DeleteAsync(record).ConfigureAwait(false),
                "A payload lock timeout should map to StorageConcurrencyException.").ConfigureAwait(false);
        }

        File.Delete(lockPath);
        Assert(File.Exists(payloadPath), "A timed-out delete must not commit later.");
        Assert(
            !Directory.EnumerateFiles(testRoot, "*.delete.ready", SearchOption.AllDirectories).Any(),
            "A timed-out delete should remove its uncommitted proposal.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task StartupRecoversTerminatedMigrationProposalAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var readySignal = Path.Combine(testRoot, "migration-child.ready");
        using var child = StartAbandonedMigrationChild(testRoot, readySignal);
        await WaitUntilAsync(
            () => File.Exists(readySignal),
            TimeSpan.FromSeconds(15),
            "Migration child did not publish its proposal.").ConfigureAwait(false);

        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().ConfigureAwait(false);

        await using var provider = BuildMigrationProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var migrated = await repository.QuerySingleAsync(x => x.Id == "shared").ConfigureAwait(false);
        Assert(
            migrated.Value == "interrupted:migrated",
            $"Interrupted migration proposal recovered unexpected value '{migrated.Value}'.");
        Assert(migrated.Envelope?.VersionTag == "2.0.0", "Recovered migration should reach the active version.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task LosingProposalReturnsAndCachesCanonicalWinnerAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using var provider = BuildProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var record = new LockTestRecord { Id = "shared", Value = "seed" };
        await repository.SaveAsync(record).ConfigureAwait(false);
        var payloadPath = record.Envelope?.Location
            ?? throw new InvalidOperationException("Seed envelope location was not initialized.");

        var serializer = provider.GetRequiredService<IObjectSerializer<ILockTestRecord>>();
        var lifecycle = provider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<ILockTestRecord>>();
        await WriteReadyProposalAsync(
            serializer,
            lifecycle,
            payloadPath,
            "canonical-winner",
            "9999999999999999999",
            record.Envelope.EnvelopeCacheKey).ConfigureAwait(false);

        record.Value = "losing-write";
        WriteConflictException<ILockTestRecord>? conflict = null;
        try { await repository.SaveAsync(record).ConfigureAwait(false); }
        catch (WriteConflictException<ILockTestRecord> error) { conflict = error; }
        Assert(conflict is not null, "The stale save must reject the recovered committed revision.");
        var current = await conflict!.ReadCurrentAsync().ConfigureAwait(false);
        Assert(current!.Value == "canonical-winner", "Conflict retrieval must read the committed payload.");
        Assert(record.Envelope is not null, "Rejection must retain the loaded token.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task InitializationCommitsNewestAbandonedProposalAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string payloadPath;
        await using (var seedProvider = BuildProvider(testRoot))
        {
            var repository = seedProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var seed = new LockTestRecord { Id = "shared", Value = "seed" };
            await repository.SaveAsync(seed).ConfigureAwait(false);
            payloadPath = seed.Envelope?.Location
                ?? throw new InvalidOperationException("Seed envelope location was not initialized.");

            var serializer = seedProvider.GetRequiredService<IObjectSerializer<ILockTestRecord>>();
            var lifecycle = seedProvider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<ILockTestRecord>>();

            await WriteReadyProposalAsync(
                serializer,
                lifecycle,
                payloadPath,
                "older",
                "0000000000000000001").ConfigureAwait(false);
            await WriteReadyProposalAsync(
                serializer,
                lifecycle,
                payloadPath,
                "newest",
                "0000000000000000002").ConfigureAwait(false);
        }

        await using var recoveryProvider = BuildProvider(testRoot);
        var recoveryRepository = recoveryProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await recoveryRepository.InitializeAsync().ConfigureAwait(false);

        var recovered = await recoveryRepository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        recovered = AssertNotNull(recovered, "Recovered record should be queryable.");
        Assert(recovered.Value == "newest", $"Expected newest proposal, found '{recovered.Value}'.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task EagerMigrationDrainsProposalsBeforeMigratingAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyDirectory = Path.Combine(testRoot, "1.0.0");
        Directory.CreateDirectory(legacyDirectory);
        var legacyPayloadPath = Path.Combine(legacyDirectory, "shared.mpk");

        await using (var writerProvider = BuildMigrationProvider(testRoot))
        {
            var serializer = writerProvider.GetRequiredService<IObjectSerializer<IVersionedTestRecord>>();
            var lifecycle = writerProvider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();

            await WriteVersionedReadyProposalAsync(
                serializer,
                lifecycle,
                legacyPayloadPath,
                "older",
                "0000000000000000001").ConfigureAwait(false);
            await WriteVersionedReadyProposalAsync(
                serializer,
                lifecycle,
                legacyPayloadPath,
                "newest",
                "0000000000000000002").ConfigureAwait(false);
        }

        await using var migrationProvider = BuildMigrationProvider(testRoot);
        var repository = migrationProvider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var migrated = await repository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        migrated = AssertNotNull(migrated, "Migrated record should be queryable.");
        Assert(migrated.Value == "newest:migrated", $"Unexpected migrated value '{migrated.Value}'.");
        Assert(migrated.Envelope?.EnvelopeCacheKey == "shared", "Migration should preserve the envelope cache key.");
        Assert(migrated.Envelope?.VersionTag == "2.0.0", "Migration should write the active envelope version.");
        Assert(File.Exists(Path.Combine(testRoot, "2.0.0", "shared.mpk")), "Migrated payload should exist in the active version directory.");
        Assert(!File.Exists(legacyPayloadPath), "Legacy payload should be removed after strict migration succeeds.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task MultiStepMigrationReachesActiveVersionAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var migrationMetadata = new byte[] { 7, 6, 5, 4 };
        var legacyPayloadPath = await SeedVersionedLegacyPayloadAsync(
            testRoot,
            "multi",
            migrationMetadata).ConfigureAwait(false);
        var renamedLegacyPayloadPath = Path.Combine(
            Path.GetDirectoryName(legacyPayloadPath)!,
            "renamed-physical.mpk");
        File.Move(legacyPayloadPath, renamedLegacyPayloadPath);

        await using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerStrict,
            new VersionedMigrationOneToTwo(),
            new VersionedMigrationTwoToThree());
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var migrated = await repository.QuerySingleAsync(x => x.Id == "shared").ConfigureAwait(false);
        Assert(migrated.Value == "multi:one:two", $"Unexpected multi-step value '{migrated.Value}'.");
        Assert(migrated.Envelope?.VersionTag == "3.0.0", "Multi-step migration should reach the active version.");
        Assert(
            migrated.Envelope?.ExtensionMetadata?.SequenceEqual(migrationMetadata) == true,
            "Multi-step file migration should preserve opaque extension metadata.");
        Assert(
            migrated.Envelope?.EnvelopeCacheKey == "renamed-physical",
            "Migration should use the legacy file name as its physical identity, not stale serialized metadata.");
        Assert(
            File.Exists(Path.Combine(testRoot, "3.0.0", "renamed-physical.mpk")),
            "Active payload should retain the externally renamed physical identity.");
        Assert(
            !File.Exists(renamedLegacyPayloadPath),
            "Renamed legacy payload should be removed after the complete chain succeeds.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task FileMigrationRejectsOversizedExtensionMetadataAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyDirectory = Path.Combine(testRoot, "1.0.0");
        Directory.CreateDirectory(legacyDirectory);
        var legacyPayloadPath = Path.Combine(legacyDirectory, "oversized.mpk");
        await File.WriteAllBytesAsync(
            legacyPayloadPath,
            CreateOversizedVersionedIdb5Payload("oversized")).ConfigureAwait(false);

        await using var provider = BuildMigrationProvider(testRoot);
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await AssertThrowsAsync<InvalidDataException>(
            () => repository.InitializeAsync().AsTask(),
            "File migration should reject extension metadata above the public envelope limit.").ConfigureAwait(false);

        Assert(File.Exists(legacyPayloadPath), "A rejected migration must retain its legacy payload.");
        Assert(
            !File.Exists(Path.Combine(testRoot, "2.0.0", "oversized.mpk")),
            "A rejected migration must not publish an active-version payload.");
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static byte[] CreateOversizedVersionedIdb5Payload(string cacheKey)
{
    using var stream = new MemoryStream();
    stream.Write("IDB5"u8);
    MessagePackSerializer.Serialize(stream, "1.0.0");
    MessagePackSerializer.Serialize(stream, cacheKey);
    MessagePackSerializer.Serialize(
        stream,
        typeof(VersionedMigrationRecord).AssemblyQualifiedName!);
    MessagePackSerializer.Serialize(stream, false);
    stream.WriteByte(MessagePackCode.MinFixMap);
    MessagePackSerializer.Serialize(
        stream,
        new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]);
    MessagePackSerializer.Serialize(
        stream,
        new VersionedMigrationRecord
        {
            Id = "shared",
            Value = "oversized"
        });
    return stream.ToArray();
}

static async Task StrictMigrationRejectsMissingLinkAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyPayloadPath = await SeedVersionedLegacyPayloadAsync(testRoot, "missing").ConfigureAwait(false);

        await using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerStrict,
            new VersionedMigrationOneToTwo());
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();

        await AssertThrowsAsync<MissingMigrationException>(
            async () => await repository.InitializeAsync().ConfigureAwait(false),
            "Strict migration should report a deleted or missing migration link.").ConfigureAwait(false);

        Assert(File.Exists(legacyPayloadPath), "A failed strict chain must preserve the legacy payload.");
        Assert(!File.Exists(Path.Combine(testRoot, "3.0.0", "shared.mpk")), "A failed strict chain must not leave an active payload.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static Task MigrationRegistrationRejectsAmbiguousSourcesAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerStrict,
            new VersionedMigrationOneToTwo(),
            new AlternateVersionedMigrationOneToTwo(),
            new VersionedMigrationTwoToThree());

        AssertThrows<AmbiguousMigrationException>(
            () => provider.GetRequiredService<IRepository<IVersionedTestRecord>>(),
            "Two migrations with the same source version should be rejected.");
        return Task.CompletedTask;
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static Task MigrationRegistrationRejectsBackwardDefinitionsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerStrict,
            new BackwardVersionedMigration());

        AssertThrows<InvalidMigrationDefinitionException>(
            () => provider.GetRequiredService<IRepository<IVersionedTestRecord>>(),
            "A migration target must be greater than its source version.");
        return Task.CompletedTask;
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task StrictMigrationPreservesLegacyStorageWhenCodeFailsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyPayloadPath = await SeedVersionedLegacyPayloadAsync(testRoot, "failed").ConfigureAwait(false);

        await using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerStrict,
            new FailingVersionedMigrationOneToTwo(),
            new VersionedMigrationTwoToThree());
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();

        await AssertThrowsAsync<MigrationExecutionException>(
            async () => await repository.InitializeAsync().ConfigureAwait(false),
            "Strict migration should expose an Up() failure.").ConfigureAwait(false);

        Assert(File.Exists(legacyPayloadPath), "Failed migration code must not delete legacy storage.");
        Assert(!File.Exists(Path.Combine(testRoot, "3.0.0", "shared.mpk")), "Failed migration code must not publish active storage.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task TolerantMigrationPreservesLegacyStorageAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyPayloadPath = await SeedVersionedLegacyPayloadAsync(testRoot, "tolerant").ConfigureAwait(false);
        var legacyBytes = await File.ReadAllBytesAsync(legacyPayloadPath).ConfigureAwait(false);

        await using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.EagerTolerant,
            new VersionedMigrationOneToTwo());
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        Assert(File.Exists(legacyPayloadPath), "Tolerant migration should retain the legacy payload.");
        var preservedBytes = await File.ReadAllBytesAsync(legacyPayloadPath).ConfigureAwait(false);
        Assert(
            legacyBytes.SequenceEqual(preservedBytes),
            "Tolerant migration should not modify a legacy payload when its chain is incomplete.");
        Assert(!File.Exists(Path.Combine(testRoot, "3.0.0", "shared.mpk")), "Tolerant migration should not publish a partial target.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task LazyMigrationUpgradesOnFirstQueryAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyPayloadPath = await SeedVersionedLegacyPayloadAsync(testRoot, "lazy").ConfigureAwait(false);
        var activePayloadPath = Path.Combine(testRoot, "3.0.0", "shared.mpk");

        await using var provider = BuildCustomMigrationProvider(
            testRoot,
            "3.0.0",
            MigrationStrategyType.Lazy,
            new VersionedMigrationOneToTwo(),
            new VersionedMigrationTwoToThree());
        var repository = provider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        Assert(File.Exists(legacyPayloadPath), "Lazy initialization should not migrate legacy storage.");
        Assert(!File.Exists(activePayloadPath), "Lazy initialization should not create active storage.");

        var migrated = await repository.QuerySingleAsync(x => x.Id == "shared").ConfigureAwait(false);
        Assert(migrated.Value == "lazy:one:two", $"Unexpected lazy migration value '{migrated.Value}'.");
        Assert(migrated.Envelope?.VersionTag == "3.0.0", "Lazy migration should reach the active version.");
        Assert(File.Exists(activePayloadPath), "The first query should persist active storage.");
        Assert(!File.Exists(legacyPayloadPath), "The first successful lazy migration should remove legacy storage.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task<string> SeedVersionedLegacyPayloadAsync(
    string testRoot,
    string value,
    byte[]? extensionMetadata = null)
{
    var legacyDirectory = Path.Combine(testRoot, "1.0.0");
    Directory.CreateDirectory(legacyDirectory);
    var legacyPayloadPath = Path.Combine(legacyDirectory, "shared.mpk");

    await using var writerProvider = BuildMigrationProvider(testRoot);
    var serializer = writerProvider.GetRequiredService<IObjectSerializer<IVersionedTestRecord>>();
    var lifecycle = writerProvider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();
    await WriteVersionedReadyProposalAsync(
        serializer,
        lifecycle,
        legacyPayloadPath,
        value,
        "0000000000000000001",
        extensionMetadata).ConfigureAwait(false);

    var coordinator = writerProvider.GetRequiredService<IFileWriteCoordinator>();
    await coordinator.DrainPendingWritesAsync(legacyPayloadPath).ConfigureAwait(false);
    return legacyPayloadPath;
}

static async Task EncryptedStorageRoundTripsAndRejectsTamperingAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        string payloadPath;
        await using (var provider = BuildEncryptedProvider(testRoot))
        {
            var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var record = new LockTestRecord { Id = "encrypted", Value = "secret-value" };
            await repository.SaveAsync(record).ConfigureAwait(false);
            payloadPath = record.Envelope?.Location
                ?? throw new InvalidOperationException("Encrypted payload location was not initialized.");
        }

        var stored = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
        Assert(stored.AsSpan(0, 4).SequenceEqual("IDBE"u8), "Encrypted payload should use the IDBE outer header.");
        Assert(!HasPlaintextEnvelopeHeader(stored), "Plain envelope header should not be visible in encrypted storage.");

        await using (var reloadProvider = BuildEncryptedProvider(testRoot))
        {
            var repository = reloadProvider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);
            var reloaded = await repository.QueryAsync(x => x.Id == "encrypted").ConfigureAwait(false);
            reloaded = AssertNotNull(reloaded, "Encrypted record should reload with the configured key.");
            Assert(reloaded.Value == "secret-value", "Encrypted record value should round trip.");
        }

        stored[^1] ^= 0x01;
        await File.WriteAllBytesAsync(payloadPath, stored).ConfigureAwait(false);

        await using var tamperedProvider = BuildEncryptedProvider(testRoot);
        var tamperedRepository = tamperedProvider.GetRequiredService<IRepository<ILockTestRecord>>();
        await tamperedRepository.InitializeAsync().ConfigureAwait(false);
        await AssertThrowsAsync<CryptographicException>(
            async () => await tamperedRepository.QueryAsync(x => x.Id == "encrypted").ConfigureAwait(false),
            "Tampered encrypted payload should fail authentication.").ConfigureAwait(false);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task EncryptedStorageLazilyRotatesToActiveKeyAsync()
{
    var testRoot = CreateTestRoot();
    var keyProvider = new RotatingTestKeyProvider();
    try
    {
        keyProvider.SetActiveKey("key-1");
        string payloadPath;
        await using (var provider = BuildRotatingEncryptedProvider(
            testRoot,
            keyProvider,
            EncryptionRotationMode.ReadHistoricalOnly))
        {
            var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);
            var saved = await repository.SaveAsync(
                new LockTestRecord { Id = "rotate", Value = "historical" }).ConfigureAwait(false);
            payloadPath = AssertNotNull(saved.Envelope?.Location, "Rotating payload should have a location.");
        }

        Assert(ReadEncryptedKeyId(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false)) == "key-1",
            "The original payload should identify the original active key.");

        keyProvider.SetActiveKey("key-2");
        keyProvider.ClearRequests();
        await using (var provider = BuildRotatingEncryptedProvider(
            testRoot,
            keyProvider,
            EncryptionRotationMode.ReencryptOnRead))
        {
            var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);
            var loaded = await repository.QuerySingleAsync(x => x.Id == "rotate").ConfigureAwait(false);
            Assert(loaded.Value == "historical", "Lazy rotation should preserve the object state.");
        }

        var rotated = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
        Assert(ReadEncryptedKeyId(rotated) == "key-2",
            "Lazy rotation should rewrite directly with the current active key.");
        Assert(keyProvider.RequestedKeyIds.Count(id => id == "key-1") == 1,
            "The historical key should be resolved once for decryption.");
        Assert(keyProvider.RequestedKeyIds.Count(id => id == "key-2") == 1,
            "The active key should be resolved once for replacement encryption.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task EncryptedStorageFieldsDecryptThroughProxyAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        await using (var provider = BuildEncryptedFieldProvider(testRoot))
        {
            var repository = provider.GetRequiredService<IRepository<IEncryptedFieldTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var concrete = new EncryptedFieldTestRecord
            {
                Id = "field-encryption",
                Secret = "sensitive-value"
            };
            await repository.SaveAsync(concrete).ConfigureAwait(false);

            Assert(concrete.Secret == "sensitive-value", "Staging must preserve the caller object; only the stored payload clears encrypted backing fields.");
            Assert(concrete.Envelope?.EncryptedFields.ContainsKey(nameof(IEncryptedFieldTestRecord.Secret)) == true,
                "Envelope should contain encrypted field ciphertext.");
        }

        await using (var provider = BuildEncryptedFieldProvider(testRoot))
        {
            var repository = provider.GetRequiredService<IRepository<IEncryptedFieldTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);

            var loaded = await repository.QueryFirstAsync(x => x.Id == "field-encryption").ConfigureAwait(false);
            loaded = AssertNotNull(loaded, "Encrypted field record should reload.");
            Assert(loaded.Secret == "sensitive-value", "Encrypted field getter should decrypt through the contract proxy.");
            Assert(loaded.Envelope?.EncryptedFields.ContainsKey(nameof(IEncryptedFieldTestRecord.Secret)) == true,
                "Reloaded proxy should expose encrypted field metadata through the envelope.");
        }
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task ForcedEncryptionRotationRewritesStoreAsync()
{
    var testRoot = CreateTestRoot();
    var keyProvider = new RotatingTestKeyProvider();
    try
    {
        keyProvider.SetActiveKey("key-1");
        string payloadPath;
        await using (var provider = BuildRotatingEncryptedProvider(
            testRoot,
            keyProvider,
            EncryptionRotationMode.ReadHistoricalOnly))
        {
            var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
            await repository.InitializeAsync().ConfigureAwait(false);
            var saved = await repository.SaveAsync(
                new LockTestRecord { Id = "forced", Value = "rotate-all" }).ConfigureAwait(false);
            payloadPath = AssertNotNull(saved.Envelope?.Location, "Forced rotation payload should have a location.");
        }

        keyProvider.SetActiveKey("key-2");
        await using var rotationProvider = BuildRotatingEncryptedProvider(
            testRoot,
            keyProvider,
            EncryptionRotationMode.ReadHistoricalOnly);
        var operation = rotationProvider
            .GetRequiredService<IEncryptionRotationOperation<ILockTestRecord>>();
        var result = await operation.RotateAllAsync().ConfigureAwait(false);

        Assert(result.ScannedPayloads == 1, "Forced rotation should scan the stored payload.");
        Assert(result.ReencryptedPayloads == 1, "Forced rotation should rewrite the historical payload.");
        Assert(result.UnchangedPayloads == 0, "Forced rotation should not report the historical payload as current.");
        Assert(ReadEncryptedKeyId(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false)) == "key-2",
            "Forced rotation should rewrite directly with the active key.");

        var secondResult = await operation.RotateAllAsync().ConfigureAwait(false);
        Assert(secondResult.ReencryptedPayloads == 0 && secondResult.UnchangedPayloads == 1,
            "A repeated forced rotation should leave current payloads unchanged.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static Task LegacyEncryptedPayloadUsesConfiguredKeyAsync()
{
    var legacyProvider = new FixedTestKeyProvider();
    var legacyEncryptor = new AesGcmEncryptor(legacyProvider);
    var plaintext = "legacy-payload"u8.ToArray();
    var legacyCipher = legacyEncryptor.Encrypt(plaintext);

    var keyProvider = new RotatingTestKeyProvider();
    keyProvider.SetActiveKey("key-2");
    var rotatingEncryptor = new AesGcmEncryptor(
        keyProvider,
        new AesGcmEncryptorOptions
        {
            LegacyKeyId = "key-1",
            RotationMode = EncryptionRotationMode.ReencryptOnRead
        });

    var result = rotatingEncryptor.DecryptWithMetadata(legacyCipher);
    Assert(result.Plaintext.SequenceEqual(plaintext), "The configured legacy key should decrypt IDBE version 1.");
    Assert(result.KeyId == "key-1", "Legacy decryption should report the configured historical key.");
    Assert(result.RequiresReencryption, "A legacy frame should be eligible for lazy rotation.");
    return Task.CompletedTask;
}

static Task WindowsCredentialManagerStoresKeyRingAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        return Task.CompletedTask;
    }

    var databaseName = "InterfaceDbTests-" + Guid.NewGuid().ToString("N");
    var store = new WindowsCredentialEncryptionKeyStore(databaseName);
    try
    {
        store.AddOrUpdateKey("old", RotatingTestKeyProvider.Key1);
        store.AddOrUpdateKey("current", RotatingTestKeyProvider.Key2);
        store.SetActiveKey("current");

        Assert(store.GetActiveKeyId() == "current", "Credential Manager should retain the active key identifier.");
        Assert(store.GetKey("old").Span.SequenceEqual(RotatingTestKeyProvider.Key1),
            "Credential Manager should retain historical key material.");
        Assert(store.GetCurrentKey().Span.SequenceEqual(RotatingTestKeyProvider.Key2),
            "Credential Manager should resolve the active key material.");
    }
    finally
    {
        store.ClearActiveKey();
        store.RemoveKey("old");
        store.RemoveKey("current");
    }

    return Task.CompletedTask;
}

static string ReadEncryptedKeyId(byte[] payload)
{
    Assert(payload.Length >= 9 && payload.AsSpan(0, 4).SequenceEqual("IDBE"u8),
        "Expected an IDBE encrypted payload.");
    Assert(payload[4] == 0x02, "Expected the key-aware IDBE version 2 frame.");
    var keyIdSize = BitConverter.ToUInt16(payload, 7);
    return Encoding.UTF8.GetString(payload, 9, keyIdSize);
}

static async Task EncryptedEagerMigrationDrainsAndMigratesProposalsAsync()
{
    var testRoot = CreateTestRoot();
    try
    {
        var legacyDirectory = Path.Combine(testRoot, "1.0.0");
        Directory.CreateDirectory(legacyDirectory);
        var legacyPayloadPath = Path.Combine(legacyDirectory, "shared.mpk");

        await using (var writerProvider = BuildEncryptedMigrationProvider(testRoot))
        {
            var serializer = writerProvider.GetRequiredService<IObjectSerializer<IVersionedTestRecord>>();
            var lifecycle = writerProvider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();
            await WriteVersionedReadyProposalAsync(
                serializer,
                lifecycle,
                legacyPayloadPath,
                "encrypted-newest",
                "0000000000000000001").ConfigureAwait(false);
        }

        await using var migrationProvider = BuildEncryptedMigrationProvider(testRoot);
        var repository = migrationProvider.GetRequiredService<IRepository<IVersionedTestRecord>>();
        await repository.InitializeAsync().ConfigureAwait(false);

        var migrated = await repository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        migrated = AssertNotNull(migrated, "Encrypted migrated record should be queryable.");
        Assert(migrated.Value == "encrypted-newest:migrated", $"Unexpected encrypted migrated value '{migrated.Value}'.");

        var activePayload = Path.Combine(testRoot, "2.0.0", "shared.mpk");
        var stored = await File.ReadAllBytesAsync(activePayload).ConfigureAwait(false);
        Assert(stored.AsSpan(0, 4).SequenceEqual("IDBE"u8), "Migrated payload should remain encrypted.");
        AssertNoSidecarArtifacts(testRoot);
    }
    finally
    {
        TryDeleteDirectory(testRoot);
    }
}

static async Task WriteReadyProposalAsync(
    IObjectSerializer<ILockTestRecord> serializer,
    Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<ILockTestRecord> lifecycle,
    string payloadPath,
    string value,
    string order,
    string cacheKey = "shared")
{
    var record = new LockTestRecord { Id = "shared", Value = value };
    var envelope = await lifecycle.WrapAsync(record).ConfigureAwait(false);
    envelope.EnvelopeCacheKey = cacheKey;
    envelope.Location = payloadPath;
    envelope.VersionTag = "1.0.0";

    var proposalPath = payloadPath + ".pending." + order + "." + Guid.NewGuid().ToString("N") + ".ready";
    await File.WriteAllBytesAsync(proposalPath, serializer.Serialize(envelope)).ConfigureAwait(false);
}

static async Task WriteVersionedReadyProposalAsync(
    IObjectSerializer<IVersionedTestRecord> serializer,
    Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<IVersionedTestRecord> lifecycle,
    string payloadPath,
    string value,
    string order,
    byte[]? extensionMetadata = null)
{
    var record = new VersionedTestRecord { Id = "shared", Value = value };
    var envelope = await lifecycle.WrapAsync(record).ConfigureAwait(false);
    envelope.EnvelopeCacheKey = "shared";
    envelope.Location = payloadPath;
    envelope.VersionTag = "1.0.0";
    envelope.ExtensionMetadata = extensionMetadata?.ToArray();

    var proposalPath = payloadPath + ".pending." + order + "." + Guid.NewGuid().ToString("N") + ".ready";
    await File.WriteAllBytesAsync(proposalPath, serializer.Serialize(envelope)).ConfigureAwait(false);
}

static async Task RunChildWriterAsync(
    string dataRoot,
    string gatePath,
    string readyPath,
    string writerName)
{
    await using var provider = BuildProvider(dataRoot);
    var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
    await repository.InitializeAsync().ConfigureAwait(false);

    var record = await repository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
    record = AssertNotNull(record, $"{writerName} could not load the shared record.");

    Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
    await File.WriteAllTextAsync(readyPath, writerName).ConfigureAwait(false);
    await WaitUntilAsync(
        () => File.Exists(gatePath),
        TimeSpan.FromSeconds(15),
        $"{writerName} timed out waiting for the start gate.").ConfigureAwait(false);

    record.Value = writerName + ":" + new string(writerName[^1], 4 * 1024 * 1024);
    try
    {
        await repository.SaveAsync(record).ConfigureAwait(false);
        Console.WriteLine($"{writerName} saved {record.Value.Length} bytes to {record.Envelope?.Location}");
    }
    catch (WriteConflictException<ILockTestRecord>) { Environment.ExitCode = 3; }
}

static async Task RunChildOperationAsync(
    string dataRoot,
    string gatePath,
    string readyPath,
    string operation)
{
    await using var provider = BuildProvider(dataRoot);
    var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
    await repository.InitializeAsync().ConfigureAwait(false);
    var record = await repository.QuerySingleAsync(x => x.Id == "shared").ConfigureAwait(false);

    await File.WriteAllTextAsync(readyPath, operation).ConfigureAwait(false);
    await WaitUntilAsync(
        () => File.Exists(gatePath),
        TimeSpan.FromSeconds(20),
        $"Child operation '{operation}' timed out waiting for its gate.").ConfigureAwait(false);

    try
    {
        if (string.Equals(operation, "delete", StringComparison.Ordinal))
            await repository.DeleteAsync(record).ConfigureAwait(false);
        else
        {
            record.Value = "child-write";
            await repository.SaveAsync(record).ConfigureAwait(false);
        }
    }
    catch (WriteConflictException<ILockTestRecord>) { Environment.ExitCode = 3; }
}

static async Task RunChildAbandonedMigrationProposalAsync(
    string dataRoot,
    string readySignal)
{
    var legacyDirectory = Path.Combine(dataRoot, "1.0.0");
    Directory.CreateDirectory(legacyDirectory);
    var payloadPath = Path.Combine(legacyDirectory, "shared.mpk");

    await using var provider = BuildMigrationProvider(dataRoot);
    var serializer = provider.GetRequiredService<IObjectSerializer<IVersionedTestRecord>>();
    var lifecycle = provider.GetRequiredService<Idb.Libraries.Abstractions.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();
    await WriteVersionedReadyProposalAsync(
        serializer,
        lifecycle,
        payloadPath,
        "interrupted",
        DateTimeOffset.UtcNow.UtcTicks.ToString("D19")).ConfigureAwait(false);
    await File.WriteAllTextAsync(readySignal, "published").ConfigureAwait(false);
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
}

static async Task SeedSharedRecordAsync(string dataRoot, string value)
{
    await using var provider = BuildProvider(dataRoot);
    var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
    await repository.InitializeAsync().ConfigureAwait(false);
    await repository.SaveAsync(
        new LockTestRecord { Id = "shared", Value = value }).ConfigureAwait(false);
}

static async Task AssertSharedRecordValueAsync(string dataRoot, string? expectedValue)
{
    await using var provider = BuildProvider(dataRoot);
    var repository = provider.GetRequiredService<IRepository<ILockTestRecord>>();
    await repository.InitializeAsync().ConfigureAwait(false);
    var record = await repository.QueryFirstAsync(x => x.Id == "shared").ConfigureAwait(false);
    if (expectedValue is null)
    {
        Assert(record is null, "The shared object should be deleted.");
        return;
    }

    record = AssertNotNull(record, "The shared object should exist.");
    Assert(record.Value == expectedValue, $"Expected shared value '{expectedValue}', found '{record.Value}'.");
}

static async Task WaitForSuccessfulExitAsync(Process process, string name)
{
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    var output = await outputTask.ConfigureAwait(false);
    var error = await errorTask.ConfigureAwait(false);
    Assert(
        process.ExitCode == 0,
        $"{name} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
}

static ServiceProvider BuildProvider(string dataRoot)
{
    var options = new LockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildSelectiveObjectCacheProvider(
    string dataRoot,
    StorageObjectCacheMode cacheMode = StorageObjectCacheMode.MetadataOnly)
{
    var options = new SelectiveObjectCacheOptions(dataRoot, cacheMode);
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildWatcherProvider(string dataRoot)
{
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(new WatcherLockTestOptions(dataRoot, watchStorageChanges: true));
    return services.BuildServiceProvider();
}

static ServiceProvider BuildRescanOnlyProvider(string dataRoot)
{
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(new WatcherLockTestOptions(dataRoot, watchStorageChanges: false));
    return services.BuildServiceProvider();
}

static ServiceProvider BuildShortTimeoutProvider(string dataRoot)
{
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(new ShortTimeoutLockTestOptions(dataRoot));
    return services.BuildServiceProvider();
}

static ServiceProvider BuildSingleProvider(string dataRoot)
{
    var options = new SingleLockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(
        options,
        storageIdentityFactory: new LockTestStorageIdentityFactory(options));
    return services.BuildServiceProvider();
}

static ServiceProvider BuildEncryptedProvider(string dataRoot)
{
    var options = new LockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddInterfaceDbAesGcmEncryption(
        new FixedTestKeyProvider(),
        new AesGcmEncryptorOptions());
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildEncryptedFieldProvider(string dataRoot)
{
    var options = new EncryptedFieldTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddInterfaceDbAesGcmEncryption(
        new FixedTestKeyProvider(),
        new AesGcmEncryptorOptions());
    services.AddRepository<IEncryptedFieldTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildRotatingEncryptedProvider(
    string dataRoot,
    RotatingTestKeyProvider keyProvider,
    EncryptionRotationMode rotationMode)
{
    var options = new LockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddInterfaceDbAesGcmEncryption(
        keyProvider,
        new AesGcmEncryptorOptions
        {
            LegacyKeyId = "key-1",
            RotationMode = rotationMode
        });
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildMigrationProvider(string dataRoot)
{
    var options = new VersionedTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddSingleton<IMigration>(new VersionedMigrationRecord());
    services.AddRepository<IVersionedTestRecord>(options, MigrationStrategyType.EagerStrict);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildCustomMigrationProvider(
    string dataRoot,
    string activeVersion,
    MigrationStrategyType strategyType,
    params IMigration[] migrations)
{
    var options = new VersionedTestOptions(dataRoot, activeVersion);
    var services = new ServiceCollection();
    foreach (var migration in migrations)
    {
        services.AddSingleton<IMigration>(migration);
    }
    services.AddRepository<IVersionedTestRecord>(options, strategyType);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildEncryptedMigrationProvider(string dataRoot)
{
    var options = new VersionedTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddInterfaceDbAesGcmEncryption(
        new FixedTestKeyProvider(),
        new AesGcmEncryptorOptions());
    services.AddSingleton<IMigration>(new VersionedMigrationRecord());
    services.AddRepository<IVersionedTestRecord>(options, MigrationStrategyType.EagerStrict);
    return services.BuildServiceProvider();
}

static Process StartChild(
    string dataRoot,
    string gatePath,
    string readyPath,
    string writerName)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Unable to resolve current test executable path.");

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--child");
    startInfo.ArgumentList.Add(dataRoot);
    startInfo.ArgumentList.Add(gatePath);
    startInfo.ArgumentList.Add(readyPath);
    startInfo.ArgumentList.Add(writerName);

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start child writer '{writerName}'.");
}

static Process StartChildOperation(
    string dataRoot,
    string gatePath,
    string readyPath,
    string operation)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Unable to resolve current test executable path.");
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--child-operation");
    startInfo.ArgumentList.Add(dataRoot);
    startInfo.ArgumentList.Add(gatePath);
    startInfo.ArgumentList.Add(readyPath);
    startInfo.ArgumentList.Add(operation);
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start child operation '{operation}'.");
}

static Process StartAbandonedMigrationChild(string dataRoot, string readySignal)
{
    var executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("Unable to resolve current test executable path.");
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--child-abandon-migration");
    startInfo.ArgumentList.Add(dataRoot);
    startInfo.ArgumentList.Add(readySignal);
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start interrupted migration child.");
}

static string CreateTestRoot()
{
    var root = Path.Combine(Path.GetTempPath(), "InterfaceDb.IntegrationTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

static async Task WaitUntilAsync(
    Func<bool> condition,
    TimeSpan timeout,
    string failureMessage)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(50).ConfigureAwait(false);
    }

    throw new TimeoutException(failureMessage);
}

static void AssertNoSidecarArtifacts(string root)
{
    var artifacts = Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path =>
        {
            var fileName = Path.GetFileName(path);
            return fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".pending.", StringComparison.OrdinalIgnoreCase);
        })
        .ToArray();

    Assert(artifacts.Length == 0, "Expected no leftover sidecar artifacts, found: " + string.Join(", ", artifacts));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool HasPlaintextEnvelopeHeader(byte[] bytes)
{
    return bytes.Length >= 4
        && bytes[0] == (byte)'I'
        && bytes[1] == (byte)'D'
        && bytes[2] == (byte)'B'
        && bytes[3] is (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5' or (byte)'6';
}

static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
{
    var results = new List<T>();
    await foreach (var item in source.ConfigureAwait(false))
    {
        results.Add(item);
    }

    return results;
}

static T AssertNotNull<T>(T? value, string message)
    where T : class
{
    if (value is null)
    {
        throw new InvalidOperationException(message);
    }

    return value;
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action().ConfigureAwait(false);
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

public sealed class LockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public LockTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "LockTest";
    public string ContractName { get; } = "LockTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        HydrateOnInitialize = true,
        ObjectCacheMode = StorageObjectCacheMode.StrongObject,
        MaxConcurrency = 4,
        RaiseNotifications = true,
        UseSidecarWriteLocks = true,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10),
        StaleWriteLockTimeout = TimeSpan.FromSeconds(30)
    };
}

public sealed class SelectiveObjectCacheOptions : IDbContractConfigDto<ILockTestRecord>
{
    public SelectiveObjectCacheOptions(
        string dataRoot,
        StorageObjectCacheMode cacheMode = StorageObjectCacheMode.MetadataOnly)
    {
        DataRoot = dataRoot;
        StoragePolicy = StoragePolicy.Default with
        {
            HydrateOnInitialize = false,
            ObjectCacheMode = cacheMode,
            RaiseNotifications = false,
            UseSidecarWriteLocks = true,
            WatchStorageChanges = false
        };
    }

    public string EnvelopeDICacheKey { get; } = "ObjectCacheTest";
    public string ContractName { get; } = "ObjectCacheTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; }
}

public sealed class IndexedTestOptions : IDbContractConfigDto<IIndexedTestRecord>
{
    public IndexedTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "IndexedTest";
    public string ContractName { get; } = "IndexedTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        HydrateOnInitialize = true,
        ObjectCacheMode = StorageObjectCacheMode.StrongObject,
        MaxConcurrency = 4,
        RaiseNotifications = true,
        UseSidecarWriteLocks = true,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10),
        StaleWriteLockTimeout = TimeSpan.FromSeconds(30)
    };
}

public sealed class SingleLockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public SingleLockTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "SingleLockTest";
    public string ContractName { get; } = "SingleLockTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Single;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10)
    };
}

public sealed class WatcherLockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public WatcherLockTestOptions(string dataRoot, bool watchStorageChanges)
    {
        DataRoot = dataRoot;
        StoragePolicy = StoragePolicy.Default with
        {
            DurabilityMode = StorageDurabilityMode.Durable,
            HydrateOnInitialize = true,
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            RaiseNotifications = true,
            UseSidecarWriteLocks = true,
            UseSingleWriteBuffer = true,
            UseWriterSemaphore = false,
            WatchStorageChanges = watchStorageChanges,
            StorageChangeDebounce = TimeSpan.FromMilliseconds(75),
            LocalStorageChangeSuppression = TimeSpan.FromMilliseconds(600),
            WriteLockTimeout = TimeSpan.FromSeconds(10),
            WriteLockRetryDelay = TimeSpan.FromMilliseconds(10)
        };
    }

    public string EnvelopeDICacheKey { get; } = "WatcherLockTest";
    public string ContractName { get; } = "WatcherLockTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; }
}

public sealed class ShortTimeoutLockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public ShortTimeoutLockTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "ShortTimeoutLockTest";
    public string ContractName { get; } = "ShortTimeoutLockTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        HydrateOnInitialize = true,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromMilliseconds(200),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(20),
        StaleWriteLockTimeout = TimeSpan.FromMinutes(5)
    };
}

public sealed class NamedLockTestOptions : IDbContractConfigDto<ILockTestRecord>
{
    public NamedLockTestOptions(string databaseName, string dataRoot)
    {
        EnvelopeDICacheKey = databaseName;
        ContractName = databaseName;
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; }
    public string ContractName { get; }
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Multiple;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        UseSingleWriteBuffer = true,
        WatchStorageChanges = false
    };
}

public sealed class EncryptedFieldTestOptions : IDbContractConfigDto<IEncryptedFieldTestRecord>
{
    public EncryptedFieldTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "EncryptedFieldTest";
    public string ContractName { get; } = "EncryptedFieldTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        HydrateOnInitialize = false,
        RaiseNotifications = false,
        UseSidecarWriteLocks = true,
        WatchStorageChanges = false
    };
}

public sealed class LockTestStorageIdentityFactory : IEnvelopeStorageIdentityFactory<ILockTestRecord>
{
    private readonly SingleLockTestOptions _options;

    public LockTestStorageIdentityFactory(SingleLockTestOptions options)
    {
        _options = options;
    }

    public StorageObjectCardinality ObjectCardinality =>
        ((IDbContractConfigDto<ILockTestRecord>)_options).ObjectCardinality;

    public void InitEnvelope(IObjectEnvelope<ILockTestRecord> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var instance = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope must contain an object before resolving its identity.");

        envelope.EnvelopeCacheKey = string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)
            ? instance.Id
            : envelope.EnvelopeCacheKey;
        envelope.Location ??= Path.Combine(
            _options.DataRoot!,
            envelope.VersionTag,
            envelope.EnvelopeCacheKey + _options.FileExtension);
    }
}

public sealed class PrefixStorageIdentityFactory : IEnvelopeStorageIdentityFactory<ILockTestRecord>
{
    private readonly LockTestOptions _options;
    private readonly string _prefix;

    public PrefixStorageIdentityFactory(LockTestOptions options, string prefix)
    {
        _options = options;
        _prefix = prefix;
    }

    public StorageObjectCardinality ObjectCardinality =>
        ((IDbContractConfigDto<ILockTestRecord>)_options).ObjectCardinality;

    public void InitEnvelope(IObjectEnvelope<ILockTestRecord> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var instance = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope must contain an object before resolving its identity.");

        envelope.EnvelopeCacheKey = string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)
            ? $"{_prefix}-{instance.Id}"
            : envelope.EnvelopeCacheKey;
        envelope.Location ??= Path.Combine(
            _options.DataRoot!,
            envelope.VersionTag,
            envelope.EnvelopeCacheKey + _options.FileExtension);
    }
}

[StorageScope(VersionTag = "1.0.0")]
public interface ILockTestRecord : IStoredContract<ILockTestRecord>
{
    string Id { get; set; }
    string Value { get; set; }
}

public sealed class LockTestRecord : ILockTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public IObjectEnvelope<ILockTestRecord>? Envelope { get; private set; }
}

[StorageCached]
public sealed class CachedLockTestRecord : ILockTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public IObjectEnvelope<ILockTestRecord>? Envelope { get; private set; }
}

[StorageScope(VersionTag = "1.0.0")]
[EncryptedStorage]
public interface IEncryptedFieldTestRecord : IStoredContract<IEncryptedFieldTestRecord>
{
    string Id { get; set; }

    [EncryptedStorageField]
    string Secret { get; set; }
}

public sealed class EncryptedFieldTestRecord : IEncryptedFieldTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public IObjectEnvelope<IEncryptedFieldTestRecord>? Envelope { get; private set; }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IIndexedTestRecord : IStoredContract<IIndexedTestRecord>
{
    string Id { get; set; }
    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix | SearchIndexMode.Contains)]
    string Name { get; set; }
    int Age { get; set; }
    string Country { get; set; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix)]
    string Phone { get; set; }

    [SearchIndex]
    IndexedAddress Address { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class IndexedTestRecord : IIndexedTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Country { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public IndexedAddress Address { get; set; } = new();

    [IgnoreMember]
    public IObjectEnvelope<IIndexedTestRecord>? Envelope { get; private set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class IndexedAddress
{
    public string City { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

[StorageScope(VersionTag = "2.0.0")]
public interface IVersionedTestRecord : IStoredContract<IVersionedTestRecord>
{
    string Id { get; set; }
    string Value { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class VersionedTestRecord : IVersionedTestRecord
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class VersionedMigrationRecord : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "1.0.0";
    public string TargetVersion => "2.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up()
    {
        Value += ":migrated";
        return true;
    }

    public bool Down()
    {
        Value = Value.Replace(":migrated", string.Empty, StringComparison.Ordinal);
        return true;
    }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class VersionedMigrationOneToTwo : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "1.0.0";
    public string TargetVersion => "2.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up()
    {
        Value += ":one";
        return true;
    }

    public bool Down() => true;
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class AlternateVersionedMigrationOneToTwo : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "1.0.0";
    public string TargetVersion => "2.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up() => true;
    public bool Down() => true;
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class VersionedMigrationTwoToThree : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "2.0.0";
    public string TargetVersion => "3.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up()
    {
        Value += ":two";
        return true;
    }

    public bool Down() => true;
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class BackwardVersionedMigration : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "2.0.0";
    public string TargetVersion => "1.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up() => true;
    public bool Down() => true;
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class FailingVersionedMigrationOneToTwo : IVersionedTestRecord, IMigration
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SourceVersion => "1.0.0";
    public string TargetVersion => "2.0.0";

    [IgnoreMember]
    public IObjectEnvelope<IVersionedTestRecord>? Envelope { get; private set; }

    public bool Up() => false;
    public bool Down() => true;
}

public sealed class VersionedTestOptions : IDbContractConfigDto<IVersionedTestRecord>
{
    public VersionedTestOptions(string dataRoot, string activeVersion = "2.0.0")
    {
        DataRoot = dataRoot;
        ActiveVersion = activeVersion;
    }

    public string EnvelopeDICacheKey { get; } = "VersionedTest";
    public string ContractName { get; } = "VersionedTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; }
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        DurabilityMode = StorageDurabilityMode.Durable,
        HydrateOnInitialize = true,
        UseSingleWriteBuffer = true,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10)
    };
}

public sealed class FixedTestKeyProvider : IKeyProvider
{
    private static readonly byte[] Key =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F
    ];

    public ReadOnlyMemory<byte> GetCurrentKey() => Key;
}

public sealed class RotatingTestKeyProvider : IRotatingKeyProvider
{
    public static readonly byte[] Key1 =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F
    ];

    public static readonly byte[] Key2 =
    [
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F
    ];

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal)
    {
        ["key-1"] = Key1,
        ["key-2"] = Key2
    };
    private readonly List<string> _requestedKeyIds = [];
    private string _activeKeyId = "key-1";

    public IReadOnlyList<string> RequestedKeyIds => _requestedKeyIds;

    public string GetActiveKeyId() => _activeKeyId;

    public ReadOnlyMemory<byte> GetCurrentKey() => GetKey(_activeKeyId);

    public ReadOnlyMemory<byte> GetKey(string keyId)
    {
        _requestedKeyIds.Add(keyId);
        return _keys[keyId];
    }

    public void SetActiveKey(string keyId) => _activeKeyId = keyId;

    public void ClearRequests() => _requestedKeyIds.Clear();
}

public sealed class CustomQueryException : Exception
{
    public CustomQueryException(string message)
        : base(message)
    {
    }
}
