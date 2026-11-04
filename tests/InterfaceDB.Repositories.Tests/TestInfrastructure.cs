using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace InterfaceDB.Commercial.Repositories.Tests;

internal class TestEntity : IStoredContract<TestEntity>
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    public IObjectEnvelope<TestEntity>? Envelope { get; set; }
}

internal sealed class TestEnvelope : IObjectEnvelope<TestEntity>
{
    public long RevisionTimestampUtcMicroseconds { get; set; }
    public string EnvelopeCacheKey { get; set; } = string.Empty;

    public string VersionTag { get; set; } = "1.0.0";

    public TestEntity? ObjectInstance { get; set; }

    public WeakReference<TestEntity>? ObjectInstanceWeakReference { get; set; }

    public string? ImplementationTypeName { get; set; } = typeof(TestEntity).AssemblyQualifiedName;

    public bool CacheObjectInstance { get; set; }

    public IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }
        = new Dictionary<string, EncryptedFieldValue>();

    public byte[]? ExtensionMetadata { get; set; }

    public string? Location { get; set; }

    public StorageObjectState State { get; set; } = StorageObjectState.Active;
}

internal sealed class TestEntityMapper : IResilientEntityMapper<TestEntity>
{
    public string GetStorageKey(TestEntity entity) => entity.Key;

    public TestEntity CreateDetachedCopy(TestEntity source)
        => new() { Key = source.Key, Value = source.Value, Tags = [.. source.Tags] };

    public void CopyValues(TestEntity source, TestEntity target)
    {
        target.Key = source.Key;
        target.Value = source.Value;
        target.Tags = [.. source.Tags];
    }
}

internal sealed class FakeMetadataRepository : IEnvelopeMetadataRepository<TestEntity>
{
    private readonly object _gate = new();
    private readonly Idb.Libraries.Implementation.Serialization.MonotonicRevisionTimestampGenerator _clock = new();
    private readonly Dictionary<string, TestEntity> _items = new(StringComparer.Ordinal);
    private readonly string _owner;
    private readonly Func<string, string> _envelopeKeyFactory;

    internal FakeMetadataRepository(
        string owner,
        StorageDurabilityMode commitDurability = StorageDurabilityMode.Durable,
        Func<string, string>? envelopeKeyFactory = null)
    {
        _owner = owner;
        CommitDurability = commitDurability;
        _envelopeKeyFactory = envelopeKeyFactory ?? (static key => key);
    }

    public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;

    public StorageDurabilityMode CommitDurability { get; }

    internal int InitializeCallCount { get; private set; }

    internal int SaveCallCount { get; private set; }

    internal int MetadataSaveCallCount { get; private set; }

    internal int QueryFirstByStorageKeyCallCount { get; private set; }

    internal int QueryFirstCallCount { get; private set; }

    internal int SearchFirstCallCount { get; private set; }

    internal int QueryManyCallCount { get; private set; }

    internal int SearchManyCallCount { get; private set; }

    internal int DeleteCallCount { get; private set; }

    internal Exception? QueryFirstByStorageKeyException { get; set; }

    internal Func<string, TestEntity?>? QueryFirstByStorageKeyOverride { get; set; }

    internal Exception? MetadataSaveException { get; set; }

    internal Exception? EnumerationException { get; set; }

    internal Func<TestEntity, string?, byte[]?, CancellationToken, ValueTask>? BeforeMetadataSaveAsync { get; set; }

    internal IReadOnlyCollection<TestEntity> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.Values.ToArray();
            }
        }
    }

    internal TestEntity? Get(string key)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(key);
        }
    }

    internal void Seed(
        string key,
        string value,
        ResilientEnvelopeMetadata? metadata = null,
        IEnumerable<string>? tags = null)
    {
        var encodedMetadata = ResilientEnvelopeMetadataCodec.Encode(
            metadata ?? ResilientEnvelopeMetadata.Empty);
        var entity = CreateOwnedEntity(key, value, encodedMetadata, envelopeCacheKey: null, tags);
        lock (_gate)
        {
            _items[key] = entity;
        }
    }

    internal void SeedRawMetadata(string key, string value, byte[]? metadata)
    {
        var entity = CreateOwnedEntity(key, value, metadata, envelopeCacheKey: null);
        lock (_gate)
        {
            _items[key] = entity;
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCallCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TestEntity> SaveAsync(
        TestEntity entityToSave,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCallCount++;
        var existing = Get(entityToSave.Key);
        var detached = new TestEntity { Key = entityToSave.Key, Value = entityToSave.Value };
        return SaveWithExtensionMetadataAsync(
            detached,
            existing?.Envelope?.EnvelopeCacheKey,
            existing?.Envelope?.ExtensionMetadata,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<TestEntity>> SaveManyAsync(
        IEnumerable<TestEntity> entitiesToSave,
        CancellationToken cancellationToken = default)
    {
        var saved = new List<TestEntity>();
        foreach (var entity in entitiesToSave)
        {
            saved.Add(await SaveAsync(entity, cancellationToken));
        }

        return saved;
    }

    public ValueTask<TestEntity> SaveWithExtensionMetadataAsync(TestEntity detachedEntity, string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata, CancellationToken cancellationToken = default)
        => SaveWithExtensionMetadataAsync(detachedEntity, existingEnvelopeCacheKey, extensionMetadata,
            new EnvelopeWriteCondition(0), cancellationToken);

    public async ValueTask<TestEntity> SaveWithExtensionMetadataAsync(
        TestEntity detachedEntity,
        string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata,
        EnvelopeWriteCondition condition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detachedEntity);
        cancellationToken.ThrowIfCancellationRequested();
        MetadataSaveCallCount++;

        if (detachedEntity.Envelope is not null)
        {
            throw new ArgumentException("The fake metadata repository only accepts detached entities.", nameof(detachedEntity));
        }

        if (BeforeMetadataSaveAsync is not null)
        {
            await BeforeMetadataSaveAsync(
                detachedEntity,
                existingEnvelopeCacheKey,
                extensionMetadata,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (MetadataSaveException is not null)
        {
            throw MetadataSaveException;
        }

        TestEntity? existing;
        lock (_gate)
        {
            existing = _items.GetValueOrDefault(detachedEntity.Key);
        }

        if (existing is null && existingEnvelopeCacheKey is not null)
        {
            throw new InvalidOperationException("An update cache key was supplied for a missing fake record.");
        }

        if (existing is not null
            && !string.Equals(
                existing.Envelope?.EnvelopeCacheKey,
                existingEnvelopeCacheKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fake update cache key does not identify the current record.");
        }

        lock (_gate)
        {
            existing = _items.GetValueOrDefault(detachedEntity.Key);
            var candidate = new TestEnvelope { EnvelopeCacheKey = existingEnvelopeCacheKey ?? detachedEntity.Key, ObjectInstance = detachedEntity };
            var context = new EnvelopeWriteConflict<TestEntity>(candidate.EnvelopeCacheKey, condition.ExpectedRevision,
                existing?.Envelope?.RevisionTimestampUtcMicroseconds, existingEnvelopeCacheKey is null, candidate,
                _ => ValueTask.FromResult(existing is null ? null : CreateOwnedEntity(existing.Key, existing.Value, existing.Envelope!.ExtensionMetadata, existing.Envelope.EnvelopeCacheKey, existing.Tags)));
            if (!new Idb.Libraries.Implementation.Serialization.StrictTimestampMatchStrategy<TestEntity>().DecideAsync(context).Result.Accepted)
                throw new WriteConflictException<TestEntity>(context);
            if (condition.Action == EnvelopeRevisionAction.MetadataOnly
                && !(existing!.Envelope!.ExtensionMetadata ?? []).AsSpan().SequenceEqual(condition.ExpectedExtensionMetadata ?? []))
                throw new WriteConflictException<TestEntity>(context);
            var values = condition.Action == EnvelopeRevisionAction.MetadataOnly ? existing! : detachedEntity;
            var canonical = CreateOwnedEntity(values.Key, values.Value, extensionMetadata, existingEnvelopeCacheKey, values.Tags);
            canonical.Envelope!.RevisionTimestampUtcMicroseconds = condition.Action switch
            {
                EnvelopeRevisionAction.Import => condition.SourceRevision,
                EnvelopeRevisionAction.MetadataOnly => condition.ExpectedRevision,
                _ => _clock.Next(condition.ExpectedRevision)
            };
            _items[canonical.Key] = canonical;
            return canonical;
        }
    }

    public ValueTask<bool> DeleteAsync(
        TestEntity entityToDelete,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCallCount++;
        lock (_gate)
        {
            return ValueTask.FromResult(_items.Remove(entityToDelete.Key));
        }
    }

    public ValueTask<TestEntity?> QueryAsync(
        Func<TestEntity, bool> filterFunction,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Snapshot().FirstOrDefault(filterFunction));

    ValueTask<TImplementation?> IRepository<TestEntity>.QueryAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken)
        where TImplementation : class
        => ValueTask.FromResult(Snapshot().OfType<TImplementation>().FirstOrDefault(filterFunction));

    public IAsyncEnumerable<TestEntity> QueryManyAsync(
        Func<TestEntity, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        QueryManyCallCount++;
        return EnumerateAsync(Snapshot().Where(filterFunction), cancellationToken);
    }

    IAsyncEnumerable<TImplementation> IRepository<TestEntity>.QueryManyAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken)
        => EnumerateAsync(Snapshot().OfType<TImplementation>().Where(filterFunction), cancellationToken);

    public IAsyncEnumerable<TestEntity> SearchManyAsync(
        Expression<Func<TestEntity, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        SearchManyCallCount++;
        return EnumerateAsync(Snapshot().Where(filterExpression.Compile()), cancellationToken);
    }

    IAsyncEnumerable<TImplementation> IRepository<TestEntity>.SearchManyAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken)
        => EnumerateAsync(
            Snapshot().OfType<TImplementation>().Where(filterExpression.Compile()),
            cancellationToken);

    public IAsyncEnumerable<TestEntity> QueryManyByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
        => EnumerateAsync(
            Snapshot().Where(item => string.Equals(item.Key, storageKey, StringComparison.Ordinal)),
            cancellationToken);

    public ValueTask<TestEntity?> QueryFirstAsync(
        Func<TestEntity, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        QueryFirstCallCount++;
        return ValueTask.FromResult(Snapshot().FirstOrDefault(filterFunction));
    }

    ValueTask<TImplementation?> IRepository<TestEntity>.QueryFirstAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken)
        where TImplementation : class
    {
        QueryFirstCallCount++;
        return ValueTask.FromResult(Snapshot().OfType<TImplementation>().FirstOrDefault(filterFunction));
    }

    public ValueTask<TestEntity?> SearchFirstAsync(
        Expression<Func<TestEntity, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        SearchFirstCallCount++;
        return ValueTask.FromResult(Snapshot().FirstOrDefault(filterExpression.Compile()));
    }

    ValueTask<TImplementation?> IRepository<TestEntity>.SearchFirstAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken)
        where TImplementation : class
    {
        SearchFirstCallCount++;
        return ValueTask.FromResult(
            Snapshot().OfType<TImplementation>().FirstOrDefault(filterExpression.Compile()));
    }

    public ValueTask<TestEntity?> QueryFirstByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryFirstByStorageKeyCallCount++;
        if (QueryFirstByStorageKeyException is not null)
        {
            throw QueryFirstByStorageKeyException;
        }

        return ValueTask.FromResult(
            QueryFirstByStorageKeyOverride?.Invoke(storageKey) ?? Get(storageKey));
    }

    public ValueTask<TestEntity> QuerySingleAsync(
        Func<TestEntity, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null)
        => ValueTask.FromResult(Single(
            Snapshot().Where(filterFunction),
            noneExceptionFactory,
            multipleExceptionFactory));

    ValueTask<TImplementation> IRepository<TestEntity>.QuerySingleAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken,
        Func<Exception>? noneExceptionFactory,
        Func<Exception>? multipleExceptionFactory)
        => ValueTask.FromResult(Single(
            Snapshot().OfType<TImplementation>().Where(filterFunction),
            noneExceptionFactory,
            multipleExceptionFactory));

    public ValueTask<TestEntity?> QuerySingleOrDefaultAsync(
        Func<TestEntity, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
        => ValueTask.FromResult(SingleOrDefault(
            Snapshot().Where(filterFunction),
            multipleExceptionFactory));

    ValueTask<TImplementation?> IRepository<TestEntity>.QuerySingleOrDefaultAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken,
        Func<Exception>? multipleExceptionFactory)
        where TImplementation : class
        => ValueTask.FromResult(SingleOrDefault(
            Snapshot().OfType<TImplementation>().Where(filterFunction),
            multipleExceptionFactory));

    public ValueTask<bool> ExistsAsync(
        TestEntity entityToCheck,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Get(entityToCheck.Key) is not null);
    }

    private TestEntity CreateOwnedEntity(
        string key,
        string value,
        byte[]? extensionMetadata,
        string? envelopeCacheKey,
        IEnumerable<string>? tags = null)
    {
        var entity = new TestEntity
        {
            Key = key,
            Value = value,
            Tags = tags?.ToList() ?? []
        };
        var envelope = new TestEnvelope
        {
            EnvelopeCacheKey = envelopeCacheKey ?? _envelopeKeyFactory(key),
            Location = $"{_owner}://{key}",
            ExtensionMetadata = extensionMetadata?.ToArray(),
            RevisionTimestampUtcMicroseconds = extensionMetadata is { Length: > 4 } && extensionMetadata.AsSpan(0, 4).SequenceEqual("IDBR"u8)
                ? ResilientEnvelopeMetadataCodec.Decode(extensionMetadata).RevisionTimestampUtcMicroseconds : 0,
            ObjectInstance = entity
        };
        entity.Envelope = envelope;
        return entity;
    }

    private TestEntity[] Snapshot()
    {
        lock (_gate)
        {
            return _items.Values.ToArray();
        }
    }

    private async IAsyncEnumerable<T> EnumerateAsync<T>(
        IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnumerationException is not null)
            {
                throw EnumerationException;
            }

            yield return item;
        }

        if (EnumerationException is not null)
        {
            throw EnumerationException;
        }

        await Task.CompletedTask;
    }

    private static T Single<T>(
        IEnumerable<T> source,
        Func<Exception>? noneExceptionFactory,
        Func<Exception>? multipleExceptionFactory)
        where T : class
    {
        var values = source.Take(2).ToArray();
        return values.Length switch
        {
            0 => throw (noneExceptionFactory?.Invoke() ?? new InvalidOperationException("No match.")),
            > 1 => throw (multipleExceptionFactory?.Invoke() ?? new InvalidOperationException("Multiple matches.")),
            _ => values[0]
        };
    }

    private static T? SingleOrDefault<T>(
        IEnumerable<T> source,
        Func<Exception>? multipleExceptionFactory)
        where T : class
    {
        var values = source.Take(2).ToArray();
        return values.Length switch
        {
            > 1 => throw (multipleExceptionFactory?.Invoke() ?? new InvalidOperationException("Multiple matches.")),
            1 => values[0],
            _ => null
        };
    }
}

internal sealed class NonMetadataRepository : IRepository<TestEntity>
{
    public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<TestEntity> SaveAsync(TestEntity entityToSave, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<TestEntity>> SaveManyAsync(IEnumerable<TestEntity> entitiesToSave, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<bool> DeleteAsync(TestEntity entityToDelete, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<TestEntity?> QueryAsync(Func<TestEntity, bool> filterFunction, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    ValueTask<TImplementation?> IRepository<TestEntity>.QueryAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken)
        where TImplementation : class
        => throw new NotSupportedException();

    public IAsyncEnumerable<TestEntity> QueryManyAsync(Func<TestEntity, bool> filterFunction, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    IAsyncEnumerable<TImplementation> IRepository<TestEntity>.QueryManyAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public IAsyncEnumerable<TestEntity> SearchManyAsync(Expression<Func<TestEntity, bool>> filterExpression, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    IAsyncEnumerable<TImplementation> IRepository<TestEntity>.SearchManyAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public IAsyncEnumerable<TestEntity> QueryManyByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<TestEntity?> QueryFirstAsync(Func<TestEntity, bool> filterFunction, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    ValueTask<TImplementation?> IRepository<TestEntity>.QueryFirstAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken)
        where TImplementation : class
        => throw new NotSupportedException();

    public ValueTask<TestEntity?> SearchFirstAsync(Expression<Func<TestEntity, bool>> filterExpression, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    ValueTask<TImplementation?> IRepository<TestEntity>.SearchFirstAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken)
        where TImplementation : class
        => throw new NotSupportedException();

    public ValueTask<TestEntity?> QueryFirstByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask<TestEntity> QuerySingleAsync(Func<TestEntity, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null)
        => throw new NotSupportedException();

    ValueTask<TImplementation> IRepository<TestEntity>.QuerySingleAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken, Func<Exception>? noneExceptionFactory, Func<Exception>? multipleExceptionFactory)
        => throw new NotSupportedException();

    public ValueTask<TestEntity?> QuerySingleOrDefaultAsync(Func<TestEntity, bool> filterFunction, CancellationToken cancellationToken = default, Func<Exception>? multipleExceptionFactory = null)
        => throw new NotSupportedException();

    ValueTask<TImplementation?> IRepository<TestEntity>.QuerySingleOrDefaultAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken, Func<Exception>? multipleExceptionFactory)
        where TImplementation : class
        => throw new NotSupportedException();

    public ValueTask<bool> ExistsAsync(TestEntity entityToCheck, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
