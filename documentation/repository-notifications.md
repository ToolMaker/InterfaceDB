# Repository notifications

Local repository notifications are part of free, MIT-licensed InterfaceDB. They are opt-in,
in-process observations of writes through one repository owner, not a durable event bus.
This feature is included in preview.7 and later. Preview.8 expands its public package documentation.

## Enable and subscribe

Use the same contract configuration and DI database key for persistence and notifications:

```csharp
var policy = StoragePolicy.Default with
{
    RepositoryNotifications = new RepositoryNotificationOptions(),
    WatchStorageChanges = false
};
services.AddFileRepository(contractConfig, storagePolicy: policy);
// Or AddSegmentCoreRepository with policy on the contract configuration.

var repository = provider.GetRequiredKeyedService<IRepository<IScore>>(databaseKey);
var notifications = provider.GetRequiredKeyedService<IRepositoryNotifications<IScore>>(databaseKey);

using var created = notifications.SubscribeCreated(score =>
{
    // score is an isolated committed entity with its own attached envelope.
    Console.WriteLine(score.Points);
    return ValueTask.CompletedTask;
});

using var changed = notifications.SubscribeChanged(change =>
{
    Console.WriteLine(string.Join(", ", change.ChangedProperties));
    return ValueTask.CompletedTask;
}, score => score.Points > 0, lifetime: applicationLifetime);

using var deleted = notifications.SubscribeDeleted(score =>
{
    // Last committed values; score.Envelope is null.
    return ValueTask.CompletedTask;
});
```

Namespaces: `Idb.Libraries.Abstractions.Options`,
`Idb.Libraries.Abstractions.Services.Repository`,
`Idb.Libraries.Implementation.Extensions`; Segment Core registration uses `Idb.Storage.SegmentCore`.
The example assumes your existing `IScore`, contract configuration, database key and application
cancellation token. The unkeyed notification alias selects the same owner as unkeyed `IRepository<T>`.

`StoragePolicy.RepositoryNotifications = null` is the default, disabled path. Registration then
throws an explanatory exception. `RaiseNotifications` continues to control **physical envelope**
notifications independently; enabling repository subscriptions does not require enabling it.

## Event and payload contract

| Event | Payload | Filter uses | When emitted |
| --- | --- | --- | --- |
| Created | Saved entity with envelope | New committed values | New entity successfully stored |
| Changed | Saved entity with envelope and read-only top-level property names | New committed values | Existing entity has actual contract-value differences |
| Deleted | Detached entity, Envelope null | Last committed values | Existing entity successfully deleted |

An insertion does not also emit Changed. Repeated/no-op or metadata-only saves do not emit
Changed. Repeated deletion does not invent a Deleted event. Initial loads and index rebuilding
do not invent Created events. Retrying a fresh detached object as a new insert is still a new
storage operation: notifications do not add an application-level idempotency key.

Each recipient receives an independently materialized snapshot. Its values cannot drift because
another caller edits a cached entity, nor can one subscriber corrupt another's payload. The
event entity need not be reference-equal to the input or returned saved object. Editing it does
not persist anything until an explicit save. Saving an old event snapshot follows normal store
semantics: preview.7 and later reject stale saves through revision-checked writes. Notifications do
not grant an event snapshot permission to overwrite a newer committed revision.

Created/Changed envelopes remain valid for explicit saves. Deleted detachment does not clear
the original caller's envelope or remove internal deletion bookkeeping. Encryption uses the
repository's existing serializer, lifecycle and configured keys. Events expose the values the
local repository can read, so only trusted application code should be given subscriptions.

The original envelope Added/Stored/Deleted/Removed API and payloads are unchanged. Repository
notifications are normalized at the completed repository mutation, not forwarded blindly from
physical events. Index maintenance completes before dispatch. A callback query may see that
commit or a newer one; the callback payload represents the announced commit.

## Preventing notification feedback loops

By default a subscription does **not** receive writes it caused, or their causal descendants.
Repository callbacks are automatically tagged using an in-process async-flow scope. If handler
A saves, B may receive that change; if B then saves in response, that descendant is not delivered
back to A or B. Unrelated subscribers can still receive it. No origin is stored in the entity,
envelope or disk format, and this mechanism does not use mutable object-reference equality.

For a save initiated outside the handler, explicitly identify the initiating subscription:

```csharp
using (changed.BeginWriteScope())
{
    score.Points += 10;
    await repository.SaveAsync(score);
}
```

Ordinary `await` preserves the scope; dispose it lexically, in nesting order, in the originating
async flow. Multiple subscription scopes may be nested when several observers belong to the
same initiating component. Scopes are not transferable handles or authentication credentials.
Without an explicit scope outside callbacks, the repository cannot know which subscriber
initiated a save. Do not suppress execution-context flow when relying on causal tracking.

This is intentional suppression: an initiator will not see descendant corrections caused by
its own write chain. Pass `receiveOwnWrites: true` at registration only when the handler needs
those notifications and is designed to converge. No-op detection still applies. A configurable
maximum causal depth (64 by default) detaches a runaway subscription with
`CausalDepthExceeded`. Remote messages, timers or explicit context suppression can break a
causal chain; they need their own correlation/idempotency strategy and are outside this feature.

Do not synchronously wait for callbacks to drain inside callbacks. Reentrant repository
callbacks may await ordinary saves, queries and deletes. In contrast, reentering an observed
repository mutation from its synchronous **physical envelope** callback is rejected with an
`InvalidOperationException`, preventing a lock cycle; move that work to a repository callback.

## Filters and comparison support

Changed requires a filter; use `_ => true` to request all changes. Created/Deleted filters are
optional. Filters evaluate an individual isolated event entity, not a rerun of a database query.
They are after-state predicates for Changed: changing German to French does not notify a
German-only subscription. Initial result sets, enter/leave tracking, aggregates and replay are
not live-query features provided here.

Expressions compile once (with an interpreter fallback when dynamic code is unavailable).
Use contract members, scalar operators, string operations, Math/MathF and `Enumerable.Contains`.
Envelope access, invoked delegates and unsupported method calls/statement blocks/mutations are
rejected. Predicates must be fast and side-effect-free. This validation is not a security sandbox;
custom operators/getters are still application code. Replace a subscription to change its filter
rather than depending on mutable captured variables.

Changed compares committed snapshots, never two references to an edited cache object. It reports
top-level readable/writable contract properties, including inherited properties, excluding
Envelope and MessagePack IgnoreMember properties. Implementations must persist those contract
values; computed properties/custom serializer transformations require careful schema review.
Runtime implementations that explicitly ignore compared properties are rejected when observed.
Changing only the implementation class, with identical visible contract values, is not Changed.

Default equality supports primitives/enums, nullable scalars, strings, decimal, Guid, DateTime,
DateTimeOffset, TimeSpan, DateOnly and TimeOnly, plus one-dimensional arrays and `List<T>` of
supported values recursively. It uses .NET value equality (ordinal string equality); collections
use element/order equality and byte arrays use span comparison. Report the collection's top-level
name, not element paths. Complex types require a pure, bounded, thread-safe value comparer in
`PropertyComparers[propertyName]`; snapshots still use the store serializer. No old/new value
pairs are exposed publicly, and collision-prone hash-only comparisons are not used.

The writer captures owned encoded before/after snapshots. After commit and index publication,
the notification dispatcher materializes both as detached objects and compares them **once per
event**, sharing the read-only property list across recipients. Subscribers still receive their
own isolated, envelope-attached after object. SaveAsync does not wait for this comparison or
handlers; it continues to wait for the provider's configured commit. A comparison failure ends
observation, not the already committed save. Comparers cannot reenter enabled repository writes.
The dispatcher uses the same process CPU, not a remote subscriber's processing resources.

## Lifetime, errors and resource bounds

`IRepositorySubscription` is disposable and exposes `IsDisposed`, `Completion`, `DeliveredCount`,
`FilteredCount`, `BeginWriteScope()` and `WaitForIdleAsync(token)`. Counts mean successful handler
returns and predicate rejections respectively; intentionally suppressed own writes count as neither.

Disposal/cancellation unregisters and discards queued work. An already-started callback may
finish; it cannot be forcibly terminated. Completion reports detachment, not callback termination.
Use WaitForIdleAsync outside handlers, with cancellation/timeout, if you need to wait. An active
delegate retains its target: dispose subscriptions when a view/service is finished with them.
Optional `keepAlive` runs during dispatch, not on a polling timer, and cannot prove remote liveness.

Callbacks are sequential per subscription, asynchronous to SaveAsync, and outside mutation/index
locks. They do not run on the caller's UI context; marshal UI updates to the application's dispatcher.
Bounded shared concurrency limits simultaneously executing callbacks. One slow callback can
exhaust its own queue; enough permanently stuck callbacks can occupy every callback slot.
Handlers must provide their own bounded/cancellable external operations.

Failure/overflow detaches the affected subscription instead of making a committed save appear to
have failed. Inspect Completion: Disposed, Cancelled, RepositoryDisposed, KeepAliveExpired,
CallbackFailed, QueueOverflow, ObservationFailed or CausalDepthExceeded. Preparation failures or
ambiguous storage exceptions conservatively end affected observation with ObservationFailed;
the original storage return/exception remains authoritative. Requery and resubscribe after a
gap; there is no silent lossy mode and no durable catch-up. Do not log sensitive event values.

| Option | Default |
| --- | ---: |
| QueueCapacity (per subscriber, pending events) | 256 |
| MaximumQueuedBytesPerSubscriber (encoded payload accounting) | 16 MiB |
| MaximumSubscribers (per repository) | 1,024 |
| MaximumConcurrentCallbacks (per repository) | 8 |
| MaximumSnapshotBytes | 4 MiB |
| MaximumCollectionItems (per compared collection) | 65,536 |
| MaximumBaselineEntries / MaximumBaselineBytes | 256 / 16 MiB |
| MaximumCausalDepth | 64 |

Queued Changed candidates charge both encoded snapshots, not just the new one. No-op candidates
can occupy queue slots until background comparison suppresses them; they increment neither
DeliveredCount nor FilteredCount. A burst of unchanged saves can therefore exhaust a stalled
subscriber's queue. WaitForIdleAsync includes comparison, filtering and callback completion.

Snapshot limits bound encoded notification buffers, not arbitrary custom serializer/comparer
allocations. Segment Core's normal read allocation occurs before its notification size check.
An in-flight callback also owns its materialized entity outside pending-queue accounting.
Setting MaximumBaselineEntries to zero disables baseline retention and rereads committed state.
Baselines clear after the last subscriber detaches once in-flight mutations finish.

## Ownership, compatibility and performance

All mutations of an observed entity must pass through its **single owning repository instance**.
No exact history is promised for direct envelope/store writes, other repository owners, external
file writers, watchers, migrations, restores or remote transport adapters. Stop subscriptions
around such operations and requery/re-register. This does not enlarge transaction boundaries or
allow another writer process. File Performance mode remains non-durable even when events are enabled.

Disabled repositories retain their direct path. Enabled zero-subscriber repositories also allow
normal parallel writes, without snapshot, comparison, per-entity lock, cache or dispatch work.
They pay brief admission accounting and async-flow bookkeeping, not a write queue held across IO.
At the first registration or last removal only, newly admitted writes wait for the preceding
phase's in-flight writes. Registration/disposal returns immediately; it does not block waiting
for a writer. Old admitted writes are not retroactively delivered to newly added subscribers.
Baseline caches belong to observation phases and cannot leak stale values across an empty phase.

With subscribers, updates/deletes to different existing physical keys can proceed concurrently;
the same object reference or physical key remains ordered through before/commit/after capture.
Observed inserts with an unassigned identity (including batches containing one) conservatively
hold an exclusive publication barrier, preventing a query-followed-by-update from overtaking
capture of the inserted revision. Existing-key writes use shared publication leases. This is
not a claim of lock-free writes or unlimited parallel insert scaling. Underlying provider locks,
durability and atomic/partial-success batch semantics are unchanged. Observed batches reject
duplicate references/physical keys and acquire overlapping identities in a consistent order.

File and Segment Core are integrated. `IRepository<T>` itself has no new mandatory members;
custom providers need the optional `ICommittedEnvelopeSnapshotSource<T>` infrastructure capability
before using the built-in notification owner. Disk format is unchanged. Expression/setter fallback
does not by itself certify the entire serializer/provider for mobile AOT; platform qualification
remains separate. No remote/commercial implementation is included in this feature.

See [the design record](repository-notifications-design.md), the
[parallel-write performance follow-up](repository-notifications-parallel-performance-20260905.md),
and the retained [initial measurements](repository-notifications-performance-20260905.md).
