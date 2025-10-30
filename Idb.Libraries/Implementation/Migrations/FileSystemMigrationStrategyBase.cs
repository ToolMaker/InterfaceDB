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
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using MessagePack;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Shared migration helpers for file-system-backed strategies.
/// </summary>
public abstract class FileSystemMigrationStrategyBase<TContract> : IMigrationStrategy<TContract, string>
    where TContract : class, IRepoDto<TContract, string>
{
    private readonly IDbContractConfigDto<TContract, string> _contractConfig;
    private readonly IReadOnlyList<MigrationDescriptor> _migrationDescriptors;
    private readonly IObjectReader<TContract, string>? _reader;
    private readonly IObjectWriter<TContract, string>? _writer;

    protected FileSystemMigrationStrategyBase(
        IDbContractConfigDto<TContract, string> contractConfig,
        IObjectSerializer<TContract, string> serializer,
        IEnvelopeKeyAndLocationFactory<TContract, string> fileNameFactory,
        IEnumerable<IMigration> migrations,
        MigrationStrategyType strategyType,
        IObjectReader<TContract, string>? reader = null,
        IObjectWriter<TContract, string>? writer = null)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        FileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        _reader = reader;
        _writer = writer;
        StrategyType = strategyType;

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        DataRootDirectory = new DirectoryInfo(Path.GetFullPath(_contractConfig.DataRoot!));
        ActiveVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        FileExtension = NormalizeExtension(_contractConfig.FileExtension.NormalizeExtension());
        SerializerOptions = ResolveSerializerOptions(Serializer);
        IsTolerant = StrategyType == MigrationStrategyType.EagerTolerant;

        _migrationDescriptors = (migrations ?? Enumerable.Empty<IMigration>())
            .Where(m => m is not null)
            .Select(m => new MigrationDescriptor(m.SourceVersion, m.TargetVersion, m.GetType()))
            .OrderBy(m => m.SourceVersionParsed)
            .ThenBy(m => m.TargetVersionParsed)
            .ToList();
    }

    public MigrationStrategyType StrategyType { get; }

    protected IObjectSerializer<TContract,string> Serializer { get; }

    protected IEnvelopeKeyAndLocationFactory<TContract, string> FileNameFactory { get; }

    protected DirectoryInfo DataRootDirectory { get; }

    protected string ActiveVersion { get; }

    protected string FileExtension { get; }

    protected bool IsTolerant { get; }

    protected MessagePackSerializerOptions SerializerOptions { get; }

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

        var currentPayload = await File.ReadAllBytesAsync(legacyFile.FullName, cancellationToken).ConfigureAwait(false);
        var currentVersion = sourceVersion;

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
        }

        var migratedInstance = MessagePackSerializer.Deserialize<TContract>(currentPayload, SerializerOptions);
        if (_writer is null)
        {
            return migratedInstance;
        }
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
        => (IMigration)MessagePackSerializer.Deserialize(migrationType, payload, SerializerOptions)!;

    private byte[] SerializeMigration(IMigration migrationInstance)
        => MessagePackSerializer.Serialize(migrationInstance.GetType(), migrationInstance, SerializerOptions);

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

    private static MessagePackSerializerOptions ResolveSerializerOptions(IObjectSerializer<TContract, string> serializer)
    {
        if (serializer is BinaryObjectSerializer<TContract, string> binarySerializer)
        {
            var field = typeof(BinaryObjectSerializer<TContract, string>)
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
}

