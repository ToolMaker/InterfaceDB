using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using MessagePack;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;
using MessagePack.Resolvers;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Shared migration helpers for file-system-backed strategies.
/// </summary>
public abstract class FileSystemMigrationStrategyBase<TContract> : IMigrationStrategy<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly IReadOnlyList<MigrationDescriptor> _migrationDescriptors;
    private readonly IObjectReader<TContract>? _reader;
    private readonly IObjectWriter<TContract>? _writer;
    private readonly IFileWriteCoordinator _fileWriteCoordinator;
    private readonly IEncryptor _encryptor;

    protected FileSystemMigrationStrategyBase(
        IDbContractConfigDto<TContract> contractConfig,
        IObjectSerializer<TContract> serializer,
        IEnvelopeKeyAndLocationFactory<TContract> fileNameFactory,
        IEnumerable<IMigration> migrations,
        MigrationStrategyType strategyType,
        IEncryptor encryptor,
        IFileWriteCoordinator fileWriteCoordinator,
        IObjectReader<TContract>? reader = null,
        IObjectWriter<TContract>? writer = null)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        FileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        _reader = reader;
        _writer = writer;
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
        StrategyType = strategyType;

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        DataRootDirectory = new DirectoryInfo(Path.GetFullPath(_contractConfig.DataRoot!));
        ActiveVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        FileExtension = NormalizeExtension(_contractConfig.FileExtension.NormalizeExtension());
        SerializerOptions = ResolveSerializerOptions(Serializer);
        ContractSerializerOptions = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        IsTolerant = StrategyType == MigrationStrategyType.EagerTolerant;

        _migrationDescriptors = (migrations ?? Enumerable.Empty<IMigration>())
            .Where(m => m is not null)
            .Select(m => new MigrationDescriptor(m.SourceVersion, m.TargetVersion, m.GetType()))
            .OrderBy(m => m.SourceVersionParsed)
            .ThenBy(m => m.TargetVersionParsed)
            .ToList();
    }

    public MigrationStrategyType StrategyType { get; }

    protected IObjectSerializer<TContract> Serializer { get; }

    protected IEnvelopeKeyAndLocationFactory<TContract> FileNameFactory { get; }

    protected DirectoryInfo DataRootDirectory { get; }

    protected string ActiveVersion { get; }

    protected string FileExtension { get; }

    protected bool IsTolerant { get; }

    protected MessagePackSerializerOptions SerializerOptions { get; }

    protected MessagePackSerializerOptions ContractSerializerOptions { get; }

    protected IReadOnlyList<MigrationDescriptor> MigrationDescriptors => _migrationDescriptors;

    protected StoragePolicy EffectivePolicy => _contractConfig.StoragePolicy ?? StoragePolicy.Default;

    public virtual async IAsyncEnumerable<TContract> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_reader is null || _contractConfig.DataRoot is null)
        {
            yield break;
        }

        var root = new DirectoryInfo(Path.GetFullPath(_contractConfig.DataRoot));
        if (!root.Exists)
        {
            yield break;
        }

        foreach (var file in root.EnumerateFiles($"*{FileExtension}", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _reader.ReadAsync(file, cancellationToken).ConfigureAwait(false);
            var env = current.Envelope;
            if (env is null)
            {
                continue;
            }
            if (string.Equals(env.VersionTag, ActiveVersion, StringComparison.OrdinalIgnoreCase))
            {
                yield return current;
                continue;
            }

            var migrated = await MigrateFileAsync(file, deleteLegacyOnSuccess: true, cancellationToken).ConfigureAwait(false);
            if (migrated is not null)
            {
                yield return migrated;
            }
        }
    }

    public virtual TContract Migrate(TContract objectInstance, CancellationToken cancellationToken = default)
    {
        if (objectInstance is null)
        {
            throw new ArgumentNullException(nameof(objectInstance));
        }

        var env = objectInstance.Envelope;
        if (env is null || string.Equals(env.VersionTag, ActiveVersion, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(env.Location))
        {
            return objectInstance;
        }

        var file = new FileInfo(env.Location);
        var migrated = MigrateFileAsync(file, deleteLegacyOnSuccess: true, cancellationToken).GetAwaiter().GetResult();
        return migrated ?? objectInstance;
    }

    protected async ValueTask<TContract?> MigrateFileAsync(
        FileInfo legacyFile,
        bool deleteLegacyOnSuccess,
        CancellationToken cancellationToken)
    {
        await _fileWriteCoordinator
            .DrainPendingWritesAsync(legacyFile.FullName, cancellationToken)
            .ConfigureAwait(false);
        legacyFile.Refresh();

        var sourceVersion = ExtractVersionFromFile(legacyFile);
        var chain = BuildMigrationChain(sourceVersion, ActiveVersion);
        if (chain.Count == 0)
        {
            // No migration needed; materialize current instance and (optionally) persist via writer
            if (_reader is not null)
            {
                return await _reader.ReadAsync(legacyFile, cancellationToken).ConfigureAwait(false);
            }
            var raw = await File.ReadAllBytesAsync(legacyFile.FullName, cancellationToken).ConfigureAwait(false);
            var inst = MessagePackSerializer.Deserialize<TContract>(raw, SerializerOptions);
            if (_writer is null)
            {
                return inst;
            }
            var savedCurrent = await _writer.WriteAsync(inst, cancellationToken).ConfigureAwait(false);
            if (deleteLegacyOnSuccess)
            {
                await DeleteLegacyFileAsync(legacyFile, cancellationToken).ConfigureAwait(false);
            }
            return savedCurrent;
        }

        var storedPayload = await File.ReadAllBytesAsync(legacyFile.FullName, cancellationToken).ConfigureAwait(false);
        storedPayload = _encryptor.Decrypt(storedPayload);
        var serializedEnvelope = ReadSerializedEnvelope(legacyFile, storedPayload);
        var currentPayload = serializedEnvelope.ObjectPayload;
        var currentVersion = sourceVersion;
        IMigration? latestMigration = null;

        foreach (var descriptor in chain)
        {
            var migrationInstance = DeserializeMigration(currentPayload, descriptor.MigrationType);
            if (!migrationInstance.Up())
            {
                if (IsTolerant)
                {
                    return null;
                }

                throw new InvalidOperationException($"Migration '{descriptor.MigrationType.Name}' failed to upgrade '{legacyFile.FullName}'.");
            }

            currentPayload = SerializeMigration(migrationInstance);
            currentVersion = descriptor.TargetVersion;
            latestMigration = migrationInstance;
        }

        if (latestMigration is not TContract migratedInstance)
        {
            throw new InvalidOperationException(
                $"Migration result does not implement active contract '{typeof(TContract).FullName}'.");
        }

        if (_writer is null)
        {
            return migratedInstance;
        }

        var targetEnvelope = new FileObjectEnvelope<TContract>(_contractConfig)
        {
            EnvelopeCacheKey = serializedEnvelope.CacheKey,
            VersionTag = ActiveVersion,
            Location = null,
            ObjectInstance = migratedInstance
        };
        StoredContractEnvelopeAccessor<TContract>.SetEnvelope(migratedInstance, targetEnvelope);

        var saved = await _writer.WriteAsync(migratedInstance, cancellationToken).ConfigureAwait(false);
        if (deleteLegacyOnSuccess)
        {
            await DeleteLegacyFileAsync(legacyFile, cancellationToken).ConfigureAwait(false);
        }
        return saved;
    }

    private IReadOnlyList<MigrationDescriptor> BuildMigrationChain(string sourceVersion, string targetVersion)
    {
        if (string.Equals(sourceVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<MigrationDescriptor>();
        }

        if (_migrationDescriptors.Count == 0)
        {
            return Array.Empty<MigrationDescriptor>();
        }

        var chain = new List<MigrationDescriptor>();
        var guard = 0;
        var currentVersion = sourceVersion;

        while (!string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase) && guard++ < _migrationDescriptors.Count)
        {
            var next = _migrationDescriptors
                .Where(m => string.Equals(m.SourceVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.TargetVersionParsed)
                .FirstOrDefault();

            if (next is null)
            {
                if (IsTolerant)
                {
                    return Array.Empty<MigrationDescriptor>();
                }

                throw new InvalidOperationException($"No migration path found from '{sourceVersion}' to '{targetVersion}'.");
            }

            chain.Add(next);
            currentVersion = next.TargetVersion;
        }

        if (!string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            if (IsTolerant)
            {
                return Array.Empty<MigrationDescriptor>();
            }

            throw new InvalidOperationException($"Migration chain from '{sourceVersion}' to '{targetVersion}' is incomplete.");
        }

        return chain;
    }

    private IMigration DeserializeMigration(byte[] payload, Type migrationType)
        => (IMigration)MessagePackSerializer.Deserialize(migrationType, payload, ContractSerializerOptions)!;

    private byte[] SerializeMigration(IMigration migrationInstance)
        => MessagePackSerializer.Serialize(migrationInstance.GetType(), migrationInstance, ContractSerializerOptions);

    private SerializedEnvelope ReadSerializedEnvelope(FileInfo file, byte[] payload)
    {
        if (payload.Length < 4
            || payload[0] != (byte)'I'
            || payload[1] != (byte)'D'
            || payload[2] != (byte)'B'
            || payload[3] != (byte)'2')
        {
            return new SerializedEnvelope(Path.GetFileNameWithoutExtension(file.Name), payload);
        }

        using var stream = new MemoryStream(payload, writable: false);
        stream.Position = 4;
        _ = MessagePackSerializer.Deserialize<string>(stream, ContractSerializerOptions);
        var cacheKey = MessagePackSerializer.Deserialize<string>(stream, ContractSerializerOptions);
        _ = MessagePackSerializer.Deserialize<string>(stream, ContractSerializerOptions);

        var objectPayload = new byte[checked((int)(stream.Length - stream.Position))];
        stream.ReadExactly(objectPayload);
        return new SerializedEnvelope(cacheKey, objectPayload);
    }

    protected static async ValueTask DeleteLegacyFileAsync(FileInfo legacyFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (legacyFile.Exists)
        {
            legacyFile.Delete();
        }

        var parent = legacyFile.Directory;
        if (parent is not null && !parent.EnumerateFileSystemInfos().Any())
        {
            parent.Delete();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static MessagePackSerializerOptions ResolveSerializerOptions(IObjectSerializer<TContract> serializer)
    {
        if (serializer is BinaryObjectSerializer<TContract> binarySerializer)
        {
            var field = typeof(BinaryObjectSerializer<TContract>)
                .GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(binarySerializer) is MessagePackSerializerOptions options)
            {
                return options;
            }
        }

        return MessagePackSerializerOptions.Standard;
    }

    protected static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }

    protected static Version ParseVersionSafe(string version)
        => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);

    protected static string ExtractVersionFromFile(FileInfo file)
        => file.Directory?.Name ?? string.Empty;

    protected sealed class MigrationDescriptor
    {
        internal MigrationDescriptor(string sourceVersion, string targetVersion, Type migrationType)
        {
            SourceVersion = sourceVersion ?? throw new ArgumentNullException(nameof(sourceVersion));
            TargetVersion = targetVersion ?? throw new ArgumentNullException(nameof(targetVersion));
            MigrationType = migrationType ?? throw new ArgumentNullException(nameof(migrationType));
            SourceVersionParsed = ParseVersionSafe(SourceVersion);
            TargetVersionParsed = ParseVersionSafe(TargetVersion);
        }

        internal string SourceVersion { get; }
        internal string TargetVersion { get; }
        internal Type MigrationType { get; }
        internal Version SourceVersionParsed { get; }
        internal Version TargetVersionParsed { get; }
    }

    private sealed record SerializedEnvelope(string CacheKey, byte[] ObjectPayload);
}

