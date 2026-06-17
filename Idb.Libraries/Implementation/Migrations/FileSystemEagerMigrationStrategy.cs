using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using System.Runtime.CompilerServices;

namespace Idb.Libraries.Implementation.Migrations;

/// <summary>
/// Eager migration strategy that upgrades legacy payloads during initialisation.
/// </summary>
public sealed class FileSystemEagerMigrationStrategy<TContract> : FileSystemMigrationStrategyBase<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public FileSystemEagerMigrationStrategy(
        IDbContractConfigDto<TContract> contractConfig,
        IObjectSerializer<TContract> serializer,
        IEnumerable<IMigration> migrations,
        MigrationStrategyType strategyType,
        IEnvelopeStorageIdentityFactory<TContract> storageIdentityFactory,
        IEncryptor encryptor,
        IFileWriteCoordinator fileWriteCoordinator,
        StoragePolicy storagePolicy,
        IObjectReader<TContract> reader,
        IObjectWriter<TContract> writer)
        : base(contractConfig, serializer, storageIdentityFactory, migrations, strategyType, encryptor, fileWriteCoordinator, storagePolicy, reader, writer)
    {
        if (strategyType is not (MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant))
        {
            throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Eager strategy requires EagerStrict or EagerTolerant.");
        }
    }

    public override async IAsyncEnumerable<TContract> OnInitializeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!DataRootDirectory.Exists)
        {
            yield break;
        }

        var legacyFiles = DataRootDirectory
            .EnumerateDirectories()
            .Where(d => !string.Equals(d.Name, ActiveVersion, StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => d.EnumerateFiles($"*{FileExtension}", SearchOption.TopDirectoryOnly))
            .ToList();

        if (legacyFiles.Count == 0)
        {
            yield break;
        }

        var dop = EffectivePolicy.NormalizedConcurrency;

        if (StrategyType == MigrationStrategyType.EagerStrict)
        {
            var written = new System.Collections.Concurrent.ConcurrentBag<(string NewPath, FileInfo LegacyFile, TContract Saved)>();
            try
            {
                await Parallel.ForEachAsync(legacyFiles, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cancellationToken }, async (file, ct) =>
                {
                    var saved = await MigrateFileAsync(file, deleteLegacyOnSuccess: false, ct).ConfigureAwait(false);
                    if (saved?.Envelope?.Location is string path && !string.IsNullOrWhiteSpace(path))
                    {
                        written.Add((path, file, saved));
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
                foreach (var entry in written)
                {
                    try { if (File.Exists(entry.NewPath)) File.Delete(entry.NewPath); } catch { }
                }
                throw;
            }

            foreach (var entry in written)
            {
                try { await DeleteLegacyFileAsync(entry.LegacyFile, cancellationToken).ConfigureAwait(false); } catch { }
                yield return entry.Saved;
            }
        }
        else // EagerTolerant
        {
            var results = new System.Collections.Concurrent.ConcurrentBag<TContract>();
            await Parallel.ForEachAsync(legacyFiles, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cancellationToken }, async (file, ct) =>
            {
                try
                {
                    var saved = await MigrateFileAsync(file, deleteLegacyOnSuccess: true, ct).ConfigureAwait(false);
                    if (saved is not null)
                    {
                        results.Add(saved);
                    }
                }
                catch
                {
                    // tolerant: best-effort; keep legacy on failure
                }
            }).ConfigureAwait(false);

            foreach (var item in results)
            {
                yield return item;
            }
        }
    }
}
