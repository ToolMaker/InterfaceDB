using System;
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
            var normalizedId = NormalizeId(id);
            var directory = _scopeService.GetDirectory<T>();

            if (directory is null)
            {
                return ValueTask.FromResult(false);
            }

            var file = FindTargetFile(directory, normalizedId, schemaVersion);
            return ValueTask.FromResult(file?.Exists == true);
        }

        public async ValueTask<ObjectReadResult<T>> ReadAsync<T>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedId = NormalizeId(id);

            var directory = _scopeService.GetDirectory<T>();
            FileInfo? file = null;
            Type? scopedType = directory is null ? null : _scopeService.GetTypeByDirectory(directory);

            if (directory is not null)
            {
                file = FindTargetFile(directory, normalizedId, schemaVersion);
            }
            else
            {
                foreach (var scope in _scopeService.EnumerateScopes())
                {
                    if (!typeof(T).IsAssignableFrom(scope.Key))
                    {
                        continue;
                    }

                    var candidate = FindTargetFile(scope.Value, normalizedId, schemaVersion);
                    if (candidate is not null)
                    {
                        directory = scope.Value;
                        file = candidate;
                        scopedType = scope.Key;
                        break;
                    }
                }
            }

            if (directory is null || file is null)
            {
                throw new FileNotFoundException($"No stored instance assignable to '{typeof(T).FullName}' with id '{normalizedId}' was found.");
            }

            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);

            var envelope = _serializer.Deserialize<ObjectEnvelope>(bytes);
            var resolvedType = ResolveStoredType(envelope.TypeName, scopedType ?? typeof(T));
            var rawValue = _serializer.Deserialize(envelope.Payload, resolvedType);

            if (rawValue is null)
            {
                throw new InvalidOperationException($"Deserializer returned null for stored type '{resolvedType.FullName}'.");
            }

            if (rawValue is not T typedValue)
            {
                if (!typeof(T).IsAssignableFrom(rawValue.GetType()))
                {
                    throw new InvalidCastException($"Stored instance type '{rawValue.GetType().FullName}' cannot be cast to '{typeof(T).FullName}'.");
                }

                typedValue = (T)rawValue;
            }

            _logger.LogInformation("Read {Type} with id {Id} v{Version} from {Path}", typeof(T).Name, normalizedId, envelope.SchemaVersion, file.FullName);

            return new ObjectReadResult<T>(normalizedId,
                                            envelope.SchemaVersion,
                                            file,
                                            envelope,
                                            typedValue);
        }

        private FileInfo? FindTargetFile(DirectoryInfo directory, string id, short? schemaVersion)
        {
            if (!directory.Exists)
            {
                return null;
            }

            var extension = NormalizeExtension(_serializer.FileExtension);

            if (schemaVersion.HasValue)
            {
                var candidate = new FileInfo(Path.Combine(directory.FullName, $"{id}.v{schemaVersion.Value}{extension}"));
                return candidate.Exists ? candidate : null;
            }

            var pattern = $"{id}.v*{extension}";
            return directory.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                             .OrderByDescending(f => ParseSchemaVersion(f.Name))
                             .FirstOrDefault();
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

        private static short ParseSchemaVersion(string fileName)
        {
            var start = fileName.IndexOf(".v", StringComparison.Ordinal);
            if (start < 0)
            {
                return short.MinValue;
            }

            start += 2;
            var end = fileName.LastIndexOf('.');
            if (end <= start)
            {
                return short.MinValue;
            }

            var span = fileName.AsSpan(start, end - start);
            return short.TryParse(span, out var version) ? version : short.MinValue;
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
