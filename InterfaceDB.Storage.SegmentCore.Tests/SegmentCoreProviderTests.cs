using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCoreProviderTests
{
    [TestMethod]
    public async Task Registration_ResolvesStoragePolicyHardwareProfileIntoConcreteSegmentCoreOptions()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
        {
            MaxReadConcurrency = 6
        };
        var config = new ProviderConfig(root.Path, policy);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IProviderRecord>(
            config,
            storagePolicy: policy,
            storageIdentityFactory: new ProviderIdentityFactory());

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SegmentCoreRepositoryOptions>();
        var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();

        Assert.AreEqual(StorageHardwareProfile.WindowsNvme, options.HardwareProfile);
        Assert.AreEqual(ObjectLogDurabilityStrategy.WriteThrough, options.StoreOptions.LogOptions.DurabilityStrategy);
        Assert.AreEqual(8, options.StoreOptions.LogOptions.StripeCount);
        Assert.AreEqual(6, options.StoreOptions.ReadOptions.MaximumParallelReads);
        Assert.AreSame(policy, maintenance.StoragePolicy);
        Assert.AreSame(options, maintenance.Options);
        Assert.AreEqual(StorageHardwareProfile.WindowsNvme, maintenance.Status.HardwareProfile);
    }

    [TestMethod]
    public async Task Registration_ExplicitSegmentCoreOptionsOverrideStoragePolicyProfile()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme);
        var explicitOptions = ProviderOptions();
        var config = new ProviderConfig(root.Path, policy);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IProviderRecord>(
            config,
            explicitOptions,
            policy,
            new ProviderIdentityFactory());

        await using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<SegmentCoreRepositoryOptions>();

        Assert.AreSame(explicitOptions, resolved);
        Assert.AreEqual(StorageHardwareProfile.Custom, resolved.HardwareProfile);
        Assert.AreEqual(ObjectLogDurabilityStrategy.ExplicitFlush, resolved.StoreOptions.LogOptions.DurabilityStrategy);
        Assert.AreEqual(4, resolved.StoreOptions.LogOptions.StripeCount);
    }

    [TestMethod]
    public void HardwareProfiles_ProduceCompleteValidatedConfigurations()
    {
        foreach (var profile in Enum.GetValues<StorageHardwareProfile>())
        {
            var policy = StoragePolicy.ForHardwareProfile(profile);
            var options = SegmentCoreRepositoryOptions.ForHardwareProfile(profile);

            Assert.AreEqual(profile, policy.HardwareProfile);
            Assert.AreEqual(profile, options.HardwareProfile);
            Assert.IsGreaterThan(0, policy.MaxConcurrency);
            Assert.IsGreaterThan(0, policy.MaxReadConcurrency);
            Assert.IsNotNull(options.StoreOptions);
            Assert.IsNotNull(options.StoreOptions.LogOptions);
            Assert.IsNotNull(options.StoreOptions.ReadOptions);
            Assert.IsNotNull(options.CompactionOptions);
        }

        var lowMemory = SegmentCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.LowMemory);
        var highThroughput = SegmentCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.HighThroughput);
        Assert.AreEqual(2, lowMemory.StoreOptions.LogOptions.StripeCount);
        Assert.IsLessThan(
            highThroughput.StoreOptions.LogOptions.QueueCapacityPerStripe,
            lowMemory.StoreOptions.LogOptions.QueueCapacityPerStripe);
        Assert.AreEqual(
            ObjectLogDurabilityStrategy.ExplicitFlush,
            highThroughput.StoreOptions.LogOptions.DurabilityStrategy);
    }

    [TestMethod]
    public void Registration_CustomProfileWithoutExplicitProviderOptions_IsRejected()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.Custom);
        var config = new ProviderConfig(root.Path, policy);
        var services = new ServiceCollection();

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddSegmentCoreRepository<IProviderRecord>(
                config,
                storagePolicy: policy,
                storageIdentityFactory: new ProviderIdentityFactory()));
    }

    [TestMethod]
    public void Registration_InvalidPhysicalReadOrWriteTuning_FailsAtCompositionRoot()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var invalidReads = ProviderOptions() with
        {
            StoreOptions = ProviderOptions().StoreOptions with
            {
                ReadOptions = new SegmentCoreReadOptions { MaximumParallelReads = 0 }
            }
        };
        var invalidWrites = ProviderOptions() with
        {
            StoreOptions = ProviderOptions().StoreOptions with
            {
                LogOptions = ProviderOptions().StoreOptions.LogOptions with
                {
                    MaximumCombinedWriteBytes = 0
                }
            }
        };
        var invalidPointReads = ProviderOptions() with
        {
            StoreOptions = ProviderOptions().StoreOptions with
            {
                ReadOptions = new SegmentCoreReadOptions
                {
                    SynchronousPointReadMaximumBytes = -1
                }
            }
        };

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddSegmentCoreRepository<IProviderRecord>(config, invalidReads));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddSegmentCoreRepository<IProviderRecord>(config, invalidWrites));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddSegmentCoreRepository<IProviderRecord>(config, invalidPointReads));
    }

    [TestMethod]
    public async Task HardwareProfiles_WriteCheckpointAndReopenWithoutChangingTheStorageContract()
    {
        using var root = new ProviderTemporaryDirectory();
        var profiles = new[]
        {
            StorageHardwareProfile.Portable,
            StorageHardwareProfile.LowMemory,
            StorageHardwareProfile.HighThroughput,
            StorageHardwareProfile.WindowsNvme
        };

        foreach (var profile in profiles)
        {
            var profileRoot = Path.Combine(root.Path, profile.ToString());
            var options = SegmentCoreRepositoryOptions.ForHardwareProfile(profile).StoreOptions;
            await using (var store = await SegmentCoreStore.OpenAsync(profileRoot, options))
            {
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync("profile-key", Payload((int)profile, 7));
                await transaction.CommitAsync();
                _ = await store.CheckpointAsync();
            }

            await using var reopened = await SegmentCoreStore.OpenAsync(profileRoot, options);
            var payload = await reopened.GetAsync("profile-key");
            Assert.IsNotNull(payload, $"Profile {profile} failed to reopen its committed object.");
            Assert.HasCount(256, payload);
        }
    }

    [TestMethod]
    public async Task Repository_BulkRoundTripDeleteCheckpointAndReopen_PreservesObjects()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var options = ProviderOptions();
        string deletedKey;

        await using (var provider = BuildProvider(config, options))
        {
            var repository = provider.GetRequiredService<IRepository<IProviderRecord>>();
            await repository.InitializeAsync();
            var saved = await repository.SaveManyAsync(
                Enumerable.Range(0, 100).Select(index => (IProviderRecord)new ProviderRecord
                {
                    Id = $"record-{index:D3}",
                    Name = index % 2 == 0 ? $"Alpha {index}" : $"Beta {index}",
                    Score = index
                }));

            Assert.AreEqual(100, saved.Count);
            saved[0].Name = "Alpha updated";
            _ = await repository.SaveAsync(saved[0]);
            var match = await repository.SearchFirstAsync(item => item.Name.StartsWith("Alpha") && item.Score >= 90);
            Assert.IsNotNull(match);
            Assert.IsGreaterThanOrEqualTo(90, match.Score);

            deletedKey = saved[10].Envelope!.EnvelopeCacheKey;
            Assert.IsTrue(await repository.DeleteAsync(saved[10]));

            var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();
            var checkpoint = await maintenance.CheckpointAsync();
            Assert.IsGreaterThan(0, checkpoint.Generation);
            var integrity = await maintenance.VerifyIntegrityAsync();
            Assert.IsTrue(integrity.IsValid, string.Join(Environment.NewLine, integrity.Failures));
            Assert.AreEqual(99, integrity.EntriesChecked);
        }

        await using (var reopenedProvider = BuildProvider(config, options))
        {
            var repository = reopenedProvider.GetRequiredService<IRepository<IProviderRecord>>();
            await repository.InitializeAsync();
            var records = new List<IProviderRecord>();
            await foreach (var record in repository.QueryManyAsync(static _ => true))
            {
                records.Add(record);
            }

            Assert.AreEqual(99, records.Count);
            Assert.IsFalse(records.Any(record => record.Envelope?.EnvelopeCacheKey == deletedKey));
            Assert.AreEqual("Alpha updated", records.Single(record => record.Id == "record-000").Name);
            Assert.AreEqual("Alpha 98", records.Single(record => record.Id == "record-098").Name);
            Assert.AreEqual(ContractRepositoryType.SegmentCore, repository.RepositoryType);
        }
    }

    [TestMethod]
    public async Task Repository_ExplicitProviderNativeMigration_IsTransactionalResumableAndKeepsSource()
    {
        using var root = new ProviderTemporaryDirectory();
        var options = ProviderOptions();
        var sourceConfig = new ProviderConfig(root.Path, activeVersion: "1.0.0");
        var sourceRoot = Path.Combine(
            root.Path,
            options.DirectoryName,
            "1.0.0");
        var serializer = new BinaryObjectSerializer<IProviderRecord>();
        await using (var sourceStore = await SegmentCoreStore.OpenAsync(sourceRoot, options.StoreOptions))
        {
            var legacy = new ProviderRecordMigration
            {
                Id = "legacy-001",
                Name = "Legacy",
                Score = 7
            };
            var envelope = new FileObjectEnvelope<IProviderRecord>(sourceConfig)
            {
                EnvelopeCacheKey = "legacy-001-0",
                VersionTag = "1.0.0",
                Location = "legacy-001-0",
                ObjectInstance = legacy
            };
            await sourceStore.SaveAsync(envelope.EnvelopeCacheKey, serializer.Serialize(envelope));
            _ = await sourceStore.CheckpointAsync();
        }

        var targetConfig = new ProviderConfig(root.Path, activeVersion: "2.0.0");
        var services = new ServiceCollection();
        services.AddSingleton<IMigration, ProviderRecordMigration>();
        services.AddSegmentCoreRepository<IProviderRecord>(
            targetConfig,
            options,
            targetConfig.StoragePolicy,
            new ProviderIdentityFactory());
        await using var provider = services.BuildServiceProvider();
        var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();
        var first = await maintenance.MigrateFromAsync(sourceRoot, "1.0.0");
        Assert.AreEqual(1, first.Examined);
        Assert.AreEqual(1, first.Migrated);
        Assert.AreEqual(0, first.AlreadyPresent);
        Assert.IsEmpty(first.Failures);
        Assert.IsTrue(first.Checkpoint.HasValue);

        var repository = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repository.InitializeAsync();
        var migrated = await repository.SearchFirstAsync(record => record.Id == "legacy-001");
        Assert.IsNotNull(migrated);
        Assert.AreEqual("Legacy migrated", migrated.Name);
        Assert.AreEqual(107, migrated.Score);

        var second = await maintenance.MigrateFromAsync(sourceRoot, "1.0.0");
        Assert.AreEqual(0, second.Migrated);
        Assert.AreEqual(1, second.AlreadyPresent);
        Assert.IsTrue(Directory.Exists(sourceRoot), "Provider-native migration must retain its source store.");
    }

    [TestMethod]
    public async Task Repository_HydrationDisabled_SeedsMetadataWithoutDeserializingPayloads()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await repository.WriteManyAsync(CreateRecords(32));
            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var reader = readerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();

        await reader.InitializeAsync();

        Assert.AreEqual(0, serializer.DeserializeCount);
        Assert.IsTrue(reader.Exists("record"));
        Assert.AreEqual(0, serializer.DeserializeCount);

        var count = 0;
        await foreach (var _ in reader.EnumerateAsync())
        {
            count++;
        }

        Assert.AreEqual(32, count);
        Assert.AreEqual(32, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_StrongHydration_EnumerationUsesCacheWithoutSecondDeserializePass()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await repository.WriteManyAsync(CreateRecords(40));
            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var reader = readerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();

        await reader.InitializeAsync();
        Assert.AreEqual(40, serializer.DeserializeCount);
        serializer.Reset();

        var count = 0;
        await foreach (var _ in reader.EnumerateAsync())
        {
            count++;
        }

        Assert.AreEqual(40, count);
        Assert.AreEqual(0, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_IndexedMetadataStartup_DeserializesEachPayloadOnlyOnce()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await repository.WriteManyAsync(CreateRecords(36));
            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        var firstSerializer = new CountingObjectSerializer();
        await using (var firstReaderProvider = BuildProvider(config, options, serializer: firstSerializer))
        {
            var repositoryWithIndexes = firstReaderProvider.GetRequiredService<IRepository<IProviderRecord>>();
            await repositoryWithIndexes.InitializeAsync();
            Assert.AreEqual(36, firstSerializer.DeserializeCount);
        }

        var catalog = Directory.EnumerateFiles(
            Path.Combine(root.Path, options.DirectoryName, "1.0.0", "search-indexes"),
            "*.idbsidx").Single();
        Assert.IsGreaterThan(0L, new FileInfo(catalog).Length);

        var secondSerializer = new CountingObjectSerializer();
        await using var secondReaderProvider = BuildProvider(config, options, serializer: secondSerializer);
        var reopenedIndexes = secondReaderProvider.GetRequiredService<IRepository<IProviderRecord>>();
        await reopenedIndexes.InitializeAsync();

        Assert.AreEqual(0, secondSerializer.DeserializeCount);
        var matches = new List<IProviderRecord>();
        await foreach (var record in reopenedIndexes.SearchManyAsync(record => record.Score >= 30))
        {
            matches.Add(record);
        }

        Assert.HasCount(6, matches);
        Assert.AreEqual(6, secondSerializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_IndexedMetadataStartup_RejectsStaleCatalogAfterDirectStorageMutation()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await repository.WriteManyAsync(CreateRecords(12));
            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        await using (var catalogProvider = BuildProvider(config, options))
        {
            await catalogProvider.GetRequiredService<IRepository<IProviderRecord>>().InitializeAsync();
        }

        await using (var directWriterProvider = BuildProvider(config, options))
        {
            var directWriter = directWriterProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await directWriter.WriteAsync(new ProviderRecord
            {
                Id = "record-stale-catalog",
                Name = "Stale catalog record",
                Score = 1_000
            });
            _ = await directWriterProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var repositoryWithIndexes = readerProvider.GetRequiredService<IRepository<IProviderRecord>>();
        await repositoryWithIndexes.InitializeAsync();

        Assert.AreEqual(13, serializer.DeserializeCount);
        serializer.Reset();

        var matches = new List<IProviderRecord>();
        await foreach (var record in repositoryWithIndexes.SearchManyAsync(record => record.Score >= 1_000))
        {
            matches.Add(record);
        }

        Assert.HasCount(1, matches);
        Assert.AreEqual("record-stale-catalog", matches[0].Id);
        Assert.AreEqual(1, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_IndexedMetadataStartup_RebuildsCorruptDerivedCatalog()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = true,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(config, options))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            _ = await repository.WriteManyAsync(CreateRecords(16));
            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        await using (var catalogProvider = BuildProvider(config, options))
        {
            await catalogProvider.GetRequiredService<IRepository<IProviderRecord>>().InitializeAsync();
        }

        var catalog = Directory.EnumerateFiles(
            Path.Combine(root.Path, options.DirectoryName, "1.0.0", "search-indexes"),
            "*.idbsidx").Single();
        await File.WriteAllBytesAsync(catalog, [0x49, 0x44, 0x42, 0x00, 0xFF]);

        var serializer = new CountingObjectSerializer();
        await using var readerProvider = BuildProvider(config, options, serializer: serializer);
        var repositoryWithIndexes = readerProvider.GetRequiredService<IRepository<IProviderRecord>>();
        await repositoryWithIndexes.InitializeAsync();

        Assert.AreEqual(16, serializer.DeserializeCount);
        serializer.Reset();

        var matches = new List<IProviderRecord>();
        await foreach (var record in repositoryWithIndexes.SearchManyAsync(record => record.Score >= 14))
        {
            matches.Add(record);
        }

        Assert.HasCount(2, matches);
        Assert.AreEqual(2, serializer.DeserializeCount);
    }

    [TestMethod]
    public async Task Repository_Reopen_RebuildsSuffixHighWaterFromHighestDurableKey()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
        var config = new ProviderConfig(root.Path, policy);
        var options = ProviderOptions();

        await using (var writerProvider = BuildProvider(
            config,
            options,
            identityFactory: new SharedIdentityFactory()))
        {
            var repository = writerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
            var saved = await repository.WriteManyAsync(CreateRecords(128));
            for (var index = 64; index < 127; index++)
            {
                Assert.IsTrue(await repository.DeleteAsync(saved[index]));
            }

            _ = await writerProvider
                .GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>()
                .CheckpointAsync();
        }

        await using var readerProvider = BuildProvider(
            config,
            options,
            identityFactory: new SharedIdentityFactory());
        var reader = readerProvider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
        var savedAfterReopen = await reader.WriteAsync(new ProviderRecord
        {
            Id = "after-reopen",
            Name = "After reopen",
            Score = 999
        });

        Assert.AreEqual("shared-128", savedAfterReopen.Envelope!.EnvelopeCacheKey);
    }

    [TestMethod]
    public async Task Registration_EncryptionNoneBypassesEncryptingSerializer()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IProviderRecord>(
            config,
            ProviderOptions(),
            config.StoragePolicy,
            new ProviderIdentityFactory());

        await using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IObjectSerializer<IProviderRecord>>();

        Assert.IsInstanceOfType(serializer, typeof(BinaryObjectSerializer<IProviderRecord>));
    }

    [TestMethod]
    public async Task Registration_CustomEncryptorKeepsEncryptingSerializer()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IProviderRecord>(
            config,
            ProviderOptions(),
            config.StoragePolicy,
            new ProviderIdentityFactory(),
            new CopyingEncryptor());

        await using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IObjectSerializer<IProviderRecord>>();

        Assert.IsInstanceOfType(serializer, typeof(EncryptingObjectSerializer<IProviderRecord>));
    }

    [TestMethod]
    public async Task Store_BoundedCompactionAndIntegrity_AllowConcurrentWritesBetweenSlices()
    {
        using var root = new ProviderTemporaryDirectory();
        var options = ProviderOptions().StoreOptions;
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, options);
        for (var round = 0; round < 4; round++)
        {
            await using var transaction = await store.BeginTransactionAsync();
            for (var index = 0; index < 300; index++)
            {
                await transaction.SaveAsync($"key-{index:D4}", Payload(round, index));
            }

            await transaction.CommitAsync();
        }

        var compaction = await store.CompactAsync(new SegmentCoreCompactionOptions
        {
            MaximumRecordsPerSlice = 31,
            DelayBetweenSlices = TimeSpan.FromMilliseconds(1)
        });

        Assert.IsGreaterThan(2, compaction.Slices);
        Assert.IsGreaterThan(0, compaction.BytesReclaimed);
        var integrity = await store.VerifyIntegrityAsync();
        Assert.IsTrue(integrity.IsValid, string.Join(Environment.NewLine, integrity.Failures));
        Assert.AreEqual(300, integrity.EntriesChecked);
    }

    private static ServiceProvider BuildProvider(
        ProviderConfig config,
        SegmentCoreRepositoryOptions options,
        IEnvelopeStorageIdentityFactory<IProviderRecord>? identityFactory = null,
        IObjectSerializer<IProviderRecord>? serializer = null)
    {
        var services = new ServiceCollection();
        if (serializer is not null)
        {
            services.AddKeyedSingleton(config.EnvelopeDICacheKey, serializer);
        }

        services.AddSegmentCoreRepository<IProviderRecord>(
            config,
            options,
            config.StoragePolicy,
            identityFactory ?? new ProviderIdentityFactory());
        return services.BuildServiceProvider();
    }

    private static IEnumerable<IProviderRecord> CreateRecords(int count) =>
        Enumerable.Range(0, count).Select(index => (IProviderRecord)new ProviderRecord
        {
            Id = $"record-{index:D6}",
            Name = $"Record {index}",
            Score = index
        });

    private static SegmentCoreRepositoryOptions ProviderOptions() => new()
    {
        HardwareProfile = StorageHardwareProfile.Custom,
        EnableBackgroundMaintenance = false,
        CheckpointOnDispose = true,
        StoreOptions = new SegmentCoreOptions
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            CheckpointGenerationsToKeep = 2,
            LogOptions = new StripedObjectLogProbeOptions
            {
                StripeCount = 4,
                MaximumBatchRecords = 64,
                MaxSegmentBytes = 16 * 1024,
                DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
            }
        }
    };

    private static byte[] Payload(int round, int index) =>
        Enumerable.Repeat((byte)(round + index), 256).ToArray();

    private sealed class ProviderConfig(
        string root,
        StoragePolicy? storagePolicy = null,
        string activeVersion = "1.0.0") : IDbContractConfigDto<IProviderRecord>
    {
        public string EnvelopeDICacheKey => "segment-provider-test";
        public string ContractName => "ProviderRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => activeVersion;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy { get; } = storagePolicy
            ?? Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
            {
                ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
                RaiseNotifications = true,
                HydrateOnInitialize = false
            };
    }

    private sealed class ProviderIdentityFactory : IEnvelopeStorageIdentityFactory<IProviderRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

        public void InitEnvelope(IObjectEnvelope<IProviderRecord> envelope)
        {
            envelope.EnvelopeCacheKey = envelope.ObjectInstance?.Id
                ?? throw new InvalidOperationException("Record id is required.");
            envelope.Location ??= envelope.EnvelopeCacheKey;
        }
    }

    private sealed class SharedIdentityFactory : IEnvelopeStorageIdentityFactory<IProviderRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

        public void InitEnvelope(IObjectEnvelope<IProviderRecord> envelope)
        {
            envelope.EnvelopeCacheKey = "shared";
            envelope.Location = envelope.EnvelopeCacheKey;
        }
    }

    private sealed class CountingObjectSerializer : IObjectSerializer<IProviderRecord>
    {
        private readonly BinaryObjectSerializer<IProviderRecord> _inner = new();
        private int _deserializeCount;

        public int DeserializeCount => Volatile.Read(ref _deserializeCount);

        public byte[] Serialize(IObjectEnvelope<IProviderRecord> envalope) => _inner.Serialize(envalope);

        public IObjectEnvelope<IProviderRecord> Deserialize(byte[] data)
        {
            Interlocked.Increment(ref _deserializeCount);
            return _inner.Deserialize(data);
        }

        public ValueTask SerializeAsync(
            IObjectEnvelope<IProviderRecord> envalope,
            Stream destination,
            CancellationToken cancellationToken = default)
            => _inner.SerializeAsync(envalope, destination, cancellationToken);

        public async ValueTask<IObjectEnvelope<IProviderRecord>> DeserializeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deserializeCount);
            return await _inner.DeserializeAsync(source, cancellationToken);
        }

        public void Reset() => Interlocked.Exchange(ref _deserializeCount, 0);
    }

    private sealed class CopyingEncryptor : IEncryptor
    {
        public byte[] Encrypt(ReadOnlySpan<byte> plain) => plain.ToArray();

        public byte[] Decrypt(ReadOnlySpan<byte> cipher) => cipher.ToArray();
    }

    private sealed class ProviderTemporaryDirectory : IDisposable
    {
        public ProviderTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "interface-db-segment-provider-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IProviderRecord : IStoredContract<IProviderRecord>
{
    string Id { get; set; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix)]
    string Name { get; set; }

    [SearchIndex]
    int Score { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class ProviderRecord : IProviderRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<IProviderRecord>? Envelope { get; private set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class ProviderRecordMigration : IProviderRecord, IMigration
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<IProviderRecord>? Envelope { get; private set; }

    [IgnoreMember]
    public string SourceVersion => "1.0.0";

    [IgnoreMember]
    public string TargetVersion => "2.0.0";

    public bool Up()
    {
        Name += " migrated";
        Score += 100;
        return true;
    }

    public bool Down()
    {
        Name = Name.EndsWith(" migrated", StringComparison.Ordinal)
            ? Name[..^" migrated".Length]
            : Name;
        Score -= 100;
        return true;
    }
}
