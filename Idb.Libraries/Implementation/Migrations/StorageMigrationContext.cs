using Idb.Libraries.Abstractions.Dtos;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Migrations;
using Idb.Libraries.Abstractions.Repository;
using Idb.Libraries.Abstractions.Serialization;
using Idb.Libraries.Implementation.Dtos;

namespace Idb.Libraries.Implementation.Migrations
{
    internal sealed class StorageMigrationContext : IStorageMigrationContext
    {
        private readonly IScopeDescriptor _scope;
        private readonly IRepository _repository;
        private readonly DirectoryInfo _sourceDirectory;
        private readonly string _extension;

        public StorageMigrationContext(IScopeDescriptor scope,
                                       string sourceVersionTag,
                                       string targetVersionTag,
                                       IObjectSerializer serializer,
                                       IRepository repository)
        {
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            if (string.IsNullOrWhiteSpace(sourceVersionTag))
            {
                throw new ArgumentException("Source version tag cannot be null or whitespace.", nameof(sourceVersionTag));
            }

            if (string.IsNullOrWhiteSpace(targetVersionTag))
            {
                throw new ArgumentException("Target version tag cannot be null or whitespace.", nameof(targetVersionTag));
            }

            ContractType = scope.ContractType;
            SourceVersionTag = sourceVersionTag;
            TargetVersionTag = targetVersionTag;

            _sourceDirectory = new DirectoryInfo(Path.Combine(scope.Directory.FullName, SourceVersionTag));
            _extension = NormalizeExtension(serializer.FileExtension);
        }

        public Type ContractType { get; }
        public string SourceVersionTag { get; }
        public string TargetVersionTag { get; }
        public string FileExtension => _extension;
        public IObjectSerializer Serializer { get; }

        public async IAsyncEnumerable<IStorageMigrationRecord<T>> ReadSourceAsync<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_sourceDirectory.Exists)
            {
                yield break;
            }

            foreach (var file in _sourceDirectory.EnumerateFiles($"*{_extension}", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                var envelope = Serializer.Deserialize<ObjectEnvelope>(bytes);

                var runtimeType = ResolveStoredType(envelope.TypeName) ?? typeof(T);

                var value = Serializer.Deserialize(envelope.Payload, runtimeType)
                            ?? throw new InvalidOperationException($"Deserializer returned null for stored implementation '{envelope.TypeName}'.");

                if (value is not T typed)
                {
                    throw new InvalidCastException($"Stored payload '{file.FullName}' resolved to type '{runtimeType.FullName}', which cannot be cast to '{typeof(T).FullName}'.");
                }

                var id = Path.GetFileNameWithoutExtension(file.Name);

                yield return new StorageMigrationRecord<T>(id,
                                                            envelope,
                                                            runtimeType,
                                                            file,
                                                            typed);
            }
        }

        public async ValueTask WriteAsync<TContract>(string id, TContract entity, CancellationToken cancellationToken = default)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
            }

            var entityType = typeof(TContract);
            if (!_scope.ContractType.IsAssignableFrom(entityType))
            {
                throw new InvalidOperationException($"Entity type '{entityType.FullName}' is not assignable to contract '{_scope.ContractType.FullName}'.");
            }

            await _repository.SaveAsync(entity, id, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DeleteSourceAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id cannot be null or whitespace.", nameof(id));
            }

            if (!_sourceDirectory.Exists)
            {
                return ValueTask.CompletedTask;
            }

            var path = Path.Combine(_sourceDirectory.FullName, id + _extension);
            if (!File.Exists(path))
            {
                return ValueTask.CompletedTask;
            }

            File.Delete(path);

            return ValueTask.CompletedTask;
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".bin";
            }

            return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        }

        private static Type ResolveStoredType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return typeof(object);
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

            return typeof(object);
        }
    }
}

