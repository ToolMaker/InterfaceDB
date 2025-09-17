using System;
using System.IO;
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
        private const int DefaultBufferSize = 64 * 1024;

        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly ILogger<ObjectWriter> _logger;
        private readonly string _extension;

        public ObjectWriter(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            ILogger<ObjectWriter>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _logger = logger ?? NullLogger<ObjectWriter>.Instance;
            _extension = NormalizeExtension(serializer.FileExtension);
        }

        public async ValueTask<ObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
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
                    string.Join(", ", scope.Implementations));
            }

            var contractDirectory = scope.Directory;
            Directory.CreateDirectory(contractDirectory.FullName);

            var versionDirectory = GetOrCreateVersionDirectory(contractDirectory, scope.VersionTag);

            var normalizedId = NormalizeId(id);
            var targetPath = Path.Combine(versionDirectory.FullName, normalizedId + _extension);

            var payload = _serializer.Serialize(obj, runtimeType);
            var envelope = new ObjectEnvelope
            {
                VersionTag = scope.VersionTag,
                Payload = payload,
                TypeName = runtimeType.AssemblyQualifiedName ?? runtimeType.FullName
            };

            var bytes = _serializer.Serialize(envelope);

            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = DefaultBufferSize
            };

            await using (var stream = new FileStream(targetPath, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Wrote {Contract} implementation {Runtime} with id {Id} tag {Tag} to {Path}",
                contractType.Name,
                runtimeType.Name,
                normalizedId,
                scope.VersionTag,
                targetPath);

            return new ObjectWriteResult(normalizedId,
                                         scope.VersionTag,
                                         contractType,
                                         new FileInfo(targetPath),
                                         envelope);
        }

        private static DirectoryInfo GetOrCreateVersionDirectory(DirectoryInfo contractDirectory,
                                                                  string versionTag)
        {
            var directory = new DirectoryInfo(Path.Combine(contractDirectory.FullName, versionTag));
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
                directory.Refresh();
            }

            return directory;
        }

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
                : "." + extension;
        }
    }
}
