using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Resumable, verified storage handover with explicitly configured partial availability.</summary>
public sealed class StorageMigration<T>(StorageMigrationOptions options,
    IStorageMigrationSource<T> source, IStorageMigrationDestination<T> destination,
    IStorageMigrationTransform<T> transform, IStorageMigrationFingerprint<T> fingerprint,
    IStorageMigrationStateStore stateStore, IStorageMigrationHistory<T>? history = null,
    IStorageMigrationLegacyRevisionRepair<T>? revisionRepair = null) :
    IStorageMigration<T>, IStorageMigrationCapabilities, IStorageMigrationServiceAccess,
    IStorageMigrationProgress, IRepositoryDataAvailability where T : class, IStoredContract<T>
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRepository<T>? _ready;
    private IStorageMigrationEndpoint<T>? _selected;
    private ExceptionDispatchInfo? _failure;
    private bool _requiresFreshProviders;
    private StorageMigrationProgress _progress = new(StorageMigrationPhase.NotStarted, 0, 0, 0);
    private IStorageMigrationWriteReservations? _reservations;
    public StorageMigrationProgress Progress => Volatile.Read(ref _progress);
    public bool IsDataComplete => Progress.Phase == StorageMigrationPhase.Complete;
    private void Report(StorageMigrationPhase phase, int total = 0, int available = 0, int pending = 0)
        => Volatile.Write(ref _progress, new(phase, total, available, pending));
    private void ReportCompleted(StorageMigrationState state)
        => Volatile.Write(ref _progress, new(StorageMigrationPhase.Complete, state.RecordCount, state.VerifiedRecords, 0)
        {
            Retirement = state.Retired ? StorageMigrationRetirement.Retired : StorageMigrationRetirement.Pending,
            RetainedSourceRecords = state.RetainedSourceRecords, RetirementIssue = state.RetirementIssue
        });
    public async ValueTask RetryAsync(CancellationToken cancellationToken = default)
        => await OpenCoreAsync(true, cancellationToken).ConfigureAwait(false);
    private sealed class CoordinatorAccess(StorageMigration<T> owner) : IStorageMigrationAccess
    {
        public void EnsureReadable() => owner._failure?.Throw();
        public ValueTask<IAsyncDisposable> AcquireWriteAsync(IEnumerable<string?> keys, CancellationToken ct)
            => owner.AcquireWriteAsync(keys, ct);
    }
    private async ValueTask<IAsyncDisposable> AcquireWriteAsync(IEnumerable<string?> keys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _failure?.Throw();
        if (IsDataComplete) return NoopWriteLease.Instance;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _failure?.Throw();
            foreach (var key in keys) _reservations?.ValidateMutation(key);
            return new WriteLease(_gate);
        }
        catch { _gate.Release(); throw; }
    }
    private sealed class WriteLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
    private sealed class NoopWriteLease : IAsyncDisposable
    {
        internal static readonly NoopWriteLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public bool OwnsServiceRegistration(string databaseKey)
        => string.Equals(source.ServiceRegistrationKey, databaseKey, StringComparison.Ordinal)
            || string.Equals(destination.ServiceRegistrationKey, databaseKey, StringComparison.Ordinal);
    public object GetReadyService(Type serviceType)
    {
        _failure?.Throw();
        RepositoryDataAvailability.RequireComplete(this);
        return _selected?.GetService(serviceType) ?? throw new InvalidOperationException(
            "Await the logical repository's InitializeAsync before resolving maintenance/infrastructure capabilities; the selected provider must support the requested capability.");
    }
    private IRepository<T> Ready(IStorageMigrationEndpoint<T> endpoint)
    {
        _selected = endpoint;
        if (IsDataComplete) return _ready = endpoint.Repository;
        var selected = new StorageMigrationRepository<T>(new SelectedStorageMigration<T>(endpoint.Repository, new CoordinatorAccess(this), this));
        return _ready = selected;
    }
    private IRepository<T> ConfiguredRepository => ValidateAndSelectSource() ? source.Repository : destination.Repository;
    public ContractRepositoryType RepositoryType => ConfiguredRepository.RepositoryType;
    public string? RunningVersion => ConfiguredRepository.RunningVersion;
    public StorageDurabilityMode CommitDurability => ConfigurationCapability<IEnvelopeMetadataRepository<T>>().CommitDurability;
    public RetainedVersionCapability RetainedVersions => ConfigurationCapability<IRevisionEditingRepository<T>>().RetainedVersions;
    public RetainedVersionStatistics RetentionStatistics => ConfigurationCapability<IRevisionEditingRepository<T>>().RetentionStatistics;
    private TC ConfigurationCapability<TC>() where TC : class
        => ConfiguredRepository as TC ?? throw new NotSupportedException($"Selected provider does not provide {typeof(TC).Name}.");

    public ValueTask<IRepository<T>> OpenAsync(CancellationToken cancellationToken = default)
        => OpenCoreAsync(false, cancellationToken);

    private async ValueTask<IRepository<T>> OpenCoreAsync(bool retry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _failure?.Throw();
            if (_ready is not null && (!retry || Progress.Retirement == StorageMigrationRetirement.Retired)) return _ready;
            _requiresFreshProviders = false;
            var useSource = ValidateAndSelectSource();
            await using var stateLease = await stateStore.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (useSource)
            {
                if (state is not null)
                    throw new InvalidOperationException("Storage migration already started; refusing to reactivate the old provider.");
                await source.Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                Report(StorageMigrationPhase.Complete);
                return Ready(source);
            }

            var plan = Digest(JsonSerializer.Serialize(new { options.MigrationId,
                Contract = typeof(T).FullName + "," + typeof(T).Assembly.GetName().Name, Source = source.Identity, Destination = destination.Identity,
                SourceVersion = source.ContractVersion, Fingerprint = fingerprint.Identity, Format = 2 }));
            if (state is not null && state.Plan != plan)
                throw new InvalidOperationException("Storage migration configuration differs from its durable receipt. Do not reuse or delete a receipt to bypass this check.");
            if (state is not null && (state.Format != 2 || state.TargetVersion is null || state.SourceVersion is null
                || state.DestinationInstance is null || state.TransformIdentity is null))
                throw new InvalidDataException("Unsupported migration receipt. Use the matching migration build for controlled recovery.");
            if (state is not null && !string.Equals(state.TargetVersion, destination.ContractVersion, StringComparison.Ordinal)
                && StorageMigrationVersions.ParseOrdered(state.TargetVersion!, "Recorded target version")
                    > StorageMigrationVersions.ParseOrdered(destination.ContractVersion, "Destination version"))
                throw new InvalidOperationException("Refusing to downgrade a migrated database.");
            if (state is { Completed: true } && state.TargetVersion == destination.ContractVersion)
            {
                _requiresFreshProviders = true;
                await destination.Binding.ValidateAsync(plan, state.DestinationInstance!, cancellationToken).ConfigureAwait(false);
                await destination.ValidateCompletedDestinationAsync(cancellationToken).ConfigureAwait(false);
                await destination.Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
                state = await RetireAsync(state, cancellationToken).ConfigureAwait(false);
                ReportCompleted(state);
                return Ready(destination);
            }
            if (!options.RunOnStartup)
                throw new InvalidOperationException("Storage migration is required but RunOnStartup is disabled.");

            if (state is not null && state.TargetVersion != destination.ContractVersion)
            {
                // Finish an interrupted old stage before moving to the new target version.
                // Historical providers are constructed by DI, never by provider-specific casts here.
                await using var previous = await OpenHistoryAsync(state.TargetVersion!, cancellationToken).ConfigureAwait(false);
                if (!state.Completed)
                    state = await ResumeAsync(plan, previous.Destination, state, cancellationToken).ConfigureAwait(false);
                if (!state.Completed) throw new StorageMigrationIncompleteException(state.PendingKeys.Length);
                await previous.Destination.Binding.ValidateAsync(plan, state.DestinationInstance!, cancellationToken).ConfigureAwait(false);
                await previous.Destination.ValidateCompletedDestinationAsync(cancellationToken).ConfigureAwait(false);
                state = await RetireAsync(state, cancellationToken).ConfigureAwait(false);
                state = await TransferAsync(plan, previous.Source, destination, null, true, state.DestinationInstance, cancellationToken, state).ConfigureAwait(false);
            }
            else if (state is not null)
                state = await ResumeAsync(plan, destination, state, cancellationToken).ConfigureAwait(false);
            else
                state = await TransferAsync(plan, source, destination, null, false, null, cancellationToken).ConfigureAwait(false);
            if (!state.Completed && options.IncompleteBehavior == IncompleteMigrationBehavior.Block)
                throw new StorageMigrationIncompleteException(state.PendingKeys.Length);
            if (state.Completed)
            {
                state = await RetireAsync(state, cancellationToken).ConfigureAwait(false);
                ReportCompleted(state);
            }
            return Ready(destination);
        }
        catch (Exception error) when (_requiresFreshProviders && error is not StorageMigrationIncompleteException)
        { _failure ??= ExceptionDispatchInfo.Capture(error); Report(StorageMigrationPhase.Faulted, Progress.TotalRecords, Progress.AvailableRecords, Progress.PendingRecords); throw; }
        finally { _gate.Release(); }
    }

    private async ValueTask<IStorageMigrationHistoryHandle<T>> OpenHistoryAsync(string version, CancellationToken ct)
    {
        _requiresFreshProviders = true;
        var handle = await (history ?? throw new InvalidOperationException(
            "Configure IStorageMigrationHistory to open previous destination versions while keeping the handover enabled."))
            .OpenAsync(version, ct).ConfigureAwait(false);
        if (handle.Source.ContractVersion != version || handle.Destination.ContractVersion != version
            || handle.Destination.Identity != destination.Identity || handle.Source.Identity != destination.Identity)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("Historical provider identity/version differs from the migration configuration.");
        }
        return handle;
    }

    private async ValueTask<StorageMigrationState> RetireAsync(StorageMigrationState state, CancellationToken ct)
    {
        if (!state.Completed || state.PendingKeys.Length != 0) return state;
        if (state.Retired) return state;
        // Completion is already durable. Cleanup failures cannot invalidate committed application data.
        _requiresFreshProviders = false;
        if (!options.RetireAfterCompletion || !options.RunOnStartup)
            return state with { RetirementIssue = "RetirementDisabled" };
        try
        {
            var requested = state.CleanupRequired || options.CleanupOriginalSourceAfterCompletion && !state.SourceIsDestination;
            if (requested)
            {
                if (!options.CleanupOriginalSourceAfterCompletion)
                    return state with { RetirementIssue = "CleanupDisabled" };
                var cleanup = source.GetService(typeof(IStorageMigrationSourceCleanup<T>)) as IStorageMigrationSourceCleanup<T>
                    ?? throw new NotSupportedException("Source does not provide verified cleanup.");
                if (state.CleanupRecords is null)
                {
                    // Older completed receipts can opt in only when the original frozen inventory is still provable.
                    if (state.SourceIsDestination || state.TransformIdentity != transform.Identity)
                        return state with { RetirementIssue = "CleanupManifestUnavailable" };
                    await using var lease = await source.AcquireSourceLeaseAsync(ct).ConfigureAwait(false);
                    await source.InitializeSnapshotAsync(ct).ConfigureAwait(false);
                    var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
                    var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    var floor = await source.ReadRevisionFloorAsync(ct).ConfigureAwait(false);
                    await foreach (var original in source.ReadAllAsync(options.MaximumRecordBytes, ct).ConfigureAwait(false))
                    {
                        if (manifest.Count >= options.MaximumRecords || !manifest.TryAdd(original.Key, fingerprint.Compute(Convert(original, state.TargetVersion!))))
                            throw new InvalidDataException("Cleanup inventory exceeds its limit or contains duplicate keys.");
                        sourceHashes.Add(original.Key, fingerprint.Compute(original));
                        floor = Math.Max(floor, original.Revision);
                    }
                    if (manifest.Count != state.RecordCount || Digest(JsonSerializer.Serialize(manifest)) != state.SourceDigest
                        || floor != (state.SourceRevisionFloor ?? state.RevisionFloor))
                        return state with { RetirementIssue = "SourceChanged", RetainedSourceRecords = manifest.Count };
                    var records = (await cleanup.CaptureAsync(sourceHashes, ct).ConfigureAwait(false)).ToArray();
                    state = state with { CleanupRequired = true, CleanupRecords = records, MigrationId = options.MigrationId };
                    await stateStore.WriteAsync(state, ct).ConfigureAwait(false);
                }
                var result = await cleanup.ExecuteAsync(state.CleanupRecords!, fingerprint, options.MaximumRecordBytes,
                    options.MaximumRecords, ct).ConfigureAwait(false);
                if (result.RetainedRecords != 0)
                {
                    state = state with { RetirementIssue = "SourceRecordsRetained", RetainedSourceRecords = result.RetainedRecords };
                    await stateStore.WriteAsync(state, ct).ConfigureAwait(false);
                    return state;
                }
            }
            var retired = state with { MigrationId = options.MigrationId, Retired = true, CleanupRecords = null,
                RetainedSourceRecords = 0, RetirementIssue = null, RepairedRevisions = new(StringComparer.Ordinal),
                PublishedKeys = [], PublishedPartial = false };
            await stateStore.WriteAsync(retired, ct).ConfigureAwait(false);
            return retired;
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or AccessViolationException))
        {
            // Keep the durable recovery plan. Report a safe category, not file paths or data.
            return state with { RetirementIssue = error switch
            {
                UnauthorizedAccessException => "SourceAccessDenied", DirectoryNotFoundException => "SourceUnavailable",
                ArgumentException => "UnsafeSourcePath", NotSupportedException => "CleanupUnsupported",
                CryptographicException => "SourceDecryptionFailed",
                _ => "CleanupRetryRequired"
            }};
        }
    }

    private async ValueTask<StorageMigrationState> ResumeAsync(string plan, IStorageMigrationDestination<T> target,
        StorageMigrationState state, CancellationToken ct)
    {
        if (state.TransformIdentity != transform.Identity)
            throw new InvalidOperationException("An unfinished migration requires its original transform semantics.");
        if (!state.SourceIsDestination)
            return await TransferAsync(plan, source, target, state, false, null, ct).ConfigureAwait(false);
        await using var previous = await OpenHistoryAsync(state.SourceVersion!, ct).ConfigureAwait(false);
        await previous.Destination.Binding.ValidateAsync(plan, state.SourceInstance!, ct).ConfigureAwait(false);
        await previous.Destination.ValidateCompletedDestinationAsync(ct).ConfigureAwait(false);
        return await TransferAsync(plan, previous.Source, target, state, true, state.SourceInstance, ct).ConfigureAwait(false);
    }

    private async ValueTask<StorageMigrationState> TransferAsync(string plan, IStorageMigrationSource<T> stageSource,
        IStorageMigrationDestination<T> target, StorageMigrationState? state, bool sourceIsDestination,
        string? sourceInstance, CancellationToken cancellationToken, StorageMigrationState? previousCompletion = null)
    {
            ValidateLocations(stageSource, target);
            if (state is not null && state.SourceVersion != stageSource.ContractVersion)
                throw new InvalidDataException("Interrupted migration source version changed.");

            await using var sourceLease = await stageSource.AcquireSourceLeaseAsync(cancellationToken).ConfigureAwait(false);
            await stageSource.InitializeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var floor = await stageSource.ReadRevisionFloorAsync(cancellationToken).ConfigureAwait(false);
            var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var cleanup = !sourceIsDestination && options.CleanupOriginalSourceAfterCompletion
                ? stageSource.GetService(typeof(IStorageMigrationSourceCleanup<T>)) as IStorageMigrationSourceCleanup<T>
                    ?? throw new NotSupportedException("Original source cleanup requires an IStorageMigrationSourceCleanup implementation.")
                : null;
            var sourceFingerprints = cleanup is null ? null : new Dictionary<string, string>(StringComparer.Ordinal);
            var legacyKeys = new HashSet<string>(StringComparer.Ordinal);
            // Preflight every record before the first destination mutation. Only hashes and keys are retained.
            await foreach (var original in stageSource.ReadAllAsync(options.MaximumRecordBytes, cancellationToken).ConfigureAwait(false))
            {
                var converted = Convert(original, target.ContractVersion);
                if (original.Revision < 0) throw new InvalidDataException("A negative record revision cannot be repaired as a missing revision.");
                if (original.Revision == 0 && options.LegacyRevisions == LegacyRevisionHandling.Reject)
                    throw new InvalidDataException("Legacy zero-revision records must receive a revision through the source provider before storage migration.");
                if (original.Revision == 0) legacyKeys.Add(original.Key);
                if (manifest.Count >= options.MaximumRecords)
                    throw new InvalidDataException("Storage migration exceeds MaximumRecords.");
                if (!manifest.TryAdd(original.Key, fingerprint.Compute(converted)))
                    throw new InvalidDataException("Source contains duplicate physical keys.");
                sourceFingerprints?.Add(original.Key, fingerprint.Compute(original));
                floor = Math.Max(floor, original.Revision);
            }
            var sourceDigest = Digest(JsonSerializer.Serialize(manifest));
            if (state is not null && (state.SourceDigest != sourceDigest || (state.SourceRevisionFloor ?? state.RevisionFloor) != floor || state.RecordCount != manifest.Count))
                throw new InvalidOperationException("Source changed since the interrupted storage migration. Restore the frozen source before resuming.");
            var inheritedCleanup = state ?? (previousCompletion is { Retired: false } ? previousCompletion : null);
            var cleanupRecords = cleanup is null ? inheritedCleanup?.CleanupRecords
                : (await cleanup.CaptureAsync(sourceFingerprints!, cancellationToken).ConfigureAwait(false)).ToArray();
            if (cleanup is not null && cleanupRecords is not null && (cleanupRecords.Length != manifest.Count
                || cleanupRecords.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != manifest.Count
                || cleanupRecords.Any(r => !manifest.ContainsKey(r.Key))))
                throw new InvalidDataException("Cleanup plan must account for every source record.");

            var sourceFloor = floor;
            var total = manifest.Count;
            var revisions = state is null ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(state.RepairedRevisions, StringComparer.Ordinal);
            if (revisions.Any(p => !legacyKeys.Contains(p.Key) || p.Value <= sourceFloor)
                || revisions.Values.Distinct().Count() != revisions.Count)
                throw new InvalidDataException("Invalid legacy revision assignments in migration receipt.");
            foreach (var revision in revisions.Values) floor = Math.Max(floor, revision);
            if (state is { PublishedPartial: true })
            {
                await target.Binding.ValidateAsync(plan, state.DestinationInstance!, cancellationToken).ConfigureAwait(false);
                floor = Math.Max(floor, await target.ReadRevisionFloorAsync(cancellationToken).ConfigureAwait(false));
            }
            var published = new HashSet<string>(state?.PublishedKeys ?? [], StringComparer.Ordinal);
            if (published.Any(key => !manifest.ContainsKey(key) || legacyKeys.Contains(key) && !revisions.ContainsKey(key))
                || published.Count > 0 && state is not { PublishedPartial: true })
                throw new InvalidDataException("Invalid published-record inventory in migration receipt.");
            var remainingBudget = options.MaximumRevisionRepairsPerAttempt;
            foreach (var key in legacyKeys.Order(StringComparer.Ordinal))
            {
                var original = await stageSource.ReadAsync(key, options.MaximumRecordBytes, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Source record disappeared while repairing revisions.");
                if (fingerprint.Compute(Convert(original, target.ContractVersion)) != manifest[key])
                    throw new InvalidDataException("Source changed while repairing revisions.");
                if (!revisions.ContainsKey(key) && remainingBudget-- > 0)
                {
                    var assigned = await revisionRepair!.AssignAsync(original, floor, cancellationToken).ConfigureAwait(false);
                    if (assigned is { } value)
                    {
                        if (value <= floor || value > long.MaxValue - 1_048_576)
                            throw new InvalidDataException("Revision repair must allocate a usable timestamp above the observed floor.");
                        revisions.Add(key, value); floor = value;
                    }
                }
                if (revisions.TryGetValue(key, out var repaired))
                    manifest[key] = fingerprint.Compute(Convert(original with { Revision = repaired }, target.ContractVersion));
                else manifest.Remove(key);
            }
            var pending = legacyKeys.Where(key => !revisions.ContainsKey(key)).Order(StringComparer.Ordinal).ToArray();
            Report(StorageMigrationPhase.Running, total, published.Count, pending.Length);
            if (options.IncompleteBehavior == IncompleteMigrationBehavior.ContinueWithAvailableRecords)
            {
                _reservations = target.GetService(typeof(IStorageMigrationWriteReservations)) as IStorageMigrationWriteReservations
                    ?? throw new NotSupportedException("Partial availability requires write reservations. Register the logical migration through AddStorageMigration.");
                // Application writes are excluded by _gate throughout retry/import/publication.
                _reservations.SetPendingKeys(pending);
            }

            var resuming = state is not null;
            state ??= new StorageMigrationState(plan, sourceDigest, total, floor, 0, false)
            {
                SourceVersion = stageSource.ContractVersion, TargetVersion = target.ContractVersion,
                SourceIsDestination = sourceIsDestination, SourceInstance = sourceInstance,
                DestinationInstance = await target.Binding.ProposeAsync(plan, cancellationToken).ConfigureAwait(false),
                TransformIdentity = transform.Identity
            };
            state = state with { RepairedRevisions = revisions, PendingKeys = pending, RevisionFloor = floor, SourceRevisionFloor = sourceFloor,
                MigrationId = options.MigrationId, CleanupRequired = inheritedCleanup?.CleanupRequired == true || cleanup is not null, CleanupRecords = cleanupRecords };
            await stateStore.ValidateCapacityAsync(state, cancellationToken).ConfigureAwait(false);
            // Reserve the largest publication/checkpoint for this attempt as well. A partial
            // handover must not import successfully and then discover that its ownership map cannot fit.
            await stateStore.ValidateCapacityAsync(state with { VerifiedRecords = total,
                PublishedKeys = pending.Length > 0 && options.IncompleteBehavior == IncompleteMigrationBehavior.ContinueWithAvailableRecords
                    ? manifest.Keys.ToArray() : state.PublishedKeys }, cancellationToken).ConfigureAwait(false);
            _requiresFreshProviders = true;
            if (resuming)
                await target.Binding.ValidateAsync(plan, state.DestinationInstance!, cancellationToken).ConfigureAwait(false);
            await target.InitializeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var existing = await VerifyExistingAsync(target, manifest, resuming, cancellationToken,
                published, state.PublishedPartial, pending).ConfigureAwait(false);
            // Capacity is proven before creating a binding, seeding the clock or importing.
            await target.Binding.BindAsync(plan, state.DestinationInstance!, cancellationToken).ConfigureAwait(false);
            await target.PrepareDestinationAsync(floor, cancellationToken).ConfigureAwait(false);
            // Assignments are durable before the first import using them. A retry reuses them.
            await stateStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);

            var verified = 0;
            foreach (var (key, expected) in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existing.Contains(key) && !published.Contains(key))
                {
                    var original = await stageSource.ReadAsync(key, options.MaximumRecordBytes, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Source record disappeared while migrating.");
                    var converted = Convert(revisions.TryGetValue(key, out var repaired) ? original with { Revision = repaired } : original, target.ContractVersion);
                    if (fingerprint.Compute(converted) != expected)
                        throw new InvalidDataException("Source changed while migrating.");
                    await target.ImportAsync(converted, cancellationToken).ConfigureAwait(false);
                    var stored = await target.ReadAsync(key, options.MaximumRecordBytes, cancellationToken).ConfigureAwait(false);
                    if (stored is null || fingerprint.Compute(stored) != expected)
                        throw new InvalidDataException("Imported record failed read-back verification.");
                }
                verified++;
                // The assignment/ownership journal was committed before import. Rewriting a
                // potentially large journal per checkpoint is unnecessary: resume verifies actual commits.
                if (verified % options.CheckpointEveryRecords == 0 && revisions.Count == 0 && !state.PublishedPartial && !state.CleanupRequired)
                    await stateStore.WriteAsync(state with { VerifiedRecords = verified }, cancellationToken).ConfigureAwait(false);
            }
            var finalKeys = await VerifyExistingAsync(target, manifest, allowExisting: true, cancellationToken,
                published, state.PublishedPartial, pending).ConfigureAwait(false);
            if (manifest.Keys.Any(key => !published.Contains(key) && !finalKeys.Contains(key)))
                throw new InvalidDataException("Destination is missing migrated records.");
            await target.VerifyDestinationAsync(cancellationToken).ConfigureAwait(false);
            // Initialize indexes before publishing the completion receipt or returning the repository.
            await target.Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var publishPartial = pending.Length > 0 && options.IncompleteBehavior == IncompleteMigrationBehavior.ContinueWithAvailableRecords;
            state = state with { VerifiedRecords = verified, Completed = pending.Length == 0,
                PublishedPartial = state.PublishedPartial || publishPartial,
                PublishedKeys = publishPartial ? manifest.Keys.ToArray() : state.PublishedKeys };
            if (state.Completed)
                state = state with { RepairedRevisions = new(StringComparer.Ordinal), PublishedKeys = [], PublishedPartial = false };
            await stateStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);
            _reservations?.SetPendingKeys(pending);
            Report(state.Completed ? StorageMigrationPhase.Complete : StorageMigrationPhase.Incomplete,
                total, verified, pending.Length);
            return state;
    }

    private StorageMigrationRecord<T> Convert(StorageMigrationRecord<T> original, string targetVersion)
        => original with { Version = targetVersion, Materialize = () =>
        {
            var converted = transform.Transform(original.Materialize(), original.Version, targetVersion);
            if (converted is null || converted.Envelope is not null)
                throw new InvalidDataException("A storage migration transform must return a detached contract instance.");
            return converted;
        }};

    private async ValueTask<HashSet<string>> VerifyExistingAsync(IStorageMigrationDestination<T> target, SortedDictionary<string, string> manifest,
        bool allowExisting, CancellationToken cancellationToken, HashSet<string>? published = null,
        bool allowApplicationChanges = false, string[]? pending = null)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pendingKeys = new HashSet<string>(pending ?? [], StringComparer.Ordinal);
        await foreach (var record in target.ReadAllAsync(options.MaximumRecordBytes, cancellationToken).ConfigureAwait(false))
        {
            if (!allowExisting) throw new InvalidOperationException("A new storage migration requires an empty destination.");
            if (!keys.Add(record.Key)) throw new InvalidDataException("Destination contains duplicate physical keys.");
            if (allowApplicationChanges && (published!.Contains(record.Key) || !manifest.ContainsKey(record.Key)))
            {
                if (pendingKeys.Contains(record.Key) || record.Revision <= 0)
                    throw new InvalidDataException("Destination conflicts with a pending migration record.");
                continue; // Includes additions and edits made after a durable partial publication.
            }
            if (!manifest.TryGetValue(record.Key, out var expected) || fingerprint.Compute(record) != expected)
                throw new InvalidDataException("Destination conflicts with the migration manifest; nothing will be overwritten.");
        }
        return keys;
    }

    private bool ValidateAndSelectSource()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MigrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(transform.Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint.Identity);
        if (string.IsNullOrWhiteSpace(source.Identity) || source.Identity == destination.Identity)
            throw new ArgumentException("Source and destination require distinct stable identities.");
        ValidateLocations(source, destination);
        if (options.MaximumRecords <= 0 || options.MaximumRecordBytes <= 0 || options.CheckpointEveryRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(options.Trigger)) throw new ArgumentOutOfRangeException(nameof(options.Trigger));
        if (!Enum.IsDefined(options.LegacyRevisions) || !Enum.IsDefined(options.IncompleteBehavior)
            || options.MaximumRevisionRepairsPerAttempt <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.LegacyRevisions == LegacyRevisionHandling.RepairOnStartup && revisionRepair is null)
            throw new ArgumentException("RepairOnStartup requires an IStorageMigrationLegacyRevisionRepair implementation.");
        if (options.CleanupOriginalSourceAfterCompletion && !options.RetireAfterCompletion)
            throw new ArgumentException("Source cleanup requires completion retirement.");
        if (options.Trigger == StorageMigrationTrigger.Explicit
            && options.SourceMaxVersion is null && options.DestinationMaxVersion is null
            && !string.IsNullOrWhiteSpace(options.RunningVersion)
            && string.Equals(options.RunningVersion, source.ContractVersion, StringComparison.Ordinal)
            && string.Equals(options.RunningVersion, destination.ContractVersion, StringComparison.Ordinal))
            return false;

        var running = StorageMigrationVersions.ParseOrdered(options.RunningVersion, "Running version");
        var sourceVersion = StorageMigrationVersions.ParseOrdered(source.ContractVersion, "Source version");
        var targetVersion = StorageMigrationVersions.ParseOrdered(destination.ContractVersion, "Destination version");
        var maximum = options.SourceMaxVersion is null ? null : StorageMigrationVersions.ParseOrdered(options.SourceMaxVersion, "Source version ceiling");
        var targetMaximum = options.DestinationMaxVersion is null ? null : StorageMigrationVersions.ParseOrdered(options.DestinationMaxVersion, "Destination version ceiling");
        if (maximum is not null && sourceVersion > maximum || targetMaximum is not null && targetVersion > targetMaximum)
            throw new ArgumentException("A provider's configured version exceeds its maximum.");
        var useSource = options.Trigger == StorageMigrationTrigger.ContractVersion && (maximum is null || running <= maximum);
        if (useSource && running != sourceVersion || !useSource && running != targetVersion)
            throw new InvalidOperationException("The selected provider must be configured for the requested running version.");
        if (!useSource && targetMaximum is not null && running > targetMaximum)
            throw new InvalidOperationException("No storage provider is configured for the requested version.");
        if (options.Trigger == StorageMigrationTrigger.ContractVersion && maximum is not null && targetVersion <= maximum)
            throw new ArgumentException("Source and destination version ranges overlap.");
        return useSource;
    }

    private static string Digest(string value) => ConvertHash(Encoding.UTF8.GetBytes(value));
    private static string ConvertHash(byte[] value) => System.Convert.ToHexString(SHA256.HashData(value));

    private void ValidateLocations(IStorageMigrationEndpoint<T> first, IStorageMigrationEndpoint<T> second)
    {
        if (first.StorageDirectory is { } a && second.StorageDirectory is { } b) StorageMigrationPaths.RequireDisjoint(a, b);
        if (stateStore.StoragePath is not { } receipt) return;
        StorageMigrationPaths.Validate(receipt);
        if (first.StorageDirectory is { } sourcePath) StorageMigrationPaths.RequireDisjoint(sourcePath, receipt);
        if (second.StorageDirectory is { } destinationPath) StorageMigrationPaths.RequireDisjoint(destinationPath, receipt);
    }
}
