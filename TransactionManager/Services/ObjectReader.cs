using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Dtos;

namespace TransactionManager.Services
{
    public sealed class ObjectReader : IObjectReader
    {
        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly ILogger<ObjectReader> _logger;

        public ObjectReader(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            ILogger<ObjectReader>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _logger = logger ?? NullLogger<ObjectReader>.Instance;
        }

        public ValueTask<bool> ExistsAsync<T>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default)
        {
            var contractType = typeof(T);
            if (!contractType.IsInterface)
            {
                return ValueTask.FromResult(false);
            }

            var scope = _scopeService.GetScope<T>();
            if (scope is null)
            {
                return ValueTask.FromResult(false);
            }

            var normalizedId = NormalizeId(id);
            var extension = NormalizeExtension(_serializer.FileExtension);

            FileInfo? file = null;

            if (schemaVersion.HasValue)
            {
                var directory = GetVersionDirectory(scope.Directory, schemaVersion!.Value, createIfMissing: false);
                if (directory is null)
                {
                    return ValueTask.FromResult(false);
                }

                file = LocateFile(directory, normalizedId, extension);
            }
            else
            {
                var latest = GetLatestVersionDirectory(scope.Directory);
                if (latest is not null)
                {
                    file = LocateFile(latest, normalizedId, extension);
                }
            }

            return ValueTask.FromResult(file?.Exists == true);
        }

        public async ValueTask<ObjectReadResult<T>> ReadAsync<T>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contractType = typeof(T);
            if (!contractType.IsInterface)
            {
                throw new InvalidOperationException($"Storage contract '{contractType.FullName}' must be an interface marked with [StorageScope].");
            }

            var scope = _scopeService.GetScope<T>()
                        ?? throw new InvalidOperationException($"No storage scope registered for interface '{contractType.FullName}'.");

            var normalizedId = NormalizeId(id);
            var extension = NormalizeExtension(_serializer.FileExtension);

            DirectoryInfo? versionDirectory;
            if (schemaVersion.HasValue)
            {
                versionDirectory = GetVersionDirectory(scope.Directory, schemaVersion!.Value, createIfMissing: false)
                                   ?? throw new FileNotFoundException($"Version folder '{ObjectWriter.GetVersionFolderName(schemaVersion!.Value)}' not found for contract '{contractType.Name}'.");
            }
            else
            {
                versionDirectory = GetLatestVersionDirectory(scope.Directory)
                                   ?? throw new FileNotFoundException($"No stored versions found for contract '{contractType.Name}'.");
            }

            var file = LocateFile(versionDirectory, normalizedId, extension)
                       ?? throw new FileNotFoundException($"No stored instance of contract '{contractType.Name}' with id '{normalizedId}' was found.");

            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);

            var envelope = _serializer.Deserialize<ObjectEnvelope>(bytes);
            var resolvedType = ResolveStoredType(envelope.TypeName, contractType);

            if (!contractType.IsAssignableFrom(resolvedType))
            {
                throw new InvalidCastException($"Stored implementation '{resolvedType.FullName}' is not assignable to requested contract '{contractType.FullName}'.");
            }

            var rawValue = _serializer.Deserialize(envelope.Payload, resolvedType)
                           ?? throw new InvalidOperationException($"Deserializer returned null for stored type '{resolvedType.FullName}'.");

            var typedValue = (T)rawValue;

            _logger.LogInformation(
                "Read {Contract} implementation {Runtime} with id {Id} v{Version} from {Path}",
                contractType.Name,
                resolvedType.Name,
                normalizedId,
                envelope.SchemaVersion,
                file.FullName);

            return new ObjectReadResult<T>(normalizedId,
                                            envelope.SchemaVersion,
                                            file,
                                            envelope,
                                            typedValue);
        }

        private static DirectoryInfo? GetVersionDirectory(DirectoryInfo contractDirectory, short schemaVersion, bool createIfMissing)
        {
            var versionFolderName = ObjectWriter.GetVersionFolderName(schemaVersion);
            var directory = new DirectoryInfo(Path.Combine(contractDirectory.FullName, versionFolderName));

            if (directory.Exists)
            {
                return directory;
            }

            if (!createIfMissing)
            {
                return null;
            }

            Directory.CreateDirectory(directory.FullName);
            directory.Refresh();
            return directory;
        }

        private static DirectoryInfo? GetLatestVersionDirectory(DirectoryInfo contractDirectory)
        {
            return contractDirectory.EnumerateDirectories()
                                     .Select(d => new { Directory = d, Version = ParseVersion(d.Name) })
                                     .Where(x => x.Version.HasValue)
                                     .OrderByDescending(x => x.Version!.Value)
                                     .Select(x => x.Directory)
                                     .FirstOrDefault();
        }

        private static decimal? ParseVersion(string name)
        {
            if (decimal.TryParse(name, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return null;
        }

        private static FileInfo? LocateFile(DirectoryInfo versionDirectory, string id, string extension)
        {
            if (!versionDirectory.Exists)
            {
                return null;
            }

            var candidate = new FileInfo(Path.Combine(versionDirectory.FullName, $"{id}{extension}"));
            return candidate.Exists ? candidate : null;
        }

        private static Type ResolveStoredType(string? typeName, Type fallback)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return fallback;
            }

            var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type != null)
            {
                return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type != null)
                {
                    return type;
                }
            }

            return fallback;
        }

        private static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
            }

            var trimmed = id.Trim();

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
                : $".{extension}";
        }
    }
}
