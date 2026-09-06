# Optional compaction maintenance controls

The local maintenance building blocks are opt-in and belong to the free core. They are separate from repository Created/Changed/Deleted notifications and from write-conflict strategies. Constructing a policy or scheduler does not start a worker, timer, filesystem scan or compaction.

## Current integration boundary

`CompactionPolicy`, `CompactionCandidateSelector`, `MaintenanceBudget`, `MaintenanceActivityHints`, `CompactionScheduler` and `MaintenanceNotificationHub` are available as independently tested building blocks. Segment Core also exposes the optional `ICompactionMaintenance<TContract>` capability through keyed and default DI aliases. File-per-object storage has no segment-compaction equivalent; it does not invent file rewriting to implement this interface.

Default Segment Core compaction remains `SlicedAppend`, with its existing sliced mutation pauses. Explicit `IndependentOutput` writes separate immutable cleanup files while application writers continue using their normal logs. Physical-address comparison prevents an old cleanup copy from replacing a newer committed save, metadata write, delete or recreated entity. Short snapshot/publication/checkpoint/retirement pauses remain; moving work to a background task is not a promise of zero latency.

Both compaction modes require `SegmentCoreTransactionProtocol.MutationWriteAheadLog`. The repository provider selects that protocol; callers constructing raw `SegmentCoreOptions` must select it explicitly. The raw store's legacy protocol default does not support online compaction.

## Segment Core configuration

```csharp
var hints = new MaintenanceActivityHints { Activity = MaintenanceActivity.Idle };
var budget = new MaintenanceBudget(new()
{
    MaximumConcurrentUnits = 2, BytesPerSecond = 32L * 1024 * 1024,
    MaximumWorkUnitBytes = 128 * 1024
});
var providerOptions = new SegmentCoreRepositoryOptions
{
    CompactionOptions = new()
    {
        Mode = SegmentCoreCompactionMode.IndependentOutput,
        MaintenanceBudget = budget, MaximumCopyConcurrency = 2,
        Selection = new() { MaximumCandidates = 8, MaximumCopyBytes = 64L * 1024 * 1024 }
    },
    CompactionSchedule = new()
    {
        ActivitySource = hints,
        Policy = new CompactionPolicy(new() { Mode = CompactionMode.IdleIncremental })
    }
};
// Supply providerOptions to AddSegmentCoreRepository<TContract>.
var maintenance = services.GetRequiredKeyedService<ICompactionMaintenance<TContract>>(databaseKey);
using var subscription = maintenance.SubscribeMaintenance((summary, lifetime) => ValueTask.CompletedTask);
var estimate = await maintenance.EstimateCompactionAsync(stoppingToken);
var outcome = await maintenance.RunCompactionAsync(manual: true, stoppingToken);
```

Use `Idb.Storage.SegmentCore` for provider types and `Microsoft.Extensions.DependencyInjection` for resolution. `services` above is the built `IServiceProvider`. Reuse a budget object across stores on the same volume when they should share limits. The common facade is non-owning; dispose the service provider once, not each alias.

`CompactionSchedule = null` keeps legacy automatic interval/mutation-count admission. A non-null schedule uses policy admission at the existing `CheckpointInterval` polling cadence, so a low-traffic store does not need to accumulate the legacy minimum mutation count. Checkpoint maintenance remains independent. `EnableBackgroundMaintenance = false` leaves polling to the application. Merely subscribing does not replace legacy automatic scheduling. Existing `ISegmentCoreRepositoryMaintenance<T>.CompactAsync` remains an unconditional manual operation and returns its full native result; if subscribers exist, it produces one summary. Scheduled requests coalesce; concurrent direct/manual calls retain engine serialization. No extra hooks, counters or scheduler calls are added to ordinary saves.

Estimates examine bounded index/file metadata, including obsolete bytes in active logs that execution can seal. They do not rotate files, upgrade storage format, or deserialize objects. They are advisory, not a consistent snapshot of ongoing writes. Load admission combines explicit activity/window hints with a sample of outstanding log writes, not all application demand. Independent selection uses sealed files and counts protected obsolete bytes as copy cost, not reclaimable space. Metadata and whole-candidate copy limits can prevent an otherwise desirable cleanup; raise limits deliberately, rather than expecting an oversized file to be partially rewritten.

Independent copying charges encoded record reads/writes and retained-history relocation in bounded I/O units outside mutation locks. Checkpoint/manifest publication, source rotation, directory metadata and file retirement are not byte-rate-controlled; they still cause short durability pauses and I/O. The budget is rejected for `SlicedAppend`, where throttling the copy loop under mutation exclusion would misrepresent concurrency. `BytesCopied` counts copied current frame bytes; `RetainedBytesCopied` reports history payload copying separately. `RetiredFileBytes` is gross deleted-file size. `BytesReclaimed` is observed net store-size reduction, clamped at zero; it includes checkpoint/history changes and competing foreground growth, so it is not gross retired bytes. Failure to delete files is counted and reports a partial result.

### Storage compatibility and recovery

Take and verify a backup before enabling independent output. Default stores keep engine format 1.0. The first explicit independent compaction durably upgrades the mandatory storage manifest to engine 1.1, even if later work is cancelled or no candidate qualifies. Older runtimes reject this format before mutation. This is not automatically downgraded by selecting `SlicedAppend` later.

Independent frames retain original encoded bytes and log sequence numbers, live outside ordinary replay, and become current only through a checked locator publication. Two full checkpoints protect retirement. A durable recovery floor prevents falling back to a pre-retirement checkpoint. Loss of every safe checkpoint fails closed instead of returning an incomplete normal-log reconstruction; restore a verified backup in that case. An initial normal-log checkpoint is published before the first independent file, so an interrupted first compaction has a safe recovery base. Failed attempts may leave durable orphan output files for a later cleanup; do not delete them speculatively because a checkpoint may already reference them. Online backup/restore includes referenced independent files and the recovery floor.

Windows tests cover concurrent saves/deletes/recreates/transactions, retained baselines, partial selection, corrupt source records, cancellation, checkpoint fallback and backup/restore. Separate process-kill probes cover publication/retirement boundaries and old-binary rejection. Before/after unused-feature, active cleanup, subscribers, retention, I/O limits and sustained-writer measurements have also been completed locally. Foreground responsiveness can improve while total cleanup time or tail latency worsens; qualify your own workload. These checks are not physical power-loss or mobile-device certification, and no zero-overhead claim is made.

## Policy and activity

`ICompactionPolicy.Evaluate` receives conservative physical/reclaimable byte estimates, elapsed times, foreground queue depth, activity/window hints and working-space admission. Invoke it outside engine locks, not once per entity save. Retention, reader safety, recovery and final disk-space checks remain the provider's authority.

| Mode | Behavior |
| --- | --- |
| ThroughputFirst | Wait for useful space recovery, configured intervals and acceptable foreground demand. |
| IdleIncremental | Additionally require explicit idle or window hints; unknown activity is not idle. The engine must actually support bounded/yielding work. |
| SpaceFirst | Prefer eligible cleanup even while busy, accepting I/O contention, but still obey benefit, interval and safety requirements. |
| ManualWindowed | Run automatically only inside an explicit maintenance window; a manual request bypasses scheduling thresholds. |

`AllowBusyAfterMaximumDeferral` is an explicit anti-starvation option. It does not override insufficient benefit, working space or retention safety. `CompactionScheduleOptions.MinimumProbeInterval` also limits expensive probes during repeated deferral/failure. Manual requests bypass that probe limit. Concurrent scheduler requests coalesce with `AlreadyRunning`, rather than queueing duplicate whole-store cleanups.

`CompactionCandidateSelector` operates only on bounded metadata. It excludes active/unsealed candidates and protected obsolete bytes from predicted reclamation, sorts by benefit per copied byte, and respects candidate/copy limits. Selection does not retire a file; the engine must revalidate physical identities and checkpoint/reader/retention requirements. Oversized candidates are skipped rather than secretly exceeding the budget.

## Example: application-owned orchestration

These delegates are integration points, not existing repository method names:

```csharp
var hints = new MaintenanceActivityHints { Activity = MaintenanceActivity.Idle };
using var scheduler = new CompactionScheduler(
    probe: ProbeProviderMetadataAsync,
    execute: CompactProviderAndReturnConfirmedCountsAsync,
    options: new CompactionScheduleOptions
    {
        ActivitySource = hints,
        Policy = new CompactionPolicy(new()
        {
            Mode = CompactionMode.IdleIncremental,
            MinimumReclaimableBytes = 4L * 1024 * 1024
        })
    });
using var subscription = scheduler.SubscribeMaintenance((summary, lifetime) =>
{
    // Log counters or update application UI. No entity has been changed by relocation.
    return ValueTask.CompletedTask;
});

// An existing application/provider maintenance loop owns polling and cancellation.
var result = await scheduler.RunAsync(cancellationToken: stoppingToken);
```

Use namespaces `Idb.Libraries.Abstractions.Options`, `Idb.Libraries.Abstractions.Services.Maintenance` and `Idb.Libraries.Implementation.Maintenance`. Probe signatures return `ValueTask<CompactionEstimate>`; execution returns `ValueTask<CompactionWorkResult>`, both accepting `CancellationToken`. Probe without materializing domain objects. Count only confirmed reclamation in the execution result. Both delegates must return with their engine locks released. Do not invoke the scheduler while holding those locks.

The scheduler rethrows probe/engine errors to its caller and emits a bounded failure code, not exception text. A successful engine result is not turned into cancellation just because the cancellation token became signalled after commit. Disposal requests cancellation and detaches subscribers; `DisposeAsync` additionally drains admitted engine operations before the owning provider destroys the store. It does not wait for uncooperative user callbacks. Arbitrary delegates, policies and cancellation callbacks are trusted application code; cooperative cancellation is not a hard time limit.

If an execution adapter returns invalid summary counters, the scheduler suppresses that notification and increments `ObservationFailureCount`; it does not turn an already completed operation into an exception. The adapter's original result remains visible to the caller for diagnosis.

`CompactionRunResult.Decision` describes scheduling admission, not guaranteed publication. An admitted engine may still return `Work.Deferred = true` with a bounded `ReasonCode`, for example if its final cost or contention checks reject publication. That produces `CompactionDeferred`, can include bytes spent on discarded copying, and does not reset the scheduler's last-success timestamp. A partial completed batch also does not mean every obsolete byte in the store was reclaimed.

## Shared maintenance I/O budget

Share an `IMaintenanceBudget` instance across participating stores on the same device. `MaintenanceBudget` bounds active work units and optionally their aggregate byte rate. Charge read plus write bytes, not just final reclaimed bytes. `MaximumWorkUnitBytes` is also the token-bucket burst limit; split larger work before admission. Acquire outside storage locks and release with `using` after that bounded work is finished.

```csharp
var budget = new MaintenanceBudget(new()
{
    MaximumConcurrentUnits = 2,
    BytesPerSecond = 32L * 1024 * 1024,
    MaximumWorkUnitBytes = 4L * 1024 * 1024
});
using (await budget.AcquireAsync(estimatedReadAndWriteBytes, stoppingToken))
{
    await CopyOneBoundedProviderUnitAsync(stoppingToken);
}
```

This is maintenance-only admission, not an OS-wide limiter or a guarantee of disk latency. It does not throttle unrelated processes or foreground writes. The current scheduler does not invent byte charges for its execution delegate: the provider copy loop must explicitly use the budget. Cancellation returns admission slots; disposing a lease twice is safe. Statistics are approximate concurrent observations, not a transactional snapshot.

## Maintenance summaries and lifetime

- Summaries are `CompactionCompleted`, `CompactionDeferred`, `CompactionFailed` or `CompactionCancelled`.
- An engine may defer after a bounded attempt without publishing replacement storage. `CompactionWorkResult.Deferred` and `ReasonCode` preserve that distinction; a deferred summary may still show copy costs for discarded work. A positive scheduling decision is permission to attempt, not proof of publication.
- They contain operation identity and bounded counters, not domain objects, secrets, arbitrary paths or huge affected-key lists.
- Registered handlers run asynchronously, outside engine and subscription locks. Each subscription has a bounded queue and the hub bounds concurrent callbacks.
- Disposal/cancellation detaches a subscriber. Callback failure or queue overflow also detaches it, with an explicit `Completion` reason. Completion does not force an admitted callback to stop; it receives cancellation cooperatively.
- Maintenance caused by a subscriber does not loop back to that subscriber. Causal depth is bounded. Do not await `WaitForIdleAsync` from any notification handler.
- These are best-effort local observations, not durable replay. Physical references and edit validity remain correct even if every notification is missed.
- Empty hubs allocate no event payload or worker per publication. The actual end-to-end cost still requires provider benchmarks; component microbenchmarks are not storage throughput results.

Baseline-expiry advice and persistent editing across restart are not added by these controls. Existing finite edit-session validation remains authoritative.
