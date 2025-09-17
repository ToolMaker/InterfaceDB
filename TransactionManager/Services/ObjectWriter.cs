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

            var directory = _scopeService.GetDirectory<T>()
                           ?? throw new InvalidOperationException($"No storage scope registered for type '{typeof(T).FullName}'.");

            Directory.CreateDirectory(directory.FullName);

            var normalizedId = NormalizeId(id);
            var extension = NormalizeExtension(_serializer.FileExtension);
            var targetPath = Path.Combine(directory.FullName, $"{normalizedId}.v{schemaVersion}{extension}");
            var tempPath = Path.Combine(directory.FullName, $".{normalizedId}.v{schemaVersion}.{Guid.NewGuid():N}.tmp");

            var runtimeType = obj.GetType();
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

            _logger.LogInformation("Wrote {Type} with id {Id} v{Version} to {Path}", typeof(T).Name, normalizedId, schemaVersion, targetPath);

            return new ObjectWriteResult(normalizedId,
                                         schemaVersion,
                                         typeof(T),
                                         new FileInfo(targetPath),
                                         envelope);
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
                : $".{extension}";
        }
    }
}
