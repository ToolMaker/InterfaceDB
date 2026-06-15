using System.Diagnostics;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Implementation.Extentions;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

if (args is ["--child", var dataRoot, var gatePath, var readyPath, var writerName])
{
    await RunChildWriterAsync(dataRoot, gatePath, readyPath, writerName).ConfigureAwait(false);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("single save cleans write artifacts", SingleSaveCleansSidecarArtifactsAsync),
    ("single cardinality rejects detached duplicates and permits loaded updates", SingleCardinalityRejectsDetachedDuplicatesAsync),
    ("single cardinality is atomic across repository instances", SingleCardinalityIsAtomicAcrossRepositoriesAsync),
    ("multiple cardinality creates distinct physical objects", MultipleCardinalityCreatesDistinctObjectsAsync),
    ("repository query cardinality methods enforce result bounds", RepositoryQueryCardinalityMethodsAsync),
    ("two processes can write the same payload", TwoProcessesWriteSamePayloadAsync),
    ("losing proposal returns and caches canonical winner", LosingProposalReturnsAndCachesCanonicalWinnerAsync),
    ("initialization commits newest abandoned proposal", InitializationCommitsNewestAbandonedProposalAsync),
    ("eager migration drains proposals before migrating", EagerMigrationDrainsProposalsBeforeMigratingAsync),
    ("encrypted storage round trips and rejects tampering", EncryptedStorageRoundTripsAndRejectsTamperingAsync),
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

static async Task<bool> TrySaveAsync(
    IRepository<ILockTestRecord> repository,
    ILockTestRecord record)
{
    try
    {
        await repository.SaveAsync(record).ConfigureAwait(false);
        return true;
    }
    catch (StorageObjectAlreadyExistsException)
    {
        return false;
    }
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

        Assert(childA.ExitCode == 0, $"writer-a failed with exit code {childA.ExitCode}.{Environment.NewLine}{outputA}{Environment.NewLine}{errorA}");
        Assert(childB.ExitCode == 0, $"writer-b failed with exit code {childB.ExitCode}.{Environment.NewLine}{outputB}{Environment.NewLine}{errorB}");
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
        var lifecycle = provider.GetRequiredService<Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<ILockTestRecord>>();
        await WriteReadyProposalAsync(
            serializer,
            lifecycle,
            payloadPath,
            "canonical-winner",
            "9999999999999999999",
            record.Envelope.EnvelopeCacheKey).ConfigureAwait(false);

        record.Value = "losing-write";
        var saved = await repository.SaveAsync(record).ConfigureAwait(false);

        Assert(saved.Value == "canonical-winner", "Save should return the proposal that became canonical.");
        Assert(record.Envelope is not null, "The losing input object should retain its storage envelope.");

        var cached = await repository.QueryAsync(x => x.Id == "shared").ConfigureAwait(false);
        cached = AssertNotNull(cached, "Canonical winner should remain queryable.");
        Assert(cached.Value == "canonical-winner", "Cache should contain the canonical winner, not the losing write.");
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
            var lifecycle = seedProvider.GetRequiredService<Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<ILockTestRecord>>();

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
            var lifecycle = writerProvider.GetRequiredService<Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();

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
        Assert(stored.AsSpan().IndexOf("IDB2"u8) < 0, "Plain IDB2 header should not be visible in encrypted storage.");

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
            var lifecycle = writerProvider.GetRequiredService<Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<IVersionedTestRecord>>();
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
    Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<ILockTestRecord> lifecycle,
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
    Idb.Libraries.Abstraction.Services.IEnvelopeLifecycle<IVersionedTestRecord> lifecycle,
    string payloadPath,
    string value,
    string order)
{
    var record = new VersionedTestRecord { Id = "shared", Value = value };
    var envelope = await lifecycle.WrapAsync(record).ConfigureAwait(false);
    envelope.EnvelopeCacheKey = "shared";
    envelope.Location = payloadPath;
    envelope.VersionTag = "1.0.0";

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
    await repository.SaveAsync(record).ConfigureAwait(false);
    Console.WriteLine($"{writerName} saved {record.Value.Length} bytes to {record.Envelope?.Location}");
}

static ServiceProvider BuildProvider(string dataRoot)
{
    var options = new LockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddRepository<ILockTestRecord>(options);
    return services.BuildServiceProvider();
}

static ServiceProvider BuildSingleProvider(string dataRoot)
{
    var options = new SingleLockTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddSingleton<IEnvelopeKeyAndLocationFactory<ILockTestRecord>>(
        new LockTestKeyAndLocationFactory(options));
    services.AddRepository<ILockTestRecord>(options);
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

static ServiceProvider BuildMigrationProvider(string dataRoot)
{
    var options = new VersionedTestOptions(dataRoot);
    var services = new ServiceCollection();
    services.AddSingleton<IMigration>(new VersionedMigrationRecord());
    services.AddRepository<IVersionedTestRecord>(options, MigrationStrategyType.EagerStrict);
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
    public IIndex<ILockTestRecord>[]? Indexes { get; } = Array.Empty<IIndex<ILockTestRecord>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; } = IndexProvisioningModeType.None;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        CacheEnvelopeOnWrite = true,
        HydrateOnInitialize = true,
        KeepObjectInstanceInCacheOnWrite = true,
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
    public IIndex<ILockTestRecord>[]? Indexes { get; } = Array.Empty<IIndex<ILockTestRecord>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; } = IndexProvisioningModeType.None;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Single;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        WatchStorageChanges = false,
        WriteLockTimeout = TimeSpan.FromSeconds(15),
        WriteLockRetryDelay = TimeSpan.FromMilliseconds(10)
    };
}

public sealed class LockTestKeyAndLocationFactory : IEnvelopeKeyAndLocationFactory<ILockTestRecord>
{
    private readonly SingleLockTestOptions _options;

    public LockTestKeyAndLocationFactory(SingleLockTestOptions options)
    {
        _options = options;
    }

    public StorageObjectCardinality ObjectCardinality => _options.ObjectCardinality;

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

public sealed class VersionedTestOptions : IDbContractConfigDto<IVersionedTestRecord>
{
    public VersionedTestOptions(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string EnvelopeDICacheKey { get; } = "VersionedTest";
    public string ContractName { get; } = "VersionedTest";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "2.0.0";
    public IIndex<IVersionedTestRecord>[]? Indexes { get; } = Array.Empty<IIndex<IVersionedTestRecord>>();
    public IndexProvisioningModeType IndexProvisioningMode { get; } = IndexProvisioningModeType.None;
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
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

public sealed class CustomQueryException : Exception
{
    public CustomQueryException(string message)
        : base(message)
    {
    }
}
