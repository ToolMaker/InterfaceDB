# Performance proposal: compatibility and measurements — 20–21 October 2026

## Windows three-build comparison — 21 October 2026

### Result and decision

The completed matrix passed **18 sequential processes, 117 engine/cache
configurations and 1,413 measurement rows**. It ran from 08:09:37 to 08:53:24
SAST on 21 October, without a restart. The summarizer independently checked
every reported p50/p95 against its raw samples and verified the frozen assemblies.

Keep A1 and the committed-value cache fix, including the measured bookkeeping
refinement. Keep the durability defaults. A2 is not adopted and A3 remains an
unadopted experiment. The evidence supports these specific conclusions:

- **A1 improves uncached SegmentCore broad reads.** Relative to the cache-only
  build, the 948-row range takes 28% less time, the 3,251-row conjunction 44%
  less time, and prefix/suffix scans about 53–54% less time. Range/conjunction allocations
  decrease about 43%; scan allocations decrease about 48%. This does not
  establish an encrypted-workload speedup: contextual serializers retain the work.
- **The cache fix greatly improves selective indexed searches.** StrongObject
  exact search changes from 1.5129 to 0.0056 ms, and WeakObject from 1.8622 to
  0.0052 ms. The 948-row cached range improves about 30% and 38% respectively.
  Almost all of those gains belong to the cache change, not A1.
- **Copying has a substantial cost for other operations.** A direct cached read
  changes from 0.3–0.4 microseconds and zero allocation to 1.2–1.3 microseconds
  and 920 bytes per read. StrongObject's 3,251-row query becomes **2.8 times as
  slow** (2.7608 to 7.7317 ms); WeakObject becomes **1.57 times as slow**
  (3.5246 to 5.5496 ms). Cached full scans become roughly **3.6–10.7 times as
  slow**, depending on mode and predicate. These regressions must accompany any
  claim about faster indexed cache searches.
- **No write-speed breakthrough is established.** ExplicitFlush SegmentCore
  writes remain around 3.0–3.1 ms, close to this SQLite configuration. Local SQL
  Server remains faster for single durable writes. File's uncached query timings
  do not establish an A1 speed improvement in this workload.

The copied reads implement the user's explicit requirement: other readers see
committed B until a successful save publishes C. Saved-data query results remain
correct; shared reference identity and unsaved-edit visibility intentionally
change. Durability, revision checks, transactions, encryption context and the
compaction deadlock fix are retained. Faster scores do not justify returning
shared mutable objects again.

### SegmentCore reads at 10,000 records

MetadataOnly means no retained object cache, not cold OS/storage caches. Prefix
and suffix here use the existing culture-sensitive predicates and scan all
10,000 records, even when few results are returned.

| Cache mode | Operation (returned rows) | Baseline ms | Cache ms | Cache + A1 ms | Final process p50 range ms |
| --- | --- | --- | --- | --- | --- |
| MetadataOnly | Known-key read (1) | 0.0108 | 0.0107 | 0.0081 | 0.0041–0.0081 |
| MetadataOnly | Exact (1) | 0.0447 | 0.0453 | 0.0384 | 0.0090–0.0402 |
| MetadataOnly | Range (948) | 6.5663 | 6.1599 | 4.4292 | 3.5740–4.9386 |
| MetadataOnly | Conjunction (3,251) | 19.5239 | 19.7425 | 11.0211 | 10.8621–11.1153 |
| MetadataOnly | Prefix scan (1,000) | 43.6415 | 44.1135 | 20.5600 | 20.4986–21.4482 |
| MetadataOnly | Suffix scan (20) | 42.1556 | 42.3889 | 19.5353 | 19.4934–20.4811 |
| MetadataOnly | Contains (39) | 0.3190 | 0.3145 | 0.2213 | 0.2202–0.2453 |
| StrongObject | Known-key read (1) | 0.0003 | 0.0012 | 0.0012 | 0.0012–0.0012 |
| StrongObject | Exact (1) | 1.5129 | 0.0057 | 0.0056 | 0.0056–0.0057 |
| StrongObject | Range (948) | 1.7447 | 1.2427 | 1.2213 | 1.2196–1.2344 |
| StrongObject | Conjunction (3,251) | 2.7608 | 7.9290 | 7.7317 | 7.5868–7.7982 |
| StrongObject | Prefix scan (1,000) | 1.5539 | 13.6885 | 13.4867 | 13.1964–13.6761 |
| StrongObject | Suffix scan (20) | 1.1794 | 12.7329 | 12.6190 | 12.2458–12.7211 |
| StrongObject | Contains (39) | 1.4802 | 0.0657 | 0.0654 | 0.0580–0.0655 |
| WeakObject | Known-key read (1) | 0.0004 | 0.0013 | 0.0013 | 0.0012–0.0021 |
| WeakObject | Exact (1) | 1.8622 | 0.0052 | 0.0052 | 0.0051–0.0215 |
| WeakObject | Range (948) | 1.9683 | 1.3013 | 1.2288 | 1.2150–1.7459 |
| WeakObject | Conjunction (3,251) | 3.5246 | 5.6235 | 5.5496 | 5.4740–5.8695 |
| WeakObject | Prefix scan (1,000) | 4.2999 | 15.8350 | 15.5119 | 15.4126–15.5948 |
| WeakObject | Suffix scan (20) | 3.8385 | 14.4436 | 14.4386 | 14.4109–14.4988 |
| WeakObject | Contains (39) | 1.6714 | 0.0575 | 0.0565 | 0.0564–0.0681 |

Ranges in the last column are the minimum and maximum **process p50**, not
individual-operation bounds or confidence intervals. Short uncached timings
vary markedly with run order. Tail behavior does not always follow the median:
StrongObject range p95 rises from 1.8236 to 1.9807 ms despite the lower p50;
MetadataOnly range p95 falls from 11.9773 to 9.5395 ms. All samples and process
p95 values are retained in the raw reports and summaries.

### Current query comparison with SQL

Milliseconds, same 10,000-record fixture and full-object checks. InterfaceDB
columns use Cache + A1; SQL values are medians of the three unchanged control
processes.

| Query | Segment MetadataOnly | Segment StrongObject | Segment WeakObject | SQLite direct | SQLite/Dapper | SQL Server |
| --- | --- | --- | --- | --- | --- | --- |
| Exact (1) | 0.0384 | 0.0056 | 0.0052 | 0.0223 | 0.0257 | 0.1024 |
| Range (948) | 4.4292 | 1.2213 | 1.2288 | 1.1736 | 1.4811 | 1.6942 |
| Conjunction (3,251) | 11.0211 | 7.7317 | 5.5496 | 4.0387 | 5.0699 | 4.4611 |
| Prefix scan (1,000) | 20.5600 | 13.4867 | 15.5119 | 1.1326 | 1.4405 | 3.5745 |
| Suffix scan (20) | 19.5353 | 12.6190 | 14.4386 | 1.0225 | 1.0321 | 15.3855 |
| Contains (39) | 0.2213 | 0.0654 | 0.0565 | 0.9094 | 0.9163 | 23.4041 |

SQLite direct remains faster on the uncached exact/range/conjunction queries
and the prefix/suffix scans. Cached exact searches beat both SQL controls;
cached ranges are close to SQLite direct and faster than this local SQL Server
case. Cached conjunctions remain slower than both SQL controls. InterfaceDB's
contains index gives it an advantage in that query; SQL has no substring index
in this fixture. SQL prefix queries use different indexable string semantics,
while these InterfaceDB prefix queries scan. These are configured repository
workloads, not a universal ranking of engines or collations.

### Allocation trade-offs

KiB allocated per complete operation (1 KiB = 1,024 bytes):

| Cache mode | Operation | Baseline KiB/op | Cache KiB/op | Cache + A1 KiB/op |
| --- | --- | --- | --- | --- |
| MetadataOnly | Range (948) | 2129.10 | 2138.03 | 1217.88 |
| MetadataOnly | Conjunction (3,251) | 7445.34 | 7495.86 | 4320.38 |
| MetadataOnly | Prefix scan (1,000) | 20390.50 | 20545.76 | 10779.30 |
| StrongObject | Known-key read (1) | 0.00 | 0.90 | 0.90 |
| StrongObject | Range (948) | 141.14 | 933.33 | 933.28 |
| StrongObject | Conjunction (3,251) | 445.39 | 4937.00 | 4936.98 |
| StrongObject | Prefix scan (1,000) | 97.51 | 9083.43 | 9083.43 |
| WeakObject | Conjunction (3,251) | 584.42 | 3431.26 | 3431.25 |

The context skip removes temporary work. Copy isolation adds object-graph
allocation, including for scanned objects that are later rejected by a
predicate. The figures do not measure retained snapshot/cache memory. There is
no claim that the new object cache uses less total memory than the shared cache.

### File reads and durable writes at 1,000 records

File point reads and exact searches:

| File cache mode | Operation | Baseline ms | Cache ms | Cache + A1 ms |
| --- | --- | --- | --- | --- |
| MetadataOnly | Known-key read (1) | 0.0917 | 0.0919 | 0.0860 |
| MetadataOnly | Exact (1) | 0.1562 | 0.1588 | 0.1444 |
| StrongObject | Known-key read (1) | 0.0004 | 0.0014 | 0.0013 |
| StrongObject | Exact (1) | 0.1414 | 0.0053 | 0.0054 |
| WeakObject | Known-key read (1) | 0.0003 | 0.0011 | 0.0011 |
| WeakObject | Exact (1) | 0.1468 | 0.0054 | 0.0053 |

File cached range medians improve from 0.1716 to 0.1208 ms (StrongObject) and
0.1791 to 0.1191 ms (WeakObject). Its conjunction worsens from 0.2419 to
0.5838 ms and from 0.2726 to 0.4455 ms. Cached prefix scans worsen from
0.3850 to 1.3164 ms and from 0.4562 to 1.4311 ms. File MetadataOnly's range
(1.8823 to 1.9298 ms), conjunction (6.2466 to 6.2700 ms) and prefix scan
(20.1830 to 20.3835 ms) are approximately unchanged; do not extend SegmentCore's
A1 gains to File.

Single durable operations, baseline → cache → cache + A1:

| Provider/cache | Insert B → C → A1, ms | Update B → C → A1, ms | Delete B → C → A1, ms |
| --- | --- | --- | --- |
| File/MetadataOnly | 4.979 → 4.893 → 4.969 | 5.457 → 5.442 → 5.273 | 1.861 → 1.998 → 1.806 |
| File/StrongObject | 4.985 → 5.004 → 5.017 | 5.428 → 5.362 → 5.260 | 1.973 → 1.667 → 1.697 |
| File/WeakObject | 5.044 → 5.025 → 4.978 | 5.506 → 5.437 → 5.293 | 1.863 → 1.895 → 1.847 |
| SegmentCore/MetadataOnly | 3.081 → 3.027 → 3.012 | 3.108 → 3.060 → 3.055 | 3.049 → 3.083 → 3.139 |
| SegmentCore/StrongObject | 3.113 → 3.100 → 3.097 | 3.308 → 3.145 → 3.102 | 3.187 → 3.099 → 3.103 |
| SegmentCore/WeakObject | 3.090 → 3.098 → 3.099 | 3.266 → 3.096 → 3.099 | 3.123 → 3.092 → 3.084 |

The SQL controls alongside Cache + A1 record SQLite direct inserts/updates/deletes
at 3.0944/3.0715/3.0706 ms and SQL Server at 0.2329/0.2157/0.2054 ms. These
numbers include their different durable flush implementations. Even unchanged
SQL controls vary between runs, so small write deltas are not isolated evidence
of an engine improvement. No relaxed durability or WriteThrough default was used.

Provider-specific startup also varies: 10,000-record StrongObject SegmentCore
is 260.8 → 272.7 ms, MetadataOnly 457.3 → 456.1 ms. WeakObject's median falls
233.1 → 183.7 ms, but the final process values span 168.1–489.8 ms; this does
not establish a dependable startup gain. Full CSV/JSON summaries include all
startup, existence, File, Dapper and tail-latency measurements.

### Verification and limits

Final public Windows Release suites passed **791 tests, with 12 platform skips**:
core 391, SegmentCore 216, public transport 184. This includes contextual
serializer/rotation, committed-copy isolation, weak lifetime, custom-envelope
fallback and nonserialized runtime-state tests. Benchmark correctness checks
are additional checks, not extra unit-test cases.

The new timing matrix covers public File and SegmentCore only. It does not
measure remote TCP deployments, large-scale/concurrent workloads, mobile devices,
encrypted performance or physical power-loss recovery. Linux timing and Android
qualification remain separate work. The tested runtime is now committed as
`4e63ddb`, with a [locally validated preview.12 candidate](preview12-validation.md).
No package has been published. Follow-up [cache-read profiling](cache-read-profile-20261021.md)
did not introduce another runtime change.

### Builds and method

- **Baseline:** public commit `fde3b5392b006cb7c87f0147215a43ac65d249be`, with
  shared mutable cached objects.
- **Cache:** committed-value copies and the envelope-owned runtime bookkeeping
  refinement described below. This is the final cache implementation, not the
  slower first attempt.
- **Cache + A1:** that same cache implementation plus the serializer-capability
  context skip. No other executable runtime changes separate these two variants.
- Identical Release benchmark harness and dependencies for all three builds;
  frozen core/provider DLL hashes are checked against each report. Runtime source
  was frozen while the matrix ran. Three process runs per variant and workload,
  with rotated variant order and reversed engine order in the second round.
- Windows 10.0.19045, Ryzen 9 3950X (16 cores/32 threads), Samsung 980 PRO NVMe,
  SDK 10.0.401 and .NET 10.0.12. SQLite 3.53.4 and local SQL Server 16.0.1000.6
  Developer Edition. Receipts verify one boot session and no overlapping benchmark
  processes. No builds or tests ran alongside the timing matrix; ordinary
  workstation/background activity is not fully controlled.
- **Mixed, 1,000 records:** File and SegmentCore in MetadataOnly, StrongObject
  and WeakObject modes, plus SQLite direct, SQLite/Dapper and SQL Server. Each
  process uses 100 measured queries per shape, 1,000 individual inserts and up to
  1,000 updates/deletes. Cache lifetime follows ordinary GC behavior.
- **Warm reads, 10,000 records:** SegmentCore in all three modes, 400 measured
  queries per shape. Strong/weak modes retain caller-owned results throughout
  the timed query phase to keep their cache populated. This does not measure a
  forced-collected weak cache. SQL controls run once per round in the baseline
  process; their code is identical across variants.
- Known-physical-key reads resolve identity outside timing, then materialize and
  consume all fields for at least 1,000 calls. This bypasses search planning, so
  do not compare that probe directly with SQL's indexed ID query as equivalent
  APIs. Query shapes have five warmups; point probes have sixteen.
- Full-object result counts/checksums are checked on every query. Mutations and
  reopen are checked against complete snapshots. File Durable publication,
  SegmentCore ExplicitFlush, SQLite WAL/FULL and SQL Server with delayed
  durability disabled are unchanged. These different flush implementations are
  still part of the end-to-end write comparison.
- Tables use the median of three process p50 values, in milliseconds per
  operation. The raw summary also retains process ranges and p95 values. No
  outliers are removed. Allocations are process-wide allocated bytes per operation,
  not retained cache memory or bytes per stored record.

### Reproduction and evidence

The runner is [`Measure-RepositoryCacheVariants.ps1`](../Scripts/Measure-RepositoryCacheVariants.ps1).
It takes the three frozen binary directories, a fresh output directory and
`-IncludeSqlServer`; it refuses to overwrite a report. The matching
[`summarize_cache_variants.py`](../Scripts/summarize_cache_variants.py) checks
assembly hashes, all report outcomes, raw-sample percentiles, settings, boot
receipts and process overlap, then writes JSON and CSV summaries.

Machine-local evidence is retained at
`C:/Code/C#/InterfaceDB/.codex-cache-a1-comparison-20261021`: `matrix/` contains
the final reports, raw samples, logs and receipts; `manifest.json` records the
binary hashes; `tests/` contains verification logs/TRX files. `initial-attempt/`
and `tracking-check/` preserve the interrupted comparison, rejected incomplete
prototype and cache-bookkeeping diagnostic. Those diagnostics are not mixed into
the final three-run medians.

The benchmark build also emitted existing NU1902/NU1903 warnings for its
transitive `System.Security.Cryptography.Pkcs` 9.0.18 dependency. They remain in
the build log; this change does not update or assess that dependency.

## A1 integration — 21 October 2026

The revised A1 proposal is integrated in File and SegmentCore. Serializer calls
construct the existing storage context only when the configured implementation
supports `IContextualObjectSerializer<TContract>`. Contextual serializers still
receive the same provider/contract/physical-key context. Native migrations keep
their separate full-context factory because they decrypt through the encryptor
directly. Committed cache snapshots, retained editing and revision reads use the
same serializer capability. No encryption, storage format or durability default
was changed.

The supplied patch targeted `fde3b53` and required adaptation to the newer cache
snapshot code. Its context/rotation tests were retained and exact-context checks
expanded to MetadataOnly, StrongObject and WeakObject. Final Windows Release
verification, including the cache bookkeeping refinement below, passed core
**391** (10 platform skips), SegmentCore **216** (2 platform skips), and public
transport **184**. A2 was not adopted; A3 and precomputed encrypted
context prefixes remain experiments. The immutable-getter cache workaround is
unnecessary for built-in providers under the clarified committed-value model.

The designer's 21 October Linux reports were checked against their raw samples:
all 30 reports passed their runner checks, and the reported p50 values match its
nearest-rank calculation. Those reports predate the cache isolation fix. They do
not establish the current Windows results or measure current copy costs.

### Cache bookkeeping found during measurement

The first Windows matrix exposed an additional cost in the initial copy-based
cache implementation: every copy entered global weak tables. Later broad cached
queries became much slower, so that matrix was stopped and preserved as diagnostic
evidence. A runtime trace showed finalization and lock waits alongside the scan.

A controlled 10,000-record StrongObject prefix-scan pair, with A1 in both builds
and 400 timed iterations, changed the process p50 from **93.5087 to 13.1859 ms**
when runtime ownership moved onto both built-in envelope types. This is one
diagnostic pair, not a general speedup claim. An earlier incomplete experiment
covered only the file envelope and missed the serializer's private envelope; it
did not improve the result and was not adopted.

The adopted refinement uses a focused internal capability for cache snapshots,
caller ownership and commit order. Custom envelopes retain the weak-table
fallback. It preserves independent committed copies and collectible weak caches;
these runtime fields never enter serialized payloads. It is applied to both cache
variants of the final comparison so the A1 delta remains separate. Copying and
allocation still have costs, and saved-query results must not be confused with
the deliberately changed visibility of unsaved edits.

## Subsequent requirement clarification — 21 October

The compatibility assessment below records the shared mutable-cache behavior
that existed when the proposal was reviewed. The user has since clarified the
desired behavior: other reads, including cached reads, should see committed
values while an editor works on a separate draft, with publication after a
successful save. Preserving visibility of unsaved shared edits is therefore no
longer the target requirement. The built-in File and SegmentCore paths now enforce that rule for ordinary
reads too; see the [upgrade guide](storage-correctness-upgrade.md#query-and-cache-behaviour).

Existing explicit `BeginEditAsync` sessions provide this separation for their
working copies. The saved-index shortcut must still not be applied alone: it
does not isolate live mutable objects or provide coherent committed-value reads.
Any cache optimization must first satisfy the clarified isolation contract.
See [retained editing](retained-editing.md) for historical baseline retention.
The remainder of this document records the original assessment, not the
superseding cache contract. Its statements about preserving shared object identity
and unsaved-edit visibility describe the earlier requirement and are superseded
by the clarification above.

## Original decision (before the committed-read clarification)

Preserve InterfaceDB's current model. Performance alone does not justify changing
live cached-object search results, shared object identity, durable acknowledgement,
revision protection, transaction behavior or per-repository configuration.

The unused encryption-context work is a credible optimization candidate. The
saved-candidate cache shortcut is incompatible with the existing requirements.
Other proposals need implementation changes and targeted evidence before adoption.
No production implementation or configuration was changed in this review.

## Scope and evidence

Reviewed the supplied summary and the six files in
`interfacedb-perf-harness.tar.gz`, against public commit
`fde3b5392b006cb7c87f0147215a43ac65d249be`. The linked Claude artifact was not
accessible, so this does not claim to review additional text in that document.
The supplied Linux timings are developer-reported measurements; they were not
independently rerun as performance measurements during this compatibility review.

The patch passes `git apply --check -p2`. It was applied only to an isolated
archive of the committed public source in the historical artifacts workspace.
The existing `ReopenedFileCacheExposesUnsavedEditsToIndexedQueries` regression
was run with its original assertions. One temporary test-only line selects the
prototype flag from an environment variable:

| Prototype mode | StrongObject | WeakObject |
| --- | --- | --- |
| SavedValueSearch off | Pass | Pass |
| SavedValueSearch on | Fail: expected 1 result, got 0 | Fail: expected 1 result, got 0 |

Logs, TRX files, the unchanged attachment, its SHA-256, and the isolated source
are retained in `.codex-perf-proposal-review-20261020`. The source repository's
tests were not edited. This was a focused behavioral check, not a full test suite.

## Requirements that must survive

- Live cached objects may be mutable and shared. Indexed searches include live
  cached objects whose unsaved edits now match, exclude those that no longer
  match, and preserve the returned shared instance. Unsaved edits are not persisted.
- Weak cache matches stay alive while being returned; queries must not resurrect
  a deleted or superseded cache entry.
- Encrypted payloads retain the same authenticated contract/provider/key context,
  corruption checks, key-rotation behavior and conditional replacement protection.
- Save acknowledgement, revisions, commit ordering, rollback and uncertain-I/O
  fault behavior remain intact. Notifications must not announce an uncommitted save.
- Separate contracts/repositories can have separate configurations. Higher-level
  code uses abstractions and DI; process-global experimental switches are not a
  production configuration design.
- Streaming, bounded memory, cancellation and device responsiveness remain
  relevant, including mobile clients and concurrent TCP service requests.

These requirements are supported by the
[upgrade contract](storage-correctness-upgrade.md), the
[cache documentation](storage-indexing-performance.md), the
[transaction contract](segment-core-transactions.md), and current regression tests.

## Proposal decisions

### 1. Skip encryption context when the serializer does not consume it

**Accept as a candidate for implementation and measurement.** Today SegmentCore
constructs the SHA-256 context before `ContextualObjectSerialization` decides
whether the configured serializer implements `IContextualObjectSerializer`.
For a serializer without that capability, the context is unused. Testing the
existing interface first fits the architecture and can avoid redundant work.

Do not substitute a general "encryption disabled" flag or a concrete serializer
type check. A custom contextual serializer can consume associated data even when
its purpose is not encryption. Preserve the exact context bytes for every
contextual serializer, both read and write paths, and retain the separate
`IRotationAwareObjectSerializer` path. Verify encrypted reopen, wrong-context/key
rejection and rotation, alongside an ordinary serializer comparison.

### 2. Synchronous materialization for records without replacement payloads

**Revise the supplied implementation before considering adoption.** Factoring
already synchronous CPU work can preserve behavior. However, the prototype calls
`Materialize`, then calls `MaterializeAndCacheAsync` if a replacement payload is
returned; that method calls `Materialize` again. A rotation read therefore invokes
deserialization/lifecycle work twice. Custom implementations are not required to
make duplicate calls harmless or cheap.

Materialize once, preserve all validation/revision/cache-admission work, and await
conditional replacement only when needed. Bulk encrypted/rotation reads and
concurrent update/delete checks are required; ordinary unencrypted timing alone
does not cover that branch.

### 3. Index candidates plus cached objects, without checking the live cache

**Reject under the current model.** Save an object with Score=1, obtain its shared
cached instance, edit Score to 99 without saving, then search for Score=99. Current
behavior returns that same instance. The shortcut consults the saved index, which
has no candidate for 99, and misses it. Both parameterized regression cases above
demonstrate this failure. The changed manager is shared across provider types.

There is a correction to the supplied description: the attached patch still
rechecks the predicate on index candidates, so it filters out an object edited
away from a match. Its demonstrated defect is missing objects edited into a
match. It is also not a coherent saved-values snapshot: it combines saved index
membership with live object values.

Returning copies would change shared-instance/aliasing behavior and unsaved-edit
visibility too; it does not preserve today's full contract. Neither change should
be slipped in as an optimization. The existing accepted mutable-cache behavior
remains the requirement for this work.

### 4. Synchronous storage reads and larger read windows

**Conditional experiment, not a universal replacement.** Synchronous file reads
can block the calling thread and cannot cancel an in-progress OS read in the same
way as the existing asynchronous path. The prototype also serializes segment
groups that currently read in parallel. Warm small-data results do not establish
behavior on slow storage, mobile UI callers or concurrent server requests.

Increasing a window from 64 to 512 increases per-batch work and memory. Preserve
first-result/early-exit behavior, cancellation, read ownership during compaction,
frame validation and ordering. Any policy must be bounded and per repository,
selected through options or an injected strategy. The static `PerfFlagsCore`
window affects all repositories in the process, contrary to separate contract
configuration. Neither global flags nor copied provider-specific coordinators
belong in the production design.

### 5. Let an idle writer append on the caller's thread

**Plausible optimization, substantial correctness review required.** An empty
queue does not mean the worker is idle: it may already have dequeued a write.
An inline path needs exclusive writer ownership coordinated with enqueueing,
rotation, checkpoints and disposal. Preserve ordering, group commit, queue limits,
admission/cancellation outcomes, durable acknowledgement and fault propagation.

The supplied patch does not implement this path. Its raw 336-byte write test
isolates submission/handoff costs; it does not reproduce the complete repository
or SQLite commit path. One ordinary autocommit frame does not imply all
transactions, rotation and maintenance perform identical disk work. The patch
also defines write-timing counters without adding their log-writer updates, so
the harness's split-write measurement is skipped with this attachment alone.

### 6. Use WriteThrough more broadly

**Retain explicit configuration; do not change the portable default on this evidence.**
WindowsNvme already selects WriteThrough, and Custom options can select it.
Previous local measurements support its lower latency on this machine. This is
a flush-strategy choice, not a proof that a new engine algorithm is faster or
that every device honors equivalent persistence guarantees. Preserve the agreed
device/power-loss qualification gates before making broader durability claims.
No-flush and SQLite NORMAL variants are diagnostic comparisons and cannot replace
our durable configuration to obtain a winning result.

### 7. Reduce index-maintenance contention

**Investigate while preserving synchronous correctness and ordering.** The shared
lock is per index-manager instance, not one lock for every repository in the
process. It protects mutation ordering, buffered initialization, updates, deletes
and retained ordering history. Removing it or making index updates lag behind a
successful save risks restoring the stale-index bugs already fixed.

The no-index comparison removes all index work, not only lock contention. Both
engines in that comparison also bypass durability flushes. A 4x difference
there does not establish a 4x gain for a fully indexed durable implementation.
Use lock/CPU evidence, then evaluate finer coordination with concurrent
save/delete/reinsert, transaction and failure regressions kept intact.

## Benchmark corrections before accepting speed claims

- Query checks discard the returned checksum. Their expected count comes from
  the first result of the engine under test; exact queries skip the count
  comparison. A stable wrong answer can pass. Validate against an independent
  fixture, including complete values and post-mutation/reopen contents.
- SQL and SegmentCore updates differ: SQL assigns Age from the iteration and
  increments the stored Revision; SegmentCore changes Age from the object's
  existing value and does not increment that application property.
- Stopwatch/Interlocked probes execute even with optimization flags off. Use
  uninstrumented timing builds for speed claims and separate diagnostic builds
  for attribution. Retain raw samples and repeated, alternating processes.
- Flags are enabled after the main warmup and remain global for later engines.
  Apply each configuration before its warmup and isolate/reset it reliably.
- Label StrongObject results as shared hot-object caching; SQLite constructs
  new objects. The results do not establish identical object-lifecycle costs.

## Next step

Proceed first with the capability-based context optimization and a corrected
single-materialization path, each as a separately measured experiment with the
existing behavioral tests intact. Keep the cache shortcut rejected. Evaluate
read scheduling, window size, inline writes and index coordination only with
their specific compatibility conditions demonstrated. No claimed percentage is
an acceptance criterion that overrides the InterfaceDB contract.
