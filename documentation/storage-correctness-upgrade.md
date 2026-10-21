# Storage correctness fixes and upgrade guidance

These changes are in the current source after preview.11. They are not a newly published NuGet release.

The [20 October performance baseline](performance-results.md#storage-review-follow-up--20-october-2026)
records repeated before/after measurements, cache costs, allocations and limitations.
The [benchmark guide](../StorageBenchmarks.Repository/README.md#storage-review-baseline)
contains the repeatable commands and result-validation rules.

## Query and cache behaviour

- Expression planning preserves conversions on captured values. When a conversion of an indexed property cannot be represented by the index, the repository evaluates the original predicate through a scan. Lossless integral promotions (including byte, short, char and enum comparisons) use the index when the bound fits exactly. Fractional, out-of-range, narrowing, floating-point member conversions and nullable unwrapping retain scan semantics.
- Index getters are evaluated before a repository save starts writing. A throwing getter rejects that save. Batch saves validate every input first.
- Saves explicitly update indexes after commit; physical notifications also maintain indexes for writes arriving through other paths. Duplicate commit notifications are ignored. An unexpected index-update failure is reported through configured telemetry and disables index plans for that repository instance. Queries then scan authoritative repository values. `SearchIndexManager.ExplainSearch` reports the effective fallback and its `FallbackReason`, even with telemetry disabled. Dispose and reopen the repository to rebuild its indexes.
- Built-in File and SegmentCore reads now return independent copies of committed values, including nested mutable lists, arrays and envelope data. This applies to `StrongObject`, `WeakObject`, `[StorageCached]` and `CacheObjectInstance`. Editing a returned object does not change any other reader or search result. Only a committed `SaveAsync` publishes its replacement. Abandoned edits, rejected conflicts and pre-commit cancellations leave the previous value visible. Do not mutate an object concurrently with its own save.
- Built-in providers expose `ICachedEnvelopeSource<TContract>.IsolatesCachedReads`. Their indexes describe all searchable values, so indexed searches load saved candidates and recheck their predicates without scanning every cached object. `ExplainSearch.RechecksCachedObjects` is false for this path. Custom providers default to the earlier shared-object behavior: cache-only enumeration rechecks live mutable objects, or a scan fallback applies when that capability is absent. A custom File reader may implement `IIsolatedObjectReader<TContract>` only if it upholds the same isolation guarantee.
- Caching still avoids storage reads. Each cache hit materializes an independent object graph from privately owned committed bytes using the configured serializer/lifecycle and encryption context; it does not reserialize the cached object. This intentionally replaces shared reference identity with committed-value isolation and adds allocations/deserialization. Strong caching retains private objects and their encoded snapshots. Weak caching releases them once no caller holds a copy. The [21 October comparison](performance-proposal-review-20261020.md#windows-three-build-comparison--21-october-2026) measures the new behavior against the shared-cache baseline and SQL: selective indexed searches improve, while direct cached reads, large cached results and scans cost more.
- `ExplainSearch` describes the plan at the time of the call, not a transactional promise about a later search. Existing scan concurrency semantics and the compaction deadlock fix are unchanged.
- `QueryAsync` now declares its possible null return. Prefer `QueryFirstAsync` when writing new code.
- Custom explicit implementations of the generic `IRepository<TContract>.QueryAsync<TImplementation>`
  must return `ValueTask<TImplementation?>` and include `where TImplementation : class`
  so the compiler interprets the nullable type parameter correctly. This does not require a data migration.
- A lost stored/deleted index-maintenance subscription now disables index plans and
  reports the reason in `ExplainSearch`; reopen to rebuild and register fresh subscriptions.
  File and SegmentCore callback failures use the configured telemetry failure/logging
  path. A failed legacy subscriber remains detached; other subscribers continue,
  and a completed commit is not reported as a failed write.

`Idb.Analyzers` diagnostic `IDB005` warns when a supported inline search uses
`StartsWith` or `EndsWith` with a known non-ordinal comparison on a directly indexed
string member. This is a compile-time warning, not a change to query semantics.
Use ordinal comparison only when it matches the application's intended meaning;
see the [analyzer guide](../Idb.Analyzers/README.md) for its scope.

## Storage keys

Identity factories produce logical keys. New logical keys are preserved exactly, including a suffix such as `order-6`. Multiple-object storage appends its own ordinal, producing a physical key such as `order-6-0`. Single-object storage keeps `invoice-2026` unchanged.

Logical-key queries first resolve the exact logical group. Existing physical-key aliases are accepted only when that physical key exists. Use the envelope repository's `GetPhysicalAsync` when an exact physical lookup is required. Entity existence checks use physical identity. Built-in providers implement the optional `IPhysicalEnvelopeLookup` capability, so `ExistsAsync` checks initialized physical-key metadata without reading or deserializing the payload. It follows the provider's normal metadata refresh policy and is not an integrity check; a logical group with the same name cannot substitute for a missing physical key. Custom providers without this capability retain the exact-read fallback. File ordinal allocation skips occupied keys after deletion.

Existing payload formats and correctly allocated physical keys remain readable. The fix does **not** infer the original intended identity of records whose keys were already rewritten by the old suffix bug. Before upgrading an affected store:

1. Stop its writers and take a complete backup.
2. Enumerate records and compare each record's intended logical identity with its stored envelope key.
3. If identities were collapsed, migrate into a fresh store using the corrected identity factory. Resolve duplicate intended identities explicitly and update references to physical keys.
4. Verify counts and logical-key lookups before switching applications to the migrated store. Keep the original backup.

Do not rename encrypted payload files or rewrite raw keys: authenticated encryption binds payloads to their storage identity. Use repository reads and saves through the configured serializer and encryptor.

## Scans, compaction, and transactions

SegmentCore scans capture key membership at scan start and resolve current locations in bounded batches. They release storage reservations before yielding to application code. A save inside a query loop can therefore complete while compaction runs. The scan is not a transaction snapshot: concurrent deletions may disappear and later batches may observe newer committed values.

**Concurrent scan compatibility:** before the deadlock fix, raw `StreamAllAsync`
retained the initial record locations throughout enumeration. The current
implementation can return a different result if those records are updated or
deleted between batches. A before/after probe with one-record batches confirms
this difference. The general repository contract did not promise snapshot
transactions, but consumers relying on that previous raw-scan behavior still
need a compatibility decision. Atomic commit is not a promise that a long scan
sees every row from one point in time; do not describe this change as preserving
all query behavior.

SegmentCore drains all admitted preparations before reporting a batch admission failure. Backpressure or cancellation can leave uncommitted preparation frames; those do not constitute a committed transaction. The store continues only when every admitted append completed without a storage failure and no commit was submitted. I/O failures and uncertain commits retain the existing requirement to reopen.

For an uncertain outcome, stop admitting application operations, let outstanding work settle, dispose the complete repository/service scope, and create and initialize a new scope against the same store. Initialization rebuilds or validates indexes against recovered storage. Re-read affected records and revisions before deciding whether to retry. Do not clear fault flags or blindly retry a batch whose commit outcome is unknown.

## Bounded tracking

Revision tracking retains current live-record revisions plus at most 1,024 missing/deleted entries. It no longer clears all revisions at 4,096 keys: that caused every update in a larger sequential working set to reread the committed payload. Memory therefore scales with current records, while deleted-key history remains bounded. After the first missing-entry retirement, only authoritative reads under the write guard can seed unknown revisions; late materializations cannot restore a retired deletion. Reusing a key cannot let an older tombstone evict its current revision. Deleted-key ordering retains at most 1,024 recent notifications in addition to live-key state. If a notification arrives older than the retained ordering window, the repository disables index plans and uses scans until reopened. These bounds do not limit record count.

## Durable publication and the revision clock

The File provider's Durable mode publishes replacements and deletions through `IFilePublication`. Default DI supplies `PlatformFilePublication`; applications can register an implementation before repository registration when their environment requires another publication strategy. The implementation uses directory `fsync` on supported POSIX systems and write-through rename on Windows. Windows deletion first renames the payload to an ignored tombstone; a crash can leave the tombstone for later cleanup. Filesystem and device flush guarantees still apply; these tests do not constitute physical power-cut qualification.

`IFilePublication.EnsureDirectory` establishes the directory path before durable
publication. The default interface and POSIX platform implementations create
missing directories one level at a time, flushing each new directory and its
containing directory. They stop at the nearest existing containing directory;
already provisioned paths do not cause ancestors to be opened or flushed.
A failed flush remains pending on that publication instance and must succeed
before it acknowledges that directory or a descendant on a retry.
The Windows platform implementation publishes new directories with write-through
renames. On all platforms, existing, externally provisioned directories are
assumed to have been persisted by their owner. The File composition root uses the injected publication
service for its root/version directories and watcher startup. Resolving the watcher
early no longer creates a storage root as a side effect of probing path case
sensitivity. Commit coordinators retain successful
directory initialization per instance; a failed barrier is retried before payload
preparation. The revision clock also establishes its directory before issuing a
reserved revision. Custom publication implementations should override this method
if directory barriers differ on their platform. The existing constructors remain
available.

Clock reservations are fully written and flushed to a staging file, then atomically published before any revision from the range is issued. The committed `.idb-revision-clock` retains the existing 16-byte format. Corruption or truncation of that committed record still fails closed. Incomplete `.idb-revision-clock.creating-*` files are ignored; they can be removed while the store is offline.

**Upgrade every writer sharing a store together.** The new clock uses a stable `.idb-revision-clock.lock` lease across atomic replacements. Do not run old and new writer binaries against the same directory. Back up the entire quiescent store, including its committed clock, and never reset the clock to recover from an error.
