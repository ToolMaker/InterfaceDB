using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// File-system backed envelope repository that manages serialization, persistence, and caching for contract instances.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class EnvelopeRepository<TContract> : IEnvelopeRepository<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IFileNameFactory<TContract, FileInfo> _fileNameFactory;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IDbContractConfigDto<TContract, FileInfo> _contractConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _dataRoot;

    private readonly ConcurrentDictionary<string, IObjectEnvelope<TContract, FileInfo>> _envelopes = new();

    public EnvelopeRepository(
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IObjectSerializer<TContract> serializer,
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        IServiceProvider serviceProvider)
    {
        _fileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(_dataRoot);
    }

    public IObjectSerializer<TContract> Serializer => _serializer;

    public bool ContainsEnvelope(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        return _envelopes.ContainsKey(cacheKey);
    }

    public bool TryGetEnvelope(string cacheKey, out IObjectEnvelope<TContract, FileInfo>? envelope)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(cacheKey));
        }

        var found = _envelopes.TryGetValue(cacheKey, out var existing);
        envelope = existing;
        return found;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Directory creation already happens in the constructor, so nothing further is required at present.
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IObjectEnvelope<TContract, FileInfo>> WriteAsync(
        TContract obj,
        CancellationToken cancellationToken = default)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        var placeholder = obj.Envelope;
        if (placeholder is null)
        {
            placeholder = (IObjectEnvelope<TContract, FileInfo>?)_serviceProvider.GetService(typeof(IObjectEnvelope<TContract, FileInfo>));
            if (placeholder is not null)
            {
                obj.Envelope = placeholder;
            }
        }

        var existingEnvelope = obj.Envelope as FileBackedObjectEnvelope<TContract>;
        var existingLocation = existingEnvelope?.Location;
        var normalizedId = existingEnvelope?.CacheKey
            ?? placeholder?.CacheKey
            ?? Guid.NewGuid().ToString("N");

        var fileNameSegment = _fileNameFactory.Create(obj, existingLocation, normalizedId);
        if (string.IsNullOrWhiteSpace(fileNameSegment))
        {
            throw new InvalidOperationException("The file-name factory returned an empty identifier.");
        }

        var extension = NormalizeExtension(_serializer.FileExtension);
        var targetPath = Path.Combine(_dataRoot, fileNameSegment + extension);
        var targetFile = new FileInfo(targetPath);

        obj.Envelope = null;
        var payload = _serializer.Serialize(obj);
        await File.WriteAllBytesAsync(targetFile.FullName, payload, cancellationToken).ConfigureAwait(false);

        var envelope = CreateEnvelope(targetFile, obj, payload);
        _envelopes[envelope.CacheKey] = envelope;
        obj.Envelope = envelope;

        return envelope;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> QueryAsync(
        Func<TContract, bool> filterFunction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (filterFunction is null)
        {
            throw new ArgumentNullException(nameof(filterFunction));
        }

        // Snapshot the current cache to avoid issues with concurrent mutations while streaming.
        var snapshot = _envelopes.Values.ToArray();

        foreach (var envelope in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instance = envelope.ObjectInstance;
            if (instance is null)
            {
                continue;
            }

            if (!filterFunction(instance))
            {
                continue;
            }

            yield return envelope;

            // Ensure we remain asynchronously friendly even though the operation is CPU-only.
            await Task.Yield();
        }
    }

    private FileBackedObjectEnvelope<TContract> CreateEnvelope(
        FileInfo file,
        TContract contract,
        byte[] payload)
    {
        var version = _contractConfig.ActiveVersion ?? "1.0.0";
        return new FileBackedObjectEnvelope<TContract>(
            file,
            version,
            contract,
            payload,
            static (_, info) => info.FullName);
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }
}



