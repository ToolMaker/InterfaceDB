using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Implementation.Services
{
    public sealed class ObjectWriter : IObjectWriter
    {
        private const int DefaultBufferSize = 64 * 1024;

        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ObjectWriter> _logger;
        private readonly string _extension;
        private readonly ConcurrentDictionary<Type, IFileNameFactory> _fileNameFactories = new();

        public ObjectWriter(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            IServiceProvider serviceProvider,
            ILogger<ObjectWriter>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _serviceProvider = serviceProvider;
            _logger = logger ?? NullLogger<ObjectWriter>.Instance;
            _extension = NormalizeExtension(serializer.FileExtension);
        }

        public async ValueTask<IObjectWriteResult> WriteAsync<T>(
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
            var fileName = ResolveFileName(scope, obj!, normalizedId);
            var targetPath = Path.Combine(versionDirectory.FullName, fileName + _extension);

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
                fileName,
                scope.VersionTag,
                targetPath);

            return new ObjectWriteResult(fileName,
                                         scope.VersionTag,
                                         contractType,
                                         new FileInfo(targetPath),
                                         envelope);
        }

        private string ResolveFileName<T>(IScopeDescriptor scope, T value, string normalizedId)
        {
            var factory = _fileNameFactories.GetOrAdd(scope.FileNameFactoryType, CreateFactoryInstance);
            var candidate = factory.Create(value!, normalizedId, scope.ContractType);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return normalizedId;
            }

            var trimmed = candidate.Trim();

            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException($"File-name '{trimmed}' produced for '{scope.ContractType.FullName}' contains invalid file-name characters.");
            }

            if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new InvalidOperationException($"File-name '{trimmed}' produced for '{scope.ContractType.FullName}' cannot contain directory separators.");
            }

            return trimmed;
        }

        private IFileNameFactory CreateFactoryInstance(Type factoryType)
        {
            var instance = ActivatorUtilities.CreateInstance(_serviceProvider, factoryType) as IFileNameFactory;
            if (instance is null)
            {
                throw new InvalidOperationException($"Resolved type '{factoryType.FullName}' does not implement {nameof(IFileNameFactory)}.");
            }

            return instance;
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
