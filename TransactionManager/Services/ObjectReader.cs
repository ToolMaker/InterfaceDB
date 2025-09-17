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
        private const int DefaultBufferSize = 64 * 1024;

        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly ILogger<ObjectReader> _logger;
        private readonly string _extension;

        public ObjectReader(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            ILogger<ObjectReader>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _logger = logger ?? NullLogger<ObjectReader>.Instance;
            _extension = NormalizeExtension(serializer.FileExtension);
        }

        public ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
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
            var tag = versionTag ?? scope.VersionTag;

            var versionDirectory = GetVersionDirectory(scope.Directory, tag, createIfMissing: false);
            if (versionDirectory is null)
            {
                return ValueTask.FromResult(false);
            }

            var file = LocateFile(versionDirectory, normalizedId);
            return ValueTask.FromResult(file?.Exists == true);
        }

        public async ValueTask<ObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
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

            FileInfo? file = null;
            string resolvedTag;

            if (!string.IsNullOrWhiteSpace(versionTag))
            {
                var directory = GetVersionDirectory(scope.Directory, versionTag, createIfMissing: false)
                               ?? throw new FileNotFoundException($"Version tag '{versionTag}' not found for contract '{contractType.Name}'.");

                file = LocateFile(directory, normalizedId)
                       ?? throw new FileNotFoundException($"No stored instance of contract '{contractType.Name}' with id '{normalizedId}' was found in version '{versionTag}'.");

                resolvedTag = versionTag;
            }
            else
            {
                var defaultDirectory = GetVersionDirectory(scope.Directory, scope.VersionTag, createIfMissing: false);
                if (defaultDirectory is not null)
                {
                    file = LocateFile(defaultDirectory, normalizedId);
                    if (file is not null)
                    {
                        resolvedTag = scope.VersionTag;
                        goto Deserialize;
                    }
                }

                var latestDirectory = GetLatestVersionDirectory(scope.Directory)
                                      ?? throw new FileNotFoundException($"No stored versions found for contract '{contractType.Name}'.");

                file = LocateFile(latestDirectory, normalizedId)
                       ?? throw new FileNotFoundException($"No stored instance of contract '{contractType.Name}' with id '{normalizedId}' was found.");

                resolvedTag = latestDirectory.Name;
            }

        Deserialize:
            var envelopeBytes = await ReadFileAsync(file!, cancellationToken).ConfigureAwait(false);

            var envelope = _serializer.Deserialize<ObjectEnvelope>(envelopeBytes);
            var resolvedType = ResolveStoredType(envelope.TypeName, contractType);

            if (!contractType.IsAssignableFrom(resolvedType))
            {
                throw new InvalidCastException($"Stored implementation '{resolvedType.FullName}' is not assignable to requested contract '{contractType.FullName}'.");
            }

            var rawValue = _serializer.Deserialize(envelope.Payload, resolvedType)
                           ?? throw new InvalidOperationException($"Deserializer returned null for stored type '{resolvedType.FullName}'.");

            var typedValue = (T)rawValue;

            _logger.LogInformation(
                "Read {Contract} implementation {Runtime} with id {Id} tag {Tag} from {Path}",
                contractType.Name,
                resolvedType.Name,
                normalizedId,
                envelope.VersionTag,
                file!.FullName);

            return new ObjectReadResult<T>(normalizedId,
                                            envelope.VersionTag,
                                            file!,
                                            envelope,
                                            typedValue);
        }

        private static DirectoryInfo? GetVersionDirectory(DirectoryInfo contractDirectory,
                                                           string versionTag,
                                                           bool createIfMissing)
        {
            var directory = new DirectoryInfo(Path.Combine(contractDirectory.FullName, versionTag));

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

        private DirectoryInfo? GetLatestVersionDirectory(DirectoryInfo contractDirectory)
        {
            return contractDirectory.EnumerateDirectories()
                                     .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                                     .FirstOrDefault();
        }

        private FileInfo? LocateFile(DirectoryInfo versionDirectory, string id)
        {
            if (!versionDirectory.Exists)
            {
                return null;
            }

            var candidate = new FileInfo(Path.Combine(versionDirectory.FullName, id + _extension));
            return candidate.Exists ? candidate : null;
        }

        private static async ValueTask<byte[]> ReadFileAsync(FileInfo file, CancellationToken cancellationToken)
        {
            var length = file.Length;
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            if (length > int.MaxValue)
            {
                throw new IOException($"File '{file.FullName}' is too large to be processed.");
            }

            var buffer = GC.AllocateUninitializedArray<byte>((int)length);

            var options = new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = DefaultBufferSize
            };

            await using var stream = new FileStream(file.FullName, options);

            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Unexpected end of file while reading '{file.FullName}'.");
                }

                offset += read;
            }

            return buffer;
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
                : "." + extension;
        }
    }
}
