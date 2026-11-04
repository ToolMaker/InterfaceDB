using System.Collections.Concurrent;
using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

public sealed partial class StorageMigrationTests
{
    [TestMethod]
    public async Task ReceiptPreflightIncludesEncryptionOverheadWithoutPublishing()
    {
        using var data = new TestData();
        var receipt = new StorageMigrationState("plan", "digest", 0, 0, 0, false);
        var bound = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(receipt).Length + 1;
        var plain = new FileStorageMigrationStateStore(data.ReceiptPath, "messages", new EncryptionNone(), PlatformFilePublication.Instance, bound);
        await plain.ValidateCapacityAsync(receipt, default);
        var encrypted = new FileStorageMigrationStateStore(data.ReceiptPath, "messages", TestData.Encryptor(2), PlatformFilePublication.Instance, bound);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => encrypted.ValidateCapacityAsync(receipt, default).AsTask());
        Assert.IsFalse(File.Exists(data.ReceiptPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReceiptCapacityFailsBeforeDestinationClockBindingOrImports(bool encrypted)
    {
        using var data = new TestData(); await data.SeedAsync(20, encrypted);
        var state = new FileStorageMigrationStateStore(data.ReceiptPath, "messages",
            encrypted ? TestData.Encryptor(2) : new EncryptionNone(), PlatformFilePublication.Instance, maximumReceiptBytes: 1024);
        await using var provider = data.Build(TestData.Options with { CleanupOriginalSourceAfterCompletion = true },
            encrypted: encrypted, state: state);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(provider).InitializeAsync().AsTask());
        Assert.IsFalse(File.Exists(data.ReceiptPath));
        Assert.IsFalse(Directory.Exists(data.DestinationStore()));
        Assert.AreEqual(20, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ReceiptLeaseFailsClosedWhenExclusionCannotBeProven()
    {
        using var data = new TestData();
        var state = new FileStorageMigrationStateStore(data.ReceiptPath, "messages", new EncryptionNone(),
            PlatformFilePublication.Instance, leaseValidation: new RejectReceiptLease());
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => state.AcquireLeaseAsync(default).AsTask());
        Assert.IsFalse(File.Exists(data.ReceiptPath));
        Assert.IsFalse(File.Exists(data.ReceiptPath + ".lock"));
    }

    private sealed class RejectReceiptLease : IFileLeaseValidation
    {
        public void RequireExclusiveHandles(string directory) => throw new NotSupportedException("Injected unsupported locking.");
    }

    [TestMethod]
    public async Task PartialRetry_ImportsAreQuiet_ApplicationWritesStillNotifyAndSearch()
    {
        using var data = new TestData(); await data.SeedAsync(2); await SeedLegacyAsync(data, 2);
        await using var provider = data.Build(PartialOptions);
        var repository = data.Registered(provider);
        await repository.InitializeAsync();
        var seen = new ConcurrentQueue<string>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = ((IRepositoryNotifications<IMigrationRecord>)repository).SubscribeCreated(record =>
        {
            seen.Enqueue(record.Id);
            if (record.Id == "after") delivered.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await subscription.Ready;
        await ((IStorageMigrationProgress)repository).RetryAsync();
        Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
        Assert.IsNotNull(await repository.QueryFirstAsync(record => record.Id == "legacy-1"));
        await repository.SaveAsync(new MigrationRecord { Id = "after", Score = 42 });
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { "after" }, seen.ToArray());
        Assert.AreEqual("after", (await repository.QueryFirstAsync(record => record.Score == 42))!.Id);

        // A quiet import must invalidate the notification baseline, not disable later changes.
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var changes = ((IRepositoryNotifications<IMigrationRecord>)repository).SubscribeChanged(change =>
        { if (change.Entity.Id == "legacy-1") changed.TrySetResult(); return ValueTask.CompletedTask; }, record => true);
        await changes.Ready;
        var imported = (await repository.QueryFirstAsync(record => record.Id == "legacy-1"))!;
        imported.Name = "application edit";
        await repository.SaveAsync(imported);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task DecoratedEndpoint_WithoutOptionalServices_CannotBypassAliasGate()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using var provider = data.Build(wrapSource: source => new NoOptionalServices(source));
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>());
        await data.Registered(provider).InitializeAsync();
        Assert.AreSame(provider.GetRequiredKeyedService<IEnvelopeRepository<IMigrationRecord>>("new"),
            provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>());
    }

    [TestMethod]
    public async Task DecoratedEndpoint_WithoutCleanupCapability_RejectsCleanupBeforeReceipt()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using var provider = data.Build(TestData.Options with { CleanupOriginalSourceAfterCompletion = true },
            wrapSource: source => new NoOptionalServices(source));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => data.Registered(provider).InitializeAsync().AsTask());
        Assert.IsFalse(File.Exists(data.ReceiptPath));
        Assert.AreEqual(2, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
    }

    private sealed class NoOptionalServices(IStorageMigrationSource<IMigrationRecord> inner) : IStorageMigrationSource<IMigrationRecord>
    {
        public string Identity => inner.Identity;
        public string ContractVersion => inner.ContractVersion;
        public string? StorageDirectory => inner.StorageDirectory;
        public string? ServiceRegistrationKey => inner.ServiceRegistrationKey;
        public IRepository<IMigrationRecord> Repository => inner.Repository;
        public ValueTask InitializeSnapshotAsync(CancellationToken ct) => inner.InitializeSnapshotAsync(ct);
        public IAsyncEnumerable<StorageMigrationRecord<IMigrationRecord>> ReadAllAsync(int max, CancellationToken ct) => inner.ReadAllAsync(max, ct);
        public ValueTask<StorageMigrationRecord<IMigrationRecord>?> ReadAsync(string key, int max, CancellationToken ct) => inner.ReadAsync(key, max, ct);
        public ValueTask<long> ReadRevisionFloorAsync(CancellationToken ct) => inner.ReadRevisionFloorAsync(ct);
        public ValueTask<IAsyncDisposable> AcquireSourceLeaseAsync(CancellationToken ct) => inner.AcquireSourceLeaseAsync(ct);
    }
}
