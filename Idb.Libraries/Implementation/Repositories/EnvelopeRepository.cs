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
internal sealed class EnvelopeRepository<TContract> : IEnvelopeRepository<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IFileNameFactory<TContract, FileInfo> _fileNameFactory;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IDbContractConfigDto<TContract, FileInfo> _contractConfig;
    private readonly string _dataRoot;

    private readonly ConcurrentDictionary<string, IObjectEnvelope<TContract, FileInfo>> _envelopes = new();
    private IEnumerator<FileInfo>? _fileEnumerator;

    public EnvelopeRepository(
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IObjectSerializer<TContract> serializer,
        IDbContractConfigDto<TContract, FileInfo> contractConfig)
    {
        _fileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));

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

    public async ValueTask<IObjectEnvelope<TContract, FileInfo>> WriteAsync(
        TContract obj,
        CancellationToken cancellationToken = default)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        var existingEnvelope = obj.Envelope as FileBackedObjectEnvelope<TContract>;
        var existingLocation = existingEnvelope?.Location;
        var existingKey = existingEnvelope?.CacheKey;

        var fileNameSegment = _fileNameFactory.Create(obj, existingLocation, existingKey);
        if (string.IsNullOrWhiteSpace(fileNameSegment))
        {
            throw new InvalidOperationException("The file-name factory returned an empty identifier.");
        }

        var extension = NormalizeExtension(_serializer.FileExtension);
        var targetPath = Path.Combine(_dataRoot, fileNameSegment + extension);
        var targetFile = new FileInfo(targetPath);

        var payload = _serializer.Serialize(obj);
        await File.WriteAllBytesAsync(targetFile.FullName, payload, cancellationToken).ConfigureAwait(false);

        var envelope = CreateEnvelope(targetFile, obj, payload);
        _envelopes[envelope.CacheKey] = envelope;
        obj.Envelope = envelope;

        return envelope;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> QueryAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var envelope in _envelopes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return envelope;

            // Ensure we remain asynchronously friendly even though the operation is CPU-only.
            await Task.Yield();
        }
    }

    private async ValueTask<IObjectEnvelope<TContract, FileInfo>> ReadNextEnvelopeAsync(
        CancellationToken cancellationToken)
    {
        EnsureFileEnumerator();

        if (_fileEnumerator is null || !_fileEnumerator.MoveNext())
        {
            return null!;
        }

        var file = _fileEnumerator.Current;
        var payload = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
        var contract = _serializer.Deserialize(payload);

        var envelope = CreateEnvelope(file, contract, payload);
        return envelope;
    }

    private void EnsureFileEnumerator()
    {
        if (_fileEnumerator is not null)
        {
            return;
        }

        var directory = new DirectoryInfo(_dataRoot);
        if (!directory.Exists)
        {
            directory.Create();
        }

        var extension = NormalizeExtension(_serializer.FileExtension);
        IEnumerable<FileInfo> files = directory.EnumerateFiles($"*{extension}", SearchOption.TopDirectoryOnly)
            .OrderBy(static file => file.Name);

        _fileEnumerator = files.GetEnumerator();
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
