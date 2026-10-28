using Idb.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using Idb.Libraries.Repositories;

namespace Idb.Transport;

/// <summary>Migration and validation code is registered by the trusted server deployment, never supplied over TCP.</summary>
public sealed record TcpStoreVersion(CompatibilityManifest Manifest,
    Func<CompatibilityManifest, string, byte[], CancellationToken, ValueTask<byte[]>>? UpgradeAsync,
    Action<string, byte[]> Validate);
public interface ITcpStoreVersionModule
{
    IReadOnlyList<TcpStoreVersion> Versions { get; }
}

/// <summary>
/// A local master selects the newest deployed version before accepting TCP clients. Each upgrade
/// writes a separate store and publishes its active pointer only after every record verifies.
/// Old stores remain available for recovery; downgrade is never automatic.
/// </summary>
public sealed class VersionedTcpStore : IAsyncDisposable
{
    private readonly FileStream _ownership;
    private readonly SegmentCoreEncodedEnvelopeStore _store;
    public TcpStoreRegistration Registration { get; }
    public string RunningVersion => Registration.Manifest.ContractVersion;
    private VersionedTcpStore(FileStream ownership, SegmentCoreEncodedEnvelopeStore store, TcpStoreRegistration registration)
        => (_ownership, _store, Registration) = (ownership, store, registration);

    // Preserve the original method for already compiled consumers.
    public static ValueTask<VersionedTcpStore> OpenAsync(string root, string alias,
        IEnumerable<TcpStoreVersion> deployedVersions, TransportLimits? limits, int maximumKeys,
        CancellationToken token, string? legacyStoreDirectory, CompatibilityManifest? legacyManifest,
        int? maximumPartitionKeys)
        => OpenAsync(root, alias, deployedVersions, limits, maximumKeys, token, legacyStoreDirectory, legacyManifest, maximumPartitionKeys, telemetry: null);

    public static ValueTask<VersionedTcpStore> OpenAsync(string root, string alias,
        IEnumerable<TcpStoreVersion> deployedVersions, TransportLimits? limits, int maximumKeys,
        CancellationToken token, string? legacyStoreDirectory, CompatibilityManifest? legacyManifest,
        int? maximumPartitionKeys, IDatabaseTelemetry? telemetry)
        => OpenAsync(root, alias, deployedVersions, limits, maximumKeys, token, legacyStoreDirectory,
            legacyManifest, maximumPartitionKeys, telemetry, keyIndexFactory: null);

    public static async ValueTask<VersionedTcpStore> OpenAsync(string root, string alias,
        IEnumerable<TcpStoreVersion> deployedVersions, TransportLimits? limits = null, int maximumKeys = 100_000,
        CancellationToken token = default, string? legacyStoreDirectory = null, CompatibilityManifest? legacyManifest = null,
        int? maximumPartitionKeys = null, IDatabaseTelemetry? telemetry = null,
        IPartitionKeyIndexFactory? keyIndexFactory = null)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var ownership = new FileStream(Path.Combine(root, ".version-owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SegmentCoreEncodedEnvelopeStore? current = null;
        try
        {
            var plans = deployedVersions.OrderBy(plan => Version.Parse(plan.Manifest.ContractVersion)).ToArray();
            if (plans.Length == 0 || plans.Select(plan => plan.Manifest.ContractVersion).Distinct().Count() != plans.Length)
                throw new ArgumentException("Register unique deployed versions.");
            foreach (var plan in plans)
            {
                plan.Manifest.Validate(); ArgumentNullException.ThrowIfNull(plan.Validate);
                if (plan.Manifest.ContractIdentity != plans[0].Manifest.ContractIdentity) throw new ArgumentException("Mixed contract identities.");
            }
            TransportValidation.Alias(alias);
            var pointer = new FileRepositoryCursorStore(Path.Combine(root, "active-version.json"));
            var active = await pointer.ReadAsync(alias, token).ConfigureAwait(false);
            int currentIndex = -1;
            bool adopting = false;
            if (active is null && Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != ".version-owner"))
                throw new InvalidDataException("A nonempty unversioned root requires explicit offline adoption into a separate empty version root.");
            string? directory = null;
            if (active is not null)
            {
                var parts = active.Split('|');
                if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) throw new InvalidDataException("Invalid active store pointer.");
                currentIndex = Array.FindIndex(plans, plan => plan.Manifest.ContractVersion == parts[0]);
                if (currentIndex < 0) throw new InvalidOperationException("The active version is missing from this server deployment. Downgrade is unsupported.");
                directory = Path.Combine(root, parts[1]);
                var manifest = JsonSerializer.Deserialize<CompatibilityManifest>(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), token).ConfigureAwait(false));
                if (manifest != plans[currentIndex].Manifest) throw new InvalidDataException("A deployed version changed its immutable manifest.");
                current = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(directory, "data"), limits,
                    maximumKeys, token, maximumPartitionKeys: maximumPartitionKeys, telemetry: telemetry, keyIndexFactory: keyIndexFactory).ConfigureAwait(false);
                await ValidateExistingStoreAsync(current, plans[currentIndex], limits, token).ConfigureAwait(false);
            }
            if (active is null && legacyStoreDirectory is not null)
            {
                currentIndex = Array.FindIndex(plans, plan => plan.Manifest == legacyManifest);
                if (currentIndex < 0) throw new ArgumentException("Register the exact legacy manifest before adoption.");
                if (!Directory.Exists(legacyStoreDirectory)) throw new DirectoryNotFoundException("Legacy store does not exist.");
                current = await SegmentCoreEncodedEnvelopeStore.OpenAsync(legacyStoreDirectory, limits,
                    maximumKeys, token, maximumPartitionKeys: maximumPartitionKeys, telemetry: telemetry, keyIndexFactory: keyIndexFactory).ConfigureAwait(false);
                adopting = true;
            }
            // An empty master can start directly at the newest version.
            int first = currentIndex < 0 ? plans.Length - 1 : adopting ? currentIndex : currentIndex + 1;
            for (int index = first; index < plans.Length; index++)
            {
                var target = plans[index];
                var generation = Guid.NewGuid().ToString("N");
                directory = Path.Combine(root, generation);
                Directory.CreateDirectory(directory);
                var next = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(directory, "data"), limits,
                    maximumKeys, token, maximumPartitionKeys: maximumPartitionKeys, telemetry: telemetry, keyIndexFactory: keyIndexFactory).ConfigureAwait(false);
                try
                {
                    if (current is not null)
                    {
                        if (!adopting && target.UpgradeAsync is null) throw new InvalidOperationException($"Missing migration to {target.Manifest.ContractVersion}.");
                        var changePartitions = current.GetChangePartitions();
                        string? after = null;
                        do
                        {
                            var page = await current.EnumerateAsync(Math.Min(128, (limits ?? new()).MaximumPageItems), after, token).ConfigureAwait(false);
                            foreach (var record in page.Records!)
                            {
                                var partition = SubjectPartition(record.Key);
                                if (partition.Length != 0 && !changePartitions.Contains(partition)) partition = "";
                                var logicalKey = record.Key[partition.Length..];
                                byte[] migrated;
                                if (logicalKey == ".idb-remote-repository-clock-v1")
                                {
                                    ValidateClock(record.Payload);
                                    migrated = record.Payload;
                                }
                                else
                                {
                                    migrated = adopting ? record.Payload
                                        : await target.UpgradeAsync!(plans[currentIndex].Manifest, record.Key, record.Payload, token).ConfigureAwait(false);
                                    target.Validate(record.Key, migrated);
                                }

                                await next.ExecuteAsync(new() { Command = EncodedStoreCommand.Put, Key = record.Key,
                                    Payload = migrated, Partition = partition }, token).ConfigureAwait(false);
                            }
                            after = page.NextAfterKey;
                        } while (after is not null);
                    }
                    await using (var manifestFile = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        await manifestFile.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(target.Manifest), token).ConfigureAwait(false);
                        manifestFile.Flush(true);
                    }
                    await next.ExecuteAsync(new() { Command = EncodedStoreCommand.Checkpoint }, token).ConfigureAwait(false);
                    await pointer.WriteAsync(alias, target.Manifest.ContractVersion + "|" + generation, token).ConfigureAwait(false);
                }
                catch { await next.DisposeAsync().ConfigureAwait(false); throw; }
                if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
                current = next; currentIndex = index; adopting = false;
            }
            var activePlan = plans[currentIndex];
            return new(ownership, current!, new(alias, activePlan.Manifest,
                new ValidatingEncodedEnvelopeStore(current!, activePlan)));
        }
        catch { if (current is not null) await current.DisposeAsync().ConfigureAwait(false); ownership.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    { try { await _store.DisposeAsync().ConfigureAwait(false); } finally { _ownership.Dispose(); } }

    private static async ValueTask ValidateExistingStoreAsync(SegmentCoreEncodedEnvelopeStore store,
        TcpStoreVersion plan, TransportLimits? limits, CancellationToken token)
    {
        var changePartitions = store.GetChangePartitions();
        string? after = null;
        do
        {
            var page = await store.EnumerateAsync(Math.Min(128, (limits ?? new()).MaximumPageItems), after, token)
                .ConfigureAwait(false);
            foreach (var record in page.Records!)
            {
                var partition = SubjectPartition(record.Key);
                if (partition.Length != 0 && !changePartitions.Contains(partition)) partition = "";
                ValidatePayload(plan, record.Key, record.Payload, partition);
            }
            after = page.NextAfterKey;
        } while (after is not null);
    }

    private static string SubjectPartition(string physicalKey) =>
        ScopedEncodedStore.TryGetPrefix(physicalKey, out var prefix) ? prefix : "";

    private static void ValidatePayload(TcpStoreVersion plan, string physicalKey, byte[] payload, string partition)
    {
        if (physicalKey.AsSpan(partition.Length).SequenceEqual(".idb-remote-repository-clock-v1"))
        {
            ValidateClock(payload);
            return;
        }
        plan.Validate(physicalKey, payload.ToArray());
    }

    private static void ValidateClock(byte[] payload)
    {
        if (payload.Length != 16) throw new InvalidDataException("Invalid remote repository clock.");
        var floor = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (floor < 0 || BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8)) != ~floor)
            throw new InvalidDataException("Invalid remote repository clock.");
    }

    private sealed class ValidatingEncodedEnvelopeStore(IEncodedEnvelopeStore inner, TcpStoreVersion plan)
        : IEncodedEnvelopeStore
    {
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            var owned = request;
            if (request.Command == EncodedStoreCommand.Put && request.Payload is { } payload)
            {
                var copy = payload.ToArray();
                if (request.Key is { } key) ValidatePayload(plan, key, copy, request.Partition);
                owned = request with { Payload = copy };
            }
            else if (request.Command == EncodedStoreCommand.ApplyBatch && request.Mutations is { } mutations)
            {
                var copies = mutations.Select(mutation => mutation is null ? null! :
                    new EncodedMutation(mutation.Key, mutation.Payload?.ToArray())).ToArray();
                foreach (var mutation in copies)
                    if (mutation?.Payload is { } mutationPayload)
                        ValidatePayload(plan, mutation.Key, mutationPayload, request.Partition);
                owned = request with { Mutations = copies };
            }
            return inner.ExecuteAsync(owned, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
