# Local-first and remote-first repositories

Available under MIT in `InterfaceDB.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

Applications consume one `IRepository<T>` backed by a dedicated local repository and a remote
repository. TCP supplies a remote endpoint; the composition and policy live in InterfaceDB.

## Per-repository behavior

| Setting | Read | Write | Remote changes |
| --- | --- | --- | --- |
| `LocalFirst` | Return matching local records; fetch remote on a miss or explicit invalidation. | Commit locally with a durable pending marker, then synchronize automatically. Works offline. | Refresh the local cache unless an unsynchronized local edit conflicts. |
| `RemoteFirst` | Fetch remote state and refresh the cache on each read. Use an eligible cache during a classified transport outage. | Commit remotely while online; notifications refresh the cache. During confirmed downtime, commit locally with a durable queue until recovery. | Refresh the cache automatically while initialized. |

Each keyed registration has its own copied options. Two repositories of the same contract can
have different policies and separate local stores. `ReadPreference` selects the complete behavior
above and supersedes the legacy `RevisionCheckMode`/`MutationAuthority` settings. Leave it null only
for the older preferred/peer behavior described in the retained preferred/peer APIs in source.

Local-first broad queries return matching cached records when any exist; they do not perform a
remote union on every hit. Automatic refresh eventually adds remote-only records. Remote-first
queries reconcile bounded remote state (incremental when a valid cursor is available) before applying the predicate, so records moved
out of a predicate or deleted remotely do not survive as stale query matches.

## Registration

Register the local provider and remote provider under distinct keys, then compose them:

```csharp
services.AddLocalRemoteRepository(
    repositoryKey: "family",
    localRepositoryKey: "family-local",
    remoteRepositoryKey: "family-remote",
    mapper: familyMapper,
    options: new ResilientRepositoryOptions<IFamily>
    {
        DatasetId = "family-v1",
        CacheScope = accountAndDatasetIdentity,
        ReadPreference = RepositoryReadPreference.LocalFirst,
        BackgroundRetryDelay = TimeSpan.FromSeconds(5),
        IsUnavailableException = TcpRepositoryAvailability.IsUnavailable,
        IsEndpointWideFailureException = TcpRepositoryAvailability.IsUnavailable,
        ValidateAccessAsync = validateApplicationSession
    });
```

Choose `RepositoryReadPreference.RemoteFirst` for another registration to make its remote endpoint
authoritative. Resolve `IRepository<IFamily>` or the keyed repository as usual. Resolve
`IResilientRepository<IFamily>` for explicit synchronization, change hints and conflict resolution.
Call `InitializeAsync` at session startup; public reads/writes also initialize automatically.

The compiled [ExampleComposition](../samples/InterfaceDB.LocalRemote.Client/ExampleComposition.cs)
shows the complete SegmentCore + TCP registration with a sample contract, mapper and local config.
The existing TCP client example supplies TLS and signed-in JWT configuration.

The mapper must return a stable logical key, deep-copy domain values and leave copied envelopes
null. Keys are unique within a dataset. Composition writes new records at a deterministic key
with an expected-missing condition; existing physical keys are retained. Cache imports go through
the ordinary InterfaceDB repository and therefore update its indexes.

## Automatic subscriptions and reconnects

Each initialized composition with `EnableBackgroundSynchronization = true` owns disposable
subscriptions. Enable `StoragePolicy.RepositoryNotifications = new RepositoryNotificationOptions()`
on its dedicated local provider and on any in-process File/SegmentCore peer. The adapter uses the
existing Created/Changed/Deleted API. A TCP peer supplies `IRepositoryChangeNotifications`.

TCP registers a separate authenticated listener connection. It does not acquire the store's
exclusive operation lease. The host publishes account-scoped hints after storage mutations settle,
including uncertain mutation outcomes. Subscription registration is acknowledged before catch-up;
the local API's `Ready` barrier also waits for writes admitted before registration. A bounded reconciliation runs on initial registration, pushed changes, local work and reconnection.

There are no periodic storage checks. `BackgroundRetryDelay` applies only to failed work or lost
subscriptions. TCP keepalive detects silently broken connections without querying storage. Token
expiry closes the listener; reconnection acquires a fresh token and validates it again. Authorization
is rechecked before each delivery. Permanent authentication/protocol failures surface through
`FailureHandler` and require repairing the session.

Subscribe to logical cache changes using the disposable API:

```csharp
await using var subscription = composed.SubscribeChanges(async () =>
{
    var current = await composed.QueryFirstAsync(_ => true);
    await UpdateViewAsync(current);
}, sessionCancellation);
await subscription.Ready;
```

Callbacks run outside composition locks. Marshal UI updates as needed. A subscription keeps one
pending coalesced hint, so a slow consumer cannot retain an unbounded event queue. `Completion`
reports termination or callback/observation failure. `Dispose` detaches; `DisposeAsync` also drains
the adapter's callback. Cancellation, repository disposal, host shutdown, token expiry and permission
revocation release registrations on the applicable side. A callback may dispose its own handle.

Change callbacks may read their source composition and update a view. Saving, deleting (including
batch saves), or resolving conflicts on that same composition from the callback's async context
throws `RepositoryFeedbackLoopException` before storage I/O and reports it through `FailureHandler`.
An uncaught callback exception detaches that subscription and faults `Completion`. Do not reconnect
a faulty callback automatically; fix it first. Issue writes from explicit application commands.

Hints request current state; they do not retain every intermediate event. Revision checks, pending
mutation IDs and local write scopes suppress replication feedback. Durable queued edits and full
reconciliation repair missed hints. Existing `NotifyChangedAsync` accepts an external trusted hint.
The legacy durable event-log `LastAppliedCheckpoint` is not advanced by snapshot reconciliation.

## Writes, failures and conflicts

- Local-first writes preserve the remote base revision inside the same durable envelope as the
  pending payload. Multiple offline edits coalesce to the latest state per key.
- The server compares that original base when accepting a queued write. A concurrent remote change
  produces a conflict and retains the local edit; neither side is silently overwritten.
- The remote copy records the mutation ID. A lost acknowledgement is reconciled by reading that ID;
  an already applied mutation is not committed again.
- Normal acknowledged delivery changes local metadata without changing the local edit revision
  or publishing another logical application change.
  Recovery after an uncertain commit imports the latest remote payload, including later edits. Ordinary new
  edits and imported remote changes still use strict revision checks.
- Remote-first writes are successful once acknowledged remotely. The command does not directly
  update the cache or publish a cache-change callback. A pushed hint triggers reconciliation;
  remote-first reads and explicit `SynchronizeAsync` also reconcile.
- From remote write dispatch until successful refresh, the running instance disables cached
  offline fallback. This includes uncertain outcomes such as a lost acknowledgement. A cache
  import failure is reported through `FailureHandler`; it never replays the remote command.
  With background synchronization disabled, a read or explicit synchronization must refresh the cache.
- Authentication, identity, protocol and corruption errors are not classified as offline conditions
  by `TcpRepositoryAvailability`. Caller cancellation is propagated.
- Composed deletes are durable tombstones; subscribers filter them from results. A later creation
  can reuse the logical key with a conditional write. Physical remote deletion is also reconciled
  after a complete successful scan. Failed or partial scans cannot imply deletion.
- `SaveManyAsync` performs bounded sequential saves; it is not a cross-record transaction.

Inspect `SynchronizeAsync()` for commits, acknowledgements, refreshes, conflicts and failures.
Configure `FailureHandler` for background diagnostics. Resolve a conflict explicitly:

```csharp
await composed.ResolveConflictAsync(
    storageKey, pendingEntity.Envelope!.RevisionTimestampUtcMicroseconds,
    RepositoryConflictResolution.AcceptRemote); // discards this pending local edit
// Or KeepLocal: requeue against the remote revision observed during resolution.
```

The expected local revision prevents discarding a newer local edit. `KeepLocal` can conflict again
if the remote changes before delivery. Automatic domain merging is not performed.

## Feedback prevention and contingencies

```text
RemoteFirst: application command -> remote commit -> pushed hint -> local import -> stop
LocalFirst:  application command -> local pending commit -> remote commit -> acknowledge -> stop
Other remote edits -> pushed hint -> local import (or retained local conflict) -> stop
```

The command return and notification are asynchronous with respect to one another. Use the returned
entity for immediate command confirmation; a logical change notification means the local refresh
has occurred. Observe before taking an initial snapshot to avoid missing an intervening refresh.

| Condition | Contingency |
| --- | --- |
| Remote import triggers local pub/sub | Imports carry local subscription write scopes and never create pending mutations. |
| Duplicate or coalesced hints | Compare remote revisions before importing; unchanged snapshots do not write or publish. |
| Ordinary delivery acknowledgement | Clear delivery metadata without publishing another application edit. |
| Lost acknowledgement | Local-first recognizes its durable mutation ID; remote-first observes the remote state without replaying the command. |
| Disconnect or missed hint | Re-register before taking a complete catch-up snapshot. Retry failed work; do not poll healthy storage. |
| Slow subscriber | Retain one pending hint per subscription, not an unbounded queue. |
| Callback writes back into its source | Reject the command before I/O; detach an uncaught faulty callback and expose its failure. |
| Concurrent local and remote edits | Preserve the pending local edit and report a revision conflict for explicit resolution. |

For application integration, monitor `FailureHandler`, subscription `Completion`, and synchronization
conflicts. Present unresolved conflicts and a disconnected/synchronizing state to the user. Keep one
composition owner per physical cache and route all domain writes through it. Direct writes to its
private cache bypass the composition's metadata and are unsupported.

The callback guard covers the same composition in the propagated async execution context.
Application workflows that relay writes between different repositories, or deliberately start a new
execution context, need their own command identity/deduplication and explicit termination rule.
Treat notifications as observations rather than unconditional commands to save again.

## Cache ownership and lifecycle

Route application writes through the composed repository; its physical cache is an implementation detail.
Use one composition owner per local store, and a dedicated cache directory per account, dataset and
contract. `CacheScope` is persisted and checked. TCP additionally binds cache metadata to a digest
of the endpoint, alias, contract baseline, JWT issuer and subject. Token refresh for the same
identity is supported; switching accounts requires a new composition and that account's cache.
This client identity check is not JWT authorization; the remote host independently validates tokens.

Use `ValidateAccessAsync` for application session and offline access rules. Dispose the composition
on logout before disposing its borrowed endpoints, or dispose their owning DI provider. Disposing
the composition stops synchronization and does not erase the cache. Family/child/device permissions,
login flows and deciding which cached data a signed-out child may use remain application policy. A dedicated signed-out, device-authorized detached snapshot API is not implemented; do not convert authentication failures into offline fallback. See [preview qualifications](preview17-release-notes.md#qualification-and-future-work).

Start these modes with a fresh dedicated cache. Existing raw app caches and legacy resilient
metadata are rejected rather than silently assigned an account or guessed remote base. Preserve
old data and perform an explicit application migration if needed. Pending edits remain queued when switching to remote-first; it resumes remote authority after successful delivery.

## Bounds and current scope

`MaximumSynchronizationScanItems` bounds local/remote composition scans and input batches. TCP also
enforces its byte, record and wire limits. Operations within a composition are serialized; a foreground call may wait for an in-flight background request.
Local recovery still scans bounded datasets; remote catch-up uses durable account-scoped cursors when available and a full bounded snapshot after retention expiry. Non-isolated aliases allow one active storage session; subject-isolated JWT aliases allow one per validated issuer/subject, alongside independent notification listeners. Tombstone cleanup, large-dataset query/paging work, delivery of every intermediate event and production/device capacity qualification remain separate roadmap work.

## Managed versions and incremental recovery

The [server-managed setup guide](server-managed-setups.md) supersedes earlier
remote-first/offline and full-snapshot-only limitations: both modes now durably queue offline
edits, client workers re-register subscriptions, and reconciliation uses account-scoped durable
cursors with a full snapshot when retention expires. Use `LocalRemoteRepositoryFactory` with a server-approved version plan for contract selection and cache migration.
