# Repository notifications: design record

**Status:** implementation authorized by Ernie after review, 5 September 2026. The original
review draft is retained below as the design record; its "proposed/current" descriptions refer
to the pre-implementation state. See [the implemented API and boundaries](repository-notifications.md)
and [measured qualification](repository-notifications-performance-20260905.md).
No release, commit or publication has been requested as part of this implementation.

Implementation decisions: isolated recipient snapshots; bounded asynchronous queues with visible
fault/detach; top-level value comparison with explicit complex-property comparers; single-owner
File/Segment Core coverage; default disabled fast path. The initial implementation used a broad
mutation gate. The authorized follow-up replaces it with phase handover, an ungated empty path,
per-identity observed ordering and a conservative new-identity publication barrier. Owned encoded
before/after states are handed to the asynchronous dispatcher, where detached objects are
compared once per committed event. The public API and envelope notifications are unchanged. Automatic causal
origin suppression and explicit subscription write scopes address the subsequently raised
notification feedback-loop requirement. These scopes are local provenance, not persisted metadata.

## 1. Summary and agreed boundary

Add object-bearing **Created**, **Changed** and **Deleted** subscriptions to the **free,
MIT-licensed InterfaceDB repository layer**. Subscribers can filter notifications by contract
values. Changed additionally identifies the contract properties that changed. Subscription
lifetimes must be explicit, clean and inexpensive to manage.

**Leave the envelope notification API, payloads and behavior unchanged.** Do not add an envelope
Changed event, query filters or property-diff machinery. Existing low-level subscriptions and
index consumers must continue to work as before.

This is a local, in-process observation feature, not a durable event bus. Remote delivery,
distributed coordination and replay are separate extensions; an extension should be able to
expose the same application-facing observation contract without requiring different handlers.
This document does not move any existing separately licensed implementation into the MIT package.

| Layer | Purpose | Notification payload | Filtering |
| --- | --- | --- | --- |
| Envelope, existing and unchanged | Report physical storage operations to infrastructure | Envelope | No subscriber query filters |
| Repository, proposed free feature | Report committed entity effects to applications | Created/Changed: saved entity with envelope; Deleted: detached prior entity without envelope | Registered contract-value predicates |

## 2. What exists today, and what must be added

| Concern | Verified current behavior | Required change |
| --- | --- | --- |
| Save result | Save returns the canonical saved contract entity with an envelope attached. Under a coalescing provider this can differ from the caller's proposed value. | Preserve this return contract. Notifications must describe committed values, not assume the input was persisted unchanged. |
| Physical insert notification | File and SegmentCore publish Stored and then Added when a new physical record is written and notifications are enabled. Handlers receive an envelope. | Add a separate repository Created notification; do not replace Added. |
| Existing-record save | Stored also covers an existing object being saved. It does not identify changed properties. | Repository Changed must report actual changed contract properties, not every Stored callback. |
| Physical delete | Providers mark the envelope Deleted and notify with that envelope. They do not create an envelope-free notification snapshot. | Repository Deleted must provide the detached prior entity, while preserving physical deletion behavior. |
| Query-filtered subscriptions | No such repository notification API exists. | Add repository-only subscription predicates. |
| Subscriber lifetime | Envelope subscriptions expose IDisposable/IsDisposed and optional keep-alive callbacks. Keep-alive is evaluated during publication, not by a periodic health checker. | Define repository disposal/cancellation, dispatch-race and failure semantics without changing existing envelope contracts. |

The existing envelope names are Added, Stored, Deleted and Removed. Removed can describe a
physical-name removal during reconciliation; it is not automatically a logical entity deletion.

## 3. Event semantics and object ownership

### Created

- Publish for a successfully committed new repository entity, after its indexes are ready.
- Pass the saved entity through its contract interface, with an envelope attached.
- Created is the event name, not a text payload or a key-only hint.
- Do not emit Changed as well for the same insertion.
- Do not invent creation events during initial enumeration, index rebuilding or subscription.
- A retry, replica write or repeated physical notification is not a second logical creation.
- No local/remote flag is required for ordinary Created handling. Optional future diagnostic
  metadata must not force applications to handle different event types for different origins.

### Changed

- Publish only when an existing entity has a nonempty committed contract-property change set
  and the subscriber's filter matches the resulting saved entity.
- Pass the saved entity with its envelope attached and a read-only list of changed contract
  property names. A changed property is one whose previous committed value differs from its
  newly committed value under the agreed equality rules.
- Property assignments in memory do not generate events. Several edits before a save describe
  one committed change; changing a value and changing it back before saving produces no change
  for that property.
- A save with identical contract values produces no Changed event. Envelope location, cache
  state, storage version bookkeeping and opaque infrastructure metadata do not themselves
  count as changed contract properties.
- The change set describes all changed properties in the configured contract comparison profile,
  not merely the properties mentioned in the subscriber's filter.

### Deleted

- After successful deletion of an existing entity, pass a detached copy of its last committed
  contract values with its Envelope reference cleared.
- The payload remains usable for application handling: identity and business values are retained.
- Capture prior committed values before deletion. Do not trust a caller's possibly edited delete
  argument as the historical value.
- Do not mutate objects already held by callers, clear their envelope references, or discard
  internal storage/deletion bookkeeping to create the notification payload.
- A missing/already-deleted entity does not produce a fabricated Deleted object or alter the
  existing false return from DeleteAsync. A duplicate deletion does not create another event.
- If a future external-change path cannot obtain prior committed values, it cannot promise this
  payload. That path needs explicit resynchronization semantics; it must not manufacture values.

### Payload ownership: recommended approach, subject to review

An event's values must remain stable while it is queued. Receiving an entity does not promise the
same CLR object reference as the save argument, the save return value or another subscriber's
payload. The repository may deliver a snapshot of the canonical saved entity.

Recommend an isolated snapshot for each subscriber that receives a notification, with immutable
changed-property metadata shared safely where useful. Created/Changed snapshots retain a valid
application-facing envelope and cannot mutate a provider-owned cached envelope; Deleted snapshots
retain no persistence envelope. An extension composing stores must not leak their private envelopes.

Snapshotters must preserve contract behavior, types and permitted field access. Encrypted-field
handling must not be weakened or automatically exposed in diagnostics. Mutating a notification
object does not implicitly save it. A subsequent explicit save follows normal repository rules.

Cloning has a measurable cost. A shared read-only payload mode is a possible later optimization,
not a reason to share mutable cached instances silently. Snapshot ownership is a review decision.

## 4. Registered filters

Filters belong only to repository subscriptions and evaluate one event entity. They are not a
request to rerun a database query after every save.

| Subscription | Values tested by its filter | Example |
| --- | --- | --- |
| Created | The new committed entity | A newly created German-language record |
| Changed | The updated committed entity | A German-language record whose Title changed |
| Deleted | The last committed entity before deletion | A deleted German-language record |

Changed requires an explicit predicate; an explicit match-all predicate is valid. Recommend
optional filters for Created/Deleted as well, with an omitted filter matching all such events.

Use contract-value expressions consistent with existing repository query semantics. Prepare
predicates once per registration, preserve the complete predicate as the correctness authority,
and do not substitute an index shortcut with different string/culture semantics. Initial
implementation can evaluate the predicate directly against the captured entity; subscription
routing indexes are an optimization only if benchmarks justify them.

Predicates must be fast, side-effect-free and independent of storage identity. They must not make
network calls or perform repository writes. They run as trusted application code, not as a
sandbox for arbitrary remote queries. Define supported expressions at registration; reject
unsupported shapes early. Captured mutable variables are not a reliable reconfiguration API:
use an explicit replacement subscription when changing a filter.

**Not a live query in the first version:** if Language changes from German to French, a filter
for German does not receive Changed because the new entity no longer matches. Notifying entry
into/exit from a query result, initial snapshots, sorting, paging and aggregates require separate
live-query semantics and state. That optional feature is not part of the agreed first scope.

## 5. Accurate property change sets

The difficult part is obtaining the previous committed value reliably. Applications can edit a
cached object in place; comparing two references to that object will miss changes. Search indexes
also contain only selected property values and cannot be the universal historical baseline.

Required mutation sequence when the relevant subscriptions exist:

1. Identify the repository entity and establish the mutation's ordered position.
2. Capture the previous committed values when Changed or Deleted delivery requires them; retain
   an immutable baseline or read committed storage without reusing a caller-mutated cache object.
3. Execute the existing persistence path without weakening locks, transactions or durability.
4. Establish the actual canonical result and completed index state. Determine whether this was a
   creation, value-changing update, no-op update or successful deletion.
5. Freeze the event values/change set and its ordering information. Dispatch only after mutation
   gates have been released; filters and application handlers do not run under storage locks.

The previous-value capture must belong to the same mutation order as the resulting commit.
A separate uncoordinated read followed by save is not sufficient under concurrent writes.
The one-writer-process boundary does not imply a single writing task inside that process.
Coalescing providers also require classification of actual committed effects rather than every
caller attempt. If a provider cannot establish the required predecessor/result relationship,
precise Changed delivery must be rejected or explicitly unsupported, not guessed.

Recommended first comparison profile:

- Compare persisted, readable top-level properties of the subscribed contract, including inherited
  contract properties, whether indexed or not. Exclude Envelope and infrastructure-only metadata.
- Report property names only. Old/new value pairs and nested paths are optional future additions.
- Use documented deterministic value equality for supported scalar types, including null handling.
- Mutable arrays, collections and complex values need registered snapshot/comparison support;
  reference equality or a shallow copy cannot detect in-place edits correctly. Report their
  top-level property name when their configured value comparison changes.
- Do not execute arbitrary computed getters as if they were persisted values. Validate the
  comparison profile against serialization metadata at registration. Reject unsupported properties
  early rather than silently omitting differences or discovering them after a committed save.
- Bound baseline and comparison resource use. Reflection accessors may be prepared once; provide
  an AOT-compatible strategy for supported deployment targets instead of assuming runtime codegen.

The exact supported-type/equality matrix and handling of implementation-type replacement need
approval before implementation. No hashes alone may be treated as collision-free equality proof.

## 6. Proposed API and configuration shape

Recommend a subscription-only companion capability, obtained from the same repository owner and
database key. This avoids adding new mandatory members to every existing IRepository implementer.
Whether to expose an optional convenience property/extension method remains an API review detail.

The following is an **illustrative API sketch, not available or compilable as a consumer example
until implemented**. Names may change:

```csharp
public interface IRepositoryNotifications<TContract>
    where TContract : class, IStoredContract<TContract>
{
    IContractSubscription SubscribeCreated(
        Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null,
        CancellationToken lifetime = default);

    IContractSubscription SubscribeChanged(
        Func<RepositoryChange<TContract>, ValueTask> handler,
        Expression<Func<TContract, bool>> filter,
        CancellationToken lifetime = default);

    IContractSubscription SubscribeDeleted(
        Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null,
        CancellationToken lifetime = default);
}

public sealed record RepositoryChange<TContract>(
    TContract Entity,
    IReadOnlyList<string> ChangedProperties);
```

Per-repository options should cover notification enablement, supported comparison/snapshot profile,
bounded delivery capacity, subscriber-error handling and optional keep-alive behavior. Defaults
and exact API names are recommendations to review, not shipped configuration.

Resolve the capability using the same normalized database key as the logical repository. Multiple
repositories of one contract must not share subscribers accidentally. Any default/unkeyed alias
must select the same owner as the default IRepository. Interface inheritance alone does not
register an alias in dependency injection.

Do not expose producer methods to application subscribers. Repository notification enablement
must be independent of physical RaiseNotifications: the logical commit boundary can report a
successful mutation even when optional physical notifications are disabled.

## 7. Subscriber lifetime, failures and dispatch

### Local lifetime management

- Registration returns a disposable handle. Dispose is idempotent and unregisters the subscriber.
- A supplied lifetime cancellation token also unregisters it. A pre-cancelled token creates no
  active subscription. Subscription lifetime is distinct from any one save's cancellation token.
- Perform a cheap active-state check immediately before starting a callback. A callback already
  in progress when disposal races with dispatch may finish; disposal is not forced termination.
- Remove delegate references and dispose token registrations when detaching. Repository disposal
  detaches owned subscriptions and ends its dispatch workers, without disposing caller-owned stores.
- Avoid a polling timer per subscriber. Optional keep-alive checks can run during dispatch. They
  are an additional application-supplied signal, not proof that an object/process is alive.
- Explicitly document that an active delegate may retain its target. Garbage collection cannot
  reliably identify an abandoned subscriber held by that delegate. Optional weak-target support
  would need to avoid strong closure captures; it is not the default correctness mechanism.
- If callers require an asynchronous stop/drain operation, add a distinct optional capability.
  Self-unsubscription must not deadlock by waiting for its own callback to complete.

### Performance and failure isolation

Queue event work after successful effects are established and run predicates/handlers outside
repository, key and store locks. Preserve per-entity commit order for a subscriber; do not promise
a global order across unrelated keys or stores. Reentrant handlers may query/save/delete through
the repository without deadlocking the original operation. A callback's query can see the announced
revision or a later committed value; its event payload preserves the announced values.

Recommend bounded per-subscriber mailboxes with shared scheduling, not a dedicated thread per
subscriber. A slow subscriber must not block other subscribers or extend the original save's
completion to the handler's running time. Limit concurrent callback work; ordinary callbacks cannot
be forcibly terminated safely. Cancellation/drain semantics for stuck callbacks need explicit limits.

Predicate or handler failures must not turn an already committed save into an apparent failed save.
Recommend faulting/detaching the affected subscription and exposing the reason through subscription
status/diagnostics. A queue overflow must likewise be observable and require re-subscription/requery;
do not silently drop an event while pretending the subscription is complete. Exact fault/status and
overflow APIs are review decisions; the minimal sketch above does not yet express them.

This local feature is **not durable delivery**: a process crash can occur after storage commit but
before notification. It does not promise replay, exactly-once side effects or notification of every
historical transition. Within a healthy active subscription, suppress duplicate reports of the same
logical effect; that is not an end-to-end exactly-once guarantee. Subscribing alone does not provide
an atomic initial query plus subsequent changes.

## 8. Storage integration and compatibility

The dependency direction remains repository -> index manager -> envelope repository -> provider.
Normalize events at the repository mutation completion boundary. Existing envelope signals can be
used internally where useful, but a blind Added/Stored/Deleted forwarding bridge is insufficient.

| Case | Required handling |
| --- | --- |
| Physical Stored/Added for one insertion | One repository Created, not Created plus Changed. |
| Index updates | Complete before repository event dispatch; physical subscriber enumeration order is not an index-readiness guarantee. |
| Atomic batch rollback | No entity events for rolled-back effects. |
| Partial successful non-atomic batch | Report only effects actually committed and normalized; preserve the existing bulk result/error contract. |
| Cancellation before commit | No successful entity notification. |
| Cancellation/exception after a possible commit | Establish the committed outcome when possible; never invent success or rollback. Recovery gaps must be explicit. |
| No-op save or metadata-only save | Preserve physical Stored behavior; no repository Changed without changed contract values. |
| External file watcher/reconciliation | Existing physical notifications stay unchanged. Exact repository events for bypass writes require an ordered previous-value baseline and deduplication; do not claim that coverage in the first version. |
| Startup, index rebuild or initial subscription | No fabricated Created events for existing records. |

Initial scope covers mutations through the owning repository, including its single and bulk save
paths and delete. Direct envelope writes, external writers and future transport changes are not
automatically equivalent coverage. Define an explicit adapter/capability before claiming it.

File and SegmentCore are mandatory first targets, across supported cache/index configurations.
Preserve provider durability options: a local event means the configured storage operation completed;
it does not make File Performance mode durable. Test Durable mode for crash-persistence claims.
No new cross-contract-store transaction, cross-process ownership or disk-format guarantee is implied.

## 9. Performance qualification plan

No implementation or notification benchmark has been run for this design. All performance points
below are acceptance targets, not measured gains or promises of zero overhead.

With notifications disabled or no relevant subscribers, avoid notification snapshots, property
diffs, predicate evaluations, queue allocation and background notification workers. A small
registration-state branch may remain; measure it against the current baseline. Created-only
subscriptions must not activate Changed's previous-value tracking unnecessarily.

When Changed subscribers exist, even filters that ultimately match nothing may require
previous-value capture to preserve correctness. No-match delivery is not automatically zero-cost.
Track and bound the memory retained by baselines, queued entities, envelopes and subscribers.

Run a controlled before/after matrix for both providers with identical durability, cache mode,
index configuration, dataset, object sizes, hardware and warm-up. Keep full object materialization
inside equivalent timings for any existing database comparisons.

| Scenario | What it measures |
| --- | --- |
| Feature disabled; enabled with zero subscribers | Fast-path regression and allocations |
| Created-only or Deleted-only subscribers | Snapshot and delivery cost without update diffs |
| Changed, one matching and one nonmatching filter | Baseline, comparison, predicate and delivery costs |
| 1, 10, 100 and 1,000 subscriptions | Scaling, retained memory and scheduler pressure |
| Identical saves; one changed field; many changed fields | Equality/change-set behavior |
| Small scalars; large payloads; supported mutable collections | Snapshot/comparer sensitivity |
| Single writes, batches and concurrent same-key/different-key mutations | Ordering and contention |
| Slow, throwing, cancelled and reentrant subscribers | Isolation, queue limits and disposal cost |

Report throughput, median and tail save latency, notification latency, allocation/GC cost, retained
memory and failure/drop/fault counts. Do not hide dispatch cost by measuring save time alone.
Agree an acceptable fast-path regression budget before the implementation is accepted.

## 10. Implementation stages and acceptance tests

1. **Review and settle semantics.** Approve payload ownership, equality/type support, bounded
   dispatch/fault policy and supported mutation coverage. No code before review approval.
2. **Add the free observation boundary and lifecycle.** Keyed capability, disposable/cancellation
   lifetimes and a zero-subscriber fast path; keep physical notification interfaces untouched.
3. **Implement Created and Deleted.** Correct commit classification, prior-value capture for
   deletion, envelope attachment/detachment and post-index, out-of-lock dispatch.
4. **Implement filtered Changed with accurate property sets.** Validate snapshot/comparison
   profiles, same-key mutation ordering and canonical results; support explicit rejection when
   required guarantees cannot be established.
5. **Qualify both providers.** Run correctness, stress, lifecycle, cache/index, supported-platform
   and performance matrices. Publish measured results for review before any release decision.

| Test area | Required assertions |
| --- | --- |
| Created | Typed saved values and attached envelope; one logical event; no additional Changed; failures/rebuilds/retries do not fabricate creation. |
| Changed | Exact property names; same-value save silence; nulls, inherited properties, unindexed values, edit-then-revert and supported in-place collection edits; metadata excluded. |
| Deleted | Prior committed values rather than edited arguments; null payload Envelope; no mutation of callers' existing objects; missing/repeated deletion silent. |
| Filters | Correct before/after entity selection; full predicate semantics and culture behavior; no envelope queries; independent filters and explicit match-all. |
| Payload isolation | Queued values cannot drift; one handler cannot corrupt another handler's payload or cached storage state. |
| Baseline/concurrency | In-place cached edits detected; registration/disposal races, concurrent saves, coalescing and first-save-after-subscription classified correctly. |
| Integration | Physical notifications enabled/disabled; existing envelope subscribers unchanged; correct indexes in callbacks; atomic/partial batches and cancellation outcomes. |
| Lifecycle | Idempotent dispose, pre-cancelled/racing tokens, allowed in-flight completion, self-disposal, keep-alive failure and release of detached handler references. |
| Faults/resources | Predicate/handler exceptions, slow callbacks, observable overflow, bounded allocations, shutdown and optional drain do not deadlock or falsify save outcomes. |
| Security/compatibility | No payloads, encrypted values or credentials in logs; keyed repository isolation; contract/provider behavior and supported AOT targets preserved. |

Repository notifications are not user authentication or authorization. The local host remains
responsible for who can obtain a repository and subscribe. A filter is not an access-control rule.

## 11. Review decisions

The free-tier placement, unchanged envelope notifications, entity-bearing Created/Changed/Deleted,
Changed property names and explicit subscriber lifetimes are agreed requirements. The following
are recommendations or boundaries that still need approval:

1. **Payload ownership:** isolated snapshots per receiving subscriber, with valid envelopes on
   Created/Changed, versus a separately enforced read-only sharing design.
2. **Comparison scope:** top-level persisted property names initially; explicit support profiles
   for complex values, equality and implementation-type changes; no old/new pairs or nested paths
   in the initial default.
3. **Delivery policy:** bounded asynchronous dispatch, fault/detach on subscriber failure or
   overflow, visible status and resubscribe/requery recovery; no crash replay guarantee.
4. **Initial coverage:** owner-repository mutations only; filtered events rather than live query
   membership tracking, external-writer history or atomic initial snapshot subscriptions.
5. **Performance budget:** acceptable unsubscribed regression and resource/latency bounds with
   active subscriptions, to be measured rather than assumed.

### Implementation evidence inspected for this draft

The review checked IContractPubSubService/IContractSubscription, IStoredContract/IObjectEnvelope,
EnvelopeLifecycle, InterfaceRepositoryDb, SearchIndexManager, FileEnvelopeRepositoryBase and
SegmentCoreEnvelopeRepository. These establish the current event names, envelope-bearing save
results, physical delete payloads and index/mutation separation described above. Proposed APIs,
subscriber fault handling and exact change tracking are not implemented merely by documenting them.
