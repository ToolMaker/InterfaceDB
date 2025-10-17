using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository.FileRepository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Idb.Libraries.Implementation.Services;

public sealed class ObjectReader : IObjectReader
{
    private readonly ILocalScopeService _scopeService;
    private readonly IObjectSerializer _serializer;
    private readonly ILogger<ObjectReader> _logger;
    private readonly string _extension;

    public ObjectReader(
        ILocalScopeService scopeService,
        IObjectSerializer serializer,
        ILogger<ObjectReader>? logger = null)
    {
        _scopeService = scopeService ?? throw new ArgumentNullException(nameof(scopeService));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger ?? NullLogger<ObjectReader>.Instance;
        _extension = NormalizeExtension(serializer.FileExtension);
    }

    public async ValueTask<IObjectReadResult<T>> ReadAsync<T>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var scope = ResolveScope<T>();
        var normalizedId = NormalizeId(id);
        var resolvedVersionTag = ResolveVersionTag(scope, versionTag);

        var file = ResolveFile(scope.Directory, resolvedVersionTag, normalizedId);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                $"No persisted object found for contract '{scope.ContractType.FullName}' with id '{normalizedId}' and version '{resolvedVersionTag}'.",
                file.FullName);
        }

        byte[] envelopeBytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
        var envelope = _serializer.Deserialize<ObjectEnvelope>(envelopeBytes);

        var runtimeType = ResolveRuntimeType(envelope.TypeName) ?? typeof(T);
        var value = _serializer.Deserialize(envelope.Payload, runtimeType);

        if (value is not T typed)
        {
            throw new InvalidCastException(
                $"Stored payload '{file.FullName}' resolved to type '{runtimeType.FullName}', which cannot be cast to '{typeof(T).FullName}'.");
        }

        _logger.LogDebug(
            "Read contract {Contract} with id {Id} and version {Version} from {Path}",
            scope.ContractType.FullName,
            normalizedId,
            resolvedVersionTag,
            file.FullName);

        return new ObjectReadResult<T>(
            normalizedId,
            resolvedVersionTag,
            file,
            envelope,
            typed);
    }

    public ValueTask<bool> ExistsAsync<T>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var scope = ResolveScope<T>();
        var normalizedId = NormalizeId(id);
        var resolvedVersionTag = ResolveVersionTag(scope, versionTag);

        var file = ResolveFile(scope.Directory, resolvedVersionTag, normalizedId);
        var exists = file.Exists;

        _logger.LogDebug(
            "Exists check for contract {Contract} id {Id} version {Version}: {Exists}",
            scope.ContractType.FullName,
            normalizedId,
            resolvedVersionTag,
            exists);

        return ValueTask.FromResult(exists);
    }

    private IScopeDescriptor ResolveScope<T>()
        where T : class
    {
        var contractType = typeof(T);
        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException($"Storage contract '{contractType.FullName}' must be an interface marked with [StorageScope].");
        }

        return _scopeService.GetScope<T>()
               ?? throw new InvalidOperationException($"No storage scope registered for interface '{contractType.FullName}'.");
    }

    private DirectoryInfo ResolveVersionDirectory(DirectoryInfo contractDirectory, string versionTag)
    {
        var versionDirectory = new DirectoryInfo(Path.Combine(contractDirectory.FullName, versionTag));
        versionDirectory.Refresh();
        return versionDirectory;
    }

    private FileInfo ResolveFile(DirectoryInfo contractDirectory, string versionTag, string normalizedId)
    {
        var versionDirectory = ResolveVersionDirectory(contractDirectory, versionTag);
        var path = Path.Combine(versionDirectory.FullName, normalizedId + _extension);
        return new FileInfo(path);
    }

    private static string ResolveVersionTag(IScopeDescriptor scope, string? requestedVersion)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            return requestedVersion.Trim();
        }

        if (!string.IsNullOrWhiteSpace(scope.VersionTag))
        {
            return scope.VersionTag;
        }

        return "1.0";
    }

    private static string NormalizeId(string id)
    {
        var trimmed = id.Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Id '{trimmed}' contains invalid file-name characters.", nameof(id));
        }

        return trimmed;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : "." + extension;
    }

    private static Type? ResolveRuntimeType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type is not null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            }
            catch (ReflectionTypeLoadException ex)
            {
                type = ex.Types.FirstOrDefault(t => t?.FullName == typeName);
            }

            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
