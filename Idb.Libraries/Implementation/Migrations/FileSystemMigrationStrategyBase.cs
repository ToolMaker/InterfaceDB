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
using Idb.Libraries.Implementation.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Shared migration helpers for file-system-backed strategies.
/// </summary>
internal abstract class FileSystemMigrationStrategyBase<TContract> : IMigrationStrategy<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IDbContractConfigDto<TContract, FileInfo> _contractConfig;
    private readonly IReadOnlyList<MigrationDescriptor> _migrationDescriptors;

    protected FileSystemMigrationStrategyBase(
        IDbContractConfigDto<TContract, FileInfo> contractConfig,
        IObjectSerializer<TContract> serializer,
        IFileNameFactory<TContract, FileInfo> fileNameFactory,
        IEnumerable<IMigration> migrations,
        MigrationStrategyType strategyType)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        FileNameFactory = fileNameFactory ?? throw new ArgumentNullException(nameof(fileNameFactory));
        StrategyType = strategyType;

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        DataRootDirectory = new DirectoryInfo(Path.GetFullPath(_contractConfig.DataRoot!));
        ActiveVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        FileExtension = NormalizeExtension(Serializer.FileExtension);
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

    protected IObjectSerializer<TContract> Serializer { get; }

    protected IFileNameFactory<TContract, FileInfo> FileNameFactory { get; }

    protected DirectoryInfo DataRootDirectory { get; }

    protected string ActiveVersion { get; }

    protected string FileExtension { get; }

    protected bool IsTolerant { get; }

    protected MessagePackSerializerOptions SerializerOptions { get; }

    protected IReadOnlyList<MigrationDescriptor> MigrationDescriptors => _migrationDescriptors;

    public abstract IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>> OnInitializeAsync(
        CancellationToken cancellationToken = default);

    public virtual IObjectEnvelope<TContract, FileInfo> Migrate(
        IObjectEnvelope<TContract, FileInfo> envelopeToMigrate,
        CancellationToken cancellationToken = default)
    {
        if (envelopeToMigrate is null)
        {
            throw new ArgumentNullException(nameof(envelopeToMigrate));
        }

        if (!TryResolveLegacyLocation(envelopeToMigrate, out var legacyFile, out var legacyVersion))
        {
            return envelopeToMigrate;
        }

        var migrated = MigrateFileAsync(legacyFile, legacyVersion, ActiveVersion, cancellationToken)
            .GetAwaiter()
            .GetResult();

        return migrated ?? envelopeToMigrate;
    }

    protected IEnumerable<DirectoryInfo> EnumerateLegacyDirectories()
    {
        if (!DataRootDirectory.Exists)
        {
            yield break;
        }

        var ordered = DataRootDirectory
            .EnumerateDirectories()
            .Select(dir => new { Directory = dir, Version = ParseVersionSafe(dir.Name) })
            .OrderBy(pair => pair.Version);

        foreach (var entry in ordered)
        {
            if (string.Equals(entry.Directory.Name, ActiveVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return entry.Directory;
        }
    }

    protected async IAsyncEnumerable<IObjectEnvelope<TContract, FileInfo>?> ProcessDirectoryAsync(
        DirectoryInfo legacyDirectory,
        string sourceVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!legacyDirectory.Exists)
        {
            yield break;
        }

        foreach (var legacyFile in legacyDirectory.EnumerateFiles($"*{FileExtension}", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var migrated = await MigrateFileAsync(legacyFile, sourceVersion, ActiveVersion, cancellationToken).ConfigureAwait(false);
            yield return migrated;
        }
    }

    protected ValueTask<IObjectEnvelope<TContract, FileInfo>?> MigrateFileAsync(
        FileInfo legacyFile,
        string sourceVersion,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        return MigrateInternalAsync(legacyFile, sourceVersion, targetVersion, cancellationToken);
    }

    protected bool TryResolveLegacyLocation(
        IObjectEnvelope<TContract, FileInfo> envelope,
        out FileInfo legacyFile,
        out string legacyVersion)
    {
        legacyFile = envelope.Location;
        legacyVersion = envelope.VersionTag;

        if (legacyFile is null || string.IsNullOrWhiteSpace(legacyVersion))
        {
            return false;
        }

        return !string.Equals(legacyVersion, ActiveVersion, StringComparison.OrdinalIgnoreCase);
    }

    protected DirectoryInfo ResolveVersionDirectory(string versionTag)
        => new(Path.Combine(DataRootDirectory.FullName, versionTag));

    private async ValueTask<IObjectEnvelope<TContract, FileInfo>?> MigrateInternalAsync(
        FileInfo legacyFile,
        string sourceVersion,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        var chain = BuildMigrationChain(sourceVersion, targetVersion);
        if (chain.Count == 0)
        {
            return null;
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

        var targetDirectory = ResolveVersionDirectory(targetVersion);
        Directory.CreateDirectory(targetDirectory.FullName);

        var upgradedContract = Serializer.Deserialize(currentPayload);
        var normalizedId = FileNameFactory.Create(upgradedContract, legacyFile, Path.GetFileNameWithoutExtension(legacyFile.Name));
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            normalizedId = Guid.NewGuid().ToString("N");
        }

        var targetPath = Path.Combine(targetDirectory.FullName, normalizedId + FileExtension);
        await File.WriteAllBytesAsync(targetPath, currentPayload, cancellationToken).ConfigureAwait(false);
        await DeleteLegacyFileAsync(legacyFile, cancellationToken).ConfigureAwait(false);

        var envelope = new FileBackedObjectEnvelope<TContract>(
            new FileInfo(targetPath),
            targetVersion,
            upgradedContract,
            currentPayload,
            static (_, info) => info.FullName);

        upgradedContract.Envelope = envelope;

        return envelope;
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

    private static async ValueTask DeleteLegacyFileAsync(FileInfo legacyFile, CancellationToken cancellationToken)
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

