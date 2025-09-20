using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TransactionManager.Abstraction.Dtos;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Abstraction.Services;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Implementation.Services
{
    public sealed class ObjectReader : IObjectReader
    {
        private readonly ILocalScopeService _scopeService;
        private readonly IObjectSerializer _serializer;
        private readonly IStoragePolicyProvider _policyProvider;
        private readonly ILogger<ObjectReader> _logger;
        private readonly string _extension;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _throttles = new(StringComparer.OrdinalIgnoreCase);

        public ObjectReader(
            ILocalScopeService scopeService,
            IObjectSerializer serializer,
            IStoragePolicyProvider policyProvider,
            ILogger<ObjectReader>? logger = null)
        {
            _scopeService = scopeService;
            _serializer = serializer;
            _policyProvider = policyProvider;
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

        public async ValueTask<IObjectReadResult<T>> ReadAsync<T>(
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

            var policy = _policyProvider.GetPolicy(scope.Directory.Root);
            var throttle = GetThrottle(scope.Directory, policy);
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
                var envelopeBytes = await ReadFileAsync(file!, policy, cancellationToken).ConfigureAwait(false);

                var envelope = _serializer.Deserialize<ObjectEnvelope>(envelopeBytes);
                var resolvedType = ResolveStoredType(envelope.TypeName, contractType);

                if (!contractType.IsAssignableFrom(resolvedType))
                {
                    throw new InvalidCastException($"Stored implementation '{resolvedType.FullName}' is not assignable to requested contract '{contractType.FullName}'.");
                }

                var rawValue = _serializer.Deserialize(envelope.Payload, resolvedType)
                               ?? throw new InvalidOperationException($"Deserializer returned null for stored implementation '{resolvedType.FullName}'.");

                var typedValue = (T)rawValue;

                return new ObjectReadResult<T>(normalizedId,
                                                resolvedTag,
                                                file!,
                                                envelope,
                                                typedValue);
            }
            finally
            {
                throttle.Release();
            }
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

        private SemaphoreSlim GetThrottle(DirectoryInfo directory, IStoragePolicy policy)
        {
            var key = GetPolicyKey(directory);
            return _throttles.GetOrAdd(key, _ => new SemaphoreSlim(policy.MaxConcurrentOperations));
        }

        private static string GetPolicyKey(DirectoryInfo directory)
        {
            var root = Path.GetPathRoot(directory.FullName);
            return string.IsNullOrEmpty(root) ? directory.FullName : root;
        }

        private static async ValueTask<byte[]> ReadFileAsync(FileInfo file, IStoragePolicy policy, CancellationToken cancellationToken)
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

            var options = CreateReadStreamOptions(policy);

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

        private static FileStreamOptions CreateReadStreamOptions(IStoragePolicy policy)
        {
            var options = policy.FileOptions;
            if (policy.UseAsync)
            {
                options |= FileOptions.Asynchronous;
            }
            else
            {
                options &= ~FileOptions.Asynchronous;
            }

            return new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = options,
                BufferSize = policy.PreferredBufferSize
            };
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
