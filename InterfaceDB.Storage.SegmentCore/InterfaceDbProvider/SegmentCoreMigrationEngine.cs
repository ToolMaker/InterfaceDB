using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Idb.Storage.SegmentCore;

internal sealed class SegmentCoreMigrationEngine<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
    private readonly string _activeVersion;
    private readonly IEncryptor _encryptor;
    private readonly IReadOnlyList<MigrationDescriptor> _descriptors;

    public SegmentCoreMigrationEngine(
        string activeVersion,
        IEncryptor encryptor,
        IEnumerable<IMigration> migrations)
    {
        _activeVersion = activeVersion;
        _encryptor = encryptor;
        _descriptors = migrations
            .Select(static migration => new MigrationDescriptor(
                migration.SourceVersion,
                migration.TargetVersion,
                migration.GetType()))
            .OrderBy(static descriptor => descriptor.Source)
            .ThenBy(static descriptor => descriptor.Target)
            .ToArray();
        ValidateDefinitions();
    }

    public TContract Migrate(
        string physicalKey,
        string sourceVersion,
        byte[] storedPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        ArgumentNullException.ThrowIfNull(storedPayload);
        var chain = BuildChain(sourceVersion);
        var envelope = ReadEnvelope(_encryptor.Decrypt(storedPayload));
        if (!string.IsNullOrWhiteSpace(envelope.CacheKey)
            && !string.Equals(envelope.CacheKey, physicalKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Legacy SegmentCore key '{physicalKey}' does not match serialized key '{envelope.CacheKey}'.");
        }

        var payload = envelope.ObjectPayload;
        IMigration? migrated = null;
        foreach (var descriptor in chain)
        {
            try
            {
                migrated = (IMigration)MessagePackSerializer.Deserialize(
                    descriptor.MigrationType,
                    payload,
                    SerializerOptions)!;
                if (!migrated.Up())
                {
                    throw new MigrationExecutionException(
                        descriptor.MigrationType,
                        descriptor.SourceText,
                        descriptor.TargetText,
                        physicalKey,
                        physicalKey);
                }

                payload = MessagePackSerializer.Serialize(
                    migrated.GetType(),
                    migrated,
                    SerializerOptions);
            }
            catch (MigrationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new MigrationExecutionException(
                    descriptor.MigrationType,
                    descriptor.SourceText,
                    descriptor.TargetText,
                    physicalKey,
                    physicalKey,
                    exception);
            }
        }

        return migrated as TContract
            ?? throw new MigrationExecutionException(
                chain[^1].MigrationType,
                sourceVersion,
                _activeVersion,
                physicalKey,
                physicalKey,
                new InvalidCastException(
                    $"Migration result does not implement '{typeof(TContract).FullName}'."));
    }

    private IReadOnlyList<MigrationDescriptor> BuildChain(string sourceVersion)
    {
        if (!Version.TryParse(sourceVersion, out var current)
            || !Version.TryParse(_activeVersion, out var target))
        {
            throw new InvalidMigrationDefinitionException(
                typeof(SegmentCoreMigrationEngine<TContract>),
                sourceVersion,
                _activeVersion,
                "source and active versions must be valid System.Version values.");
        }

        var chain = new List<MigrationDescriptor>();
        var visited = new HashSet<Version>();
        while (current != target)
        {
            if (!visited.Add(current))
            {
                throw new InvalidMigrationDefinitionException(
                    typeof(SegmentCoreMigrationEngine<TContract>),
                    sourceVersion,
                    _activeVersion,
                    "the migration chain contains a cycle.");
            }

            var candidates = _descriptors.Where(descriptor => descriptor.Source == current).ToArray();
            if (candidates.Length == 0)
            {
                throw new MissingMigrationException(sourceVersion, _activeVersion, current.ToString());
            }

            if (candidates.Length > 1)
            {
                throw new AmbiguousMigrationException(
                    current.ToString(),
                    candidates.Select(static descriptor => descriptor.MigrationType));
            }

            var next = candidates[0];
            if (next.Target <= current || next.Target > target)
            {
                throw new InvalidMigrationDefinitionException(
                    next.MigrationType,
                    next.SourceText,
                    next.TargetText,
                    "migrations must advance toward, and not beyond, the active version.");
            }

            chain.Add(next);
            current = next.Target;
        }

        return chain;
    }

    private void ValidateDefinitions()
    {
        foreach (var duplicate in _descriptors
                     .GroupBy(static descriptor => descriptor.Source)
                     .Where(static group => group.Count() > 1))
        {
            throw new AmbiguousMigrationException(
                duplicate.Key.ToString(),
                duplicate.Select(static descriptor => descriptor.MigrationType));
        }
    }

    private static SerializedEnvelope ReadEnvelope(byte[] payload)
    {
        if (payload.Length < 4
            || payload[0] != (byte)'I'
            || payload[1] != (byte)'D'
            || payload[2] != (byte)'B'
            || payload[3] is not ((byte)'2' or (byte)'3' or (byte)'4'))
        {
            return new SerializedEnvelope(string.Empty, payload);
        }

        var hasCacheFlag = payload[3] is (byte)'3' or (byte)'4';
        var hasEncryptedFields = payload[3] == (byte)'4';
        using var stream = new MemoryStream(payload, writable: false);
        stream.Position = 4;
        _ = MessagePackSerializer.Deserialize<string>(stream, SerializerOptions);
        var cacheKey = MessagePackSerializer.Deserialize<string>(stream, SerializerOptions);
        _ = MessagePackSerializer.Deserialize<string>(stream, SerializerOptions);
        if (hasCacheFlag)
        {
            _ = MessagePackSerializer.Deserialize<bool>(stream, SerializerOptions);
        }

        if (hasEncryptedFields)
        {
            _ = MessagePackSerializer.Deserialize<Dictionary<string, EncryptedFieldValue>>(
                stream,
                SerializerOptions);
        }

        var objectPayload = new byte[checked((int)(stream.Length - stream.Position))];
        stream.ReadExactly(objectPayload);
        return new SerializedEnvelope(cacheKey, objectPayload);
    }

    private sealed record SerializedEnvelope(string CacheKey, byte[] ObjectPayload);

    private sealed record MigrationDescriptor
    {
        public MigrationDescriptor(string source, string target, Type migrationType)
        {
            SourceText = source;
            TargetText = target;
            MigrationType = migrationType;
            if (!Version.TryParse(source, out var parsedSource)
                || !Version.TryParse(target, out var parsedTarget))
            {
                throw new InvalidMigrationDefinitionException(
                    migrationType,
                    source,
                    target,
                    "source and target must be valid System.Version values.");
            }

            Source = parsedSource;
            Target = parsedTarget;
        }

        public string SourceText { get; }

        public string TargetText { get; }

        public Version Source { get; }

        public Version Target { get; }

        public Type MigrationType { get; }
    }
}
