using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Keeps the envelope cache aligned with committed file-system payload changes.
/// </summary>
public sealed class FileEnvelopeStorageChangeHandler<TContract> : IEnvelopeStorageChangeHandler<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly StorageObjectCardinality _objectCardinality;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
    private readonly IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> _envelopes;
    private readonly StoragePolicy _storagePolicy;
    private readonly string _dataRoot;
    private readonly string _fileExtension;
    private readonly StringComparer _pathComparer;

    public FileEnvelopeStorageChangeHandler(
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy,
        IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> envelopeLifecycle,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes)
        : this(contractConfig, storagePolicy, serializer, envelopeLifecycle, envelopes, null)
    {
    }

    public FileEnvelopeStorageChangeHandler(
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy,
        IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> envelopeLifecycle,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
        IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _objectCardinality = storageIdentityFactory?.ObjectCardinality ?? contractConfig.ObjectCardinality;
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));

        if (string.IsNullOrWhiteSpace(contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(contractConfig.DataRoot!);
        _fileExtension = contractConfig.FileExtension.NormalizeExtension();
        _pathComparer = FileSystemPathIdentity.ResolveComparer(_dataRoot);
    }

    public async ValueTask<EnvelopeStorageChangeResult<TContract>> HandleAsync(
        EnvelopeStorageChange change,
        CancellationToken cancellationToken = default)
    {
        if (change is null)
        {
            throw new ArgumentNullException(nameof(change));
        }

        return change.ChangeType switch
        {
            EnvelopeStorageChangeType.Created => await HandleCurrentPayloadAsync(change.ChangeType, change.NewPath, cancellationToken).ConfigureAwait(false),
            EnvelopeStorageChangeType.Changed => await HandleCurrentPayloadAsync(change.ChangeType, change.NewPath, cancellationToken).ConfigureAwait(false),
            EnvelopeStorageChangeType.Deleted => HandleDeleted(change.OldPath ?? change.NewPath, EnvelopeStorageChangeType.Deleted),
            EnvelopeStorageChangeType.Renamed => await HandleRenamedAsync(change, cancellationToken).ConfigureAwait(false),
            _ => new EnvelopeStorageChangeResult<TContract>(change.ChangeType, null, null)
        };
    }

    private async ValueTask<EnvelopeStorageChangeResult<TContract>> HandleRenamedAsync(
        EnvelopeStorageChange change,
        CancellationToken cancellationToken)
    {
        var oldResult = HandleDeleted(change.OldPath, EnvelopeStorageChangeType.Renamed);
        var newResult = await HandleCurrentPayloadAsync(EnvelopeStorageChangeType.Renamed, change.NewPath, cancellationToken).ConfigureAwait(false);
        return new EnvelopeStorageChangeResult<TContract>(
            EnvelopeStorageChangeType.Renamed,
            oldResult.OldEnvelope,
            newResult.NewEnvelope);
    }

    private EnvelopeStorageChangeResult<TContract> HandleDeleted(
        string? path,
        EnvelopeStorageChangeType changeType)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsPayloadPath(path))
        {
            return new EnvelopeStorageChangeResult<TContract>(changeType, null, null);
        }

        var cacheKey = Path.GetFileNameWithoutExtension(path);
        _envelopes.TryGetValue(cacheKey, out var oldEnvelope);
        if (oldEnvelope is not null)
        {
            oldEnvelope.State = StorageObjectState.Deleted;
        }
        _envelopes.Remove(cacheKey);

        return new EnvelopeStorageChangeResult<TContract>(changeType, oldEnvelope, null);
    }

    private async ValueTask<EnvelopeStorageChangeResult<TContract>> HandleCurrentPayloadAsync(
        EnvelopeStorageChangeType changeType,
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsPayloadPath(path))
        {
            return new EnvelopeStorageChangeResult<TContract>(changeType, null, null);
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return HandleDeleted(path, EnvelopeStorageChangeType.Deleted);
        }

        var envelope = await ReadCommittedEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);
        envelope.Location = file.FullName;
        // The file name is the provider's physical identity. Serialized metadata can be stale
        // after an external rename/copy and must not redirect cache membership to another path.
        envelope.EnvelopeCacheKey = Path.GetFileNameWithoutExtension(file.Name);
        envelope.VersionTag = string.IsNullOrWhiteSpace(envelope.VersionTag)
            ? ResolveVersion(file)
            : envelope.VersionTag;

        var cachedEnvelope = envelope;
        var unwrapped = envelope.ObjectInstance is not null
            ? _envelopeLifecycle.Unwrap(envelope)
            : null;
        var cacheMode = StorageObjectCachePolicy.ResolveObjectCacheMode(envelope, _storagePolicy);
        if (cacheMode is StorageObjectCacheMode.MetadataOnly)
        {
            cachedEnvelope = new FileObjectEnvelope<TContract>(_contractConfig)
            {
                EnvelopeCacheKey = envelope.EnvelopeCacheKey,
                VersionTag = envelope.VersionTag,
                Location = envelope.Location,
                ImplementationTypeName = envelope.ImplementationTypeName,
                CacheObjectInstance = envelope.CacheObjectInstance,
                EncryptedFields = envelope.EncryptedFields,
                ExtensionMetadata = envelope.ExtensionMetadata?.ToArray(),
                ObjectInstance = null,
                ObjectInstanceWeakReference = null,
                State = envelope.State
            };
        }
        else
        {
            cachedEnvelope = CommittedCacheCopies<TContract>.Copy(envelope, retainCache: false);
            var privateInstance = cachedEnvelope.ObjectInstance;
            if (cacheMode == StorageObjectCacheMode.WeakObject)
                cachedEnvelope = EnvelopeWriteCoordinator<TContract>.CopyEnvelope(cachedEnvelope);
            StorageObjectCachePolicy.StoreObjectReference(cachedEnvelope, privateInstance, _storagePolicy);
            if (unwrapped is not null && privateInstance is not null)
                CommittedCacheCopies<TContract>.KeepAlive(unwrapped, privateInstance);
        }

        _envelopes.AddOrUpdate(
            cachedEnvelope.EnvelopeCacheKey,
            (_objectCardinality == StorageObjectCardinality.Single ? cachedEnvelope.EnvelopeCacheKey : EnvelopeCacheKey.ToLogicalKey(cachedEnvelope.EnvelopeCacheKey)),
            cachedEnvelope);
        return new EnvelopeStorageChangeResult<TContract>(changeType, null, envelope);
    }

    private async ValueTask<IObjectEnvelope<TContract>> ReadCommittedEnvelopeAsync(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fso = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = _storagePolicy.NormalizedBufferSize
                };

                await using var stream = new FileStream(file.FullName, fso);
                if (stream.Length > _storagePolicy.NormalizedMaximumStoredObjectBytes)
                {
                    throw new InvalidDataException(
                        $"Stored envelope exceeds the configured {_storagePolicy.NormalizedMaximumStoredObjectBytes}-byte limit.");
                }

                var payload = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                var physicalKey = Path.GetFileNameWithoutExtension(file.Name);
                var envelope = ContextualObjectSerialization.Deserialize(
                    _serializer,
                    payload,
                    StorageEncryptionContext.Create(_contractConfig, "file", physicalKey));
                envelope.EnvelopeCacheKey = physicalKey;
                envelope.Location = file.FullName;
                CommittedCacheCopies<TContract>.Remember(envelope, payload, _serializer,
                    _envelopeLifecycle, _storagePolicy, StorageEncryptionContext.Create(_contractConfig, "file", physicalKey));
                return envelope;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"Unable to read changed payload '{file.FullName}'.");
    }

    private bool IsPayloadPath(string path)
    {
        if (RetainedHistoryNamespace.ContainsRelative(Path.GetRelativePath(_dataRoot, path))) return false;
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".pending.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(Path.GetExtension(path), _fileExtension, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveVersion(FileInfo file)
    {
        var dataRoot = new DirectoryInfo(_dataRoot);
        var directory = file.Directory;
        while (directory is not null && !_pathComparer.Equals(directory.Parent?.FullName, dataRoot.FullName))
        {
            directory = directory.Parent;
        }

        return directory?.Name ?? _contractConfig.ActiveVersion ?? "1.0.0";
    }
}
