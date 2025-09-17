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
    public sealed class ObjectWriter : IObjectWriter
    {
        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly ILogger<ObjectWriter> _logger;

        public ObjectWriter(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            ILogger<ObjectWriter>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _logger = logger ?? NullLogger<ObjectWriter>.Instance;
        }

        public async ValueTask<ObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            short schemaVersion = 1,
            CancellationToken cancellationToken = default)
        {
            if (obj is null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var contractType = typeof(T);
            if (!contractType.IsInterface)
            {
                throw new InvalidOperationException($"Storage contract '{contractType.FullName}' must be an interface marked with [StorageScope].");
            }

            var scope = _scopeService.GetScope<T>()
                        ?? throw new InvalidOperationException($"No storage scope registered for interface '{contractType.FullName}'.");

            var runtimeType = obj.GetType();
            if (!contractType.IsAssignableFrom(runtimeType))
            {
                throw new InvalidOperationException($"Instance of type '{runtimeType.FullName}' does not implement storage contract '{contractType.FullName}'.");
            }

            if (scope.Implementations.Count > 0 && !scope.Implementations.Contains(runtimeType))
            {
                _logger.LogWarning(
                    "Type {RuntimeType} was not discovered for contract {Contract}. Known implementations: {Known}",
                    runtimeType.FullName,
                    contractType.FullName,
                    string.Join(", ", scope.Implementations.Select(t => t.FullName)));
            }

            var contractDirectory = scope.Directory;
            Directory.CreateDirectory(contractDirectory.FullName);

            var versionDirectory = GetOrCreateVersionDirectory(contractDirectory, schemaVersion);

            var normalizedId = NormalizeId(id);
            var extension = NormalizeExtension(_serializer.FileExtension);
            var targetPath = Path.Combine(versionDirectory.FullName, $"{normalizedId}{extension}");
            var tempPath = Path.Combine(versionDirectory.FullName, $".{normalizedId}.{Guid.NewGuid():N}.tmp");

            var payload = _serializer.Serialize(obj, runtimeType);
            var envelope = new ObjectEnvelope
            {
                SchemaVersion = schemaVersion,
                Payload = payload,
                TypeName = runtimeType.AssemblyQualifiedName ?? runtimeType.FullName
            };

            var bytes = _serializer.Serialize(envelope);

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 32_768, useAsync: true))
            {
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, targetPath, true);

            _logger.LogInformation(
                "Wrote {Contract} implementation {Runtime} with id {Id} v{Version} to {Path}",
                contractType.Name,
                runtimeType.Name,
                normalizedId,
                schemaVersion,
                targetPath);

            return new ObjectWriteResult(normalizedId,
                                         schemaVersion,
                                         contractType,
                                         new FileInfo(targetPath),
                                         envelope);
        }

        private static DirectoryInfo GetOrCreateVersionDirectory(DirectoryInfo contractDirectory, short schemaVersion)
        {
            var versionFolderName = GetVersionFolderName(schemaVersion);
            var directory = new DirectoryInfo(Path.Combine(contractDirectory.FullName, versionFolderName));
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
                directory.Refresh();
            }
            return directory;
        }

        internal static string GetVersionFolderName(short schemaVersion)
            => schemaVersion.ToString("0.0", CultureInfo.InvariantCulture);

        private static string NormalizeId(string? id)
        {
            var value = string.IsNullOrWhiteSpace(id)
                ? Guid.NewGuid().ToString("N")
                : id.Trim();

            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"Id '{value}' contains invalid file-name characters.", nameof(id));
            }

            return value;
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
