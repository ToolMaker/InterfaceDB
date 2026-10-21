# SQL performance comparison — 20 October 2026

The [21 October follow-up](performance-proposal-review-20261020.md) measures the
later committed-cache isolation and A1 changes separately. The shared-cache
figures in this document describe the earlier implementation.

## Scope and result

This is a measured comparison of post-preview.11 public source, not a new NuGet
release. The main matrix used product source commit
`0b567f275ca92b26c9601a46af8ea28e10dee511`; only the benchmark and documentation
changed during that experiment. The later review follow-up below measures
additional source fixes separately. FileSystem,
SegmentCore, SQLite direct, SQLite through Dapper, and local SQL Server were
measured. Commercial Atlas, TCP/network deployments and production application
soak are outside this comparison.

The main matrix passed **12 timed processes, 84 engine/cache configurations and
1,008 measurement rows**. Three further fresh-process ordinal-string probes
passed, as did the repeated storage-core/SQLite workload and its maintenance/
recovery checks. Every query verifies complete-object count and all-field
checksum. Writes, updates and deletes have full snapshot/reopen checks.

**The main improvements are exact existence and SegmentCore reads. Individual
write latency is approximately unchanged. Broad result materialization remains
slower than SQL in this workload.** There is no consistent greater-than-10%
regression in the main 10,000-record InterfaceDB before/current cases.

## Current full repository versus SQL

10,000 records; MetadataOnly/fresh objects for InterfaceDB. Values are the median
of three process p50s, in **milliseconds per operation**; lower is better.

| Operation | File | SegmentCore | SQLite direct | SQLite Dapper | SQL Server |
| --- | --- | --- | --- | --- | --- |
| Independent insert | 5.3601 | 3.1008 | 3.1133 | 3.1603 | 0.2218 |
| Exact query, 1 object | 0.1402 | 0.0180 | 0.0229 | 0.0266 | 0.1177 |
| Range, 948 objects | 20.5825 | 7.4756 | 1.5864 | 1.8237 | 2.8345 |
| Prefix, 1,000 objects (default string method) | 217.3236 | 44.2701 | 1.3994 | 1.5664 | 3.6260 |
| Suffix, 20 objects (default string method) | 205.2268 | 43.0753 | 1.0156 | 1.0443 | 15.6278 |
| Contains, 39 objects | 0.8357 | 0.3301 | 0.9112 | 0.9389 | 24.2515 |
| Conjunction, 3,251 objects | 67.9886 | 20.7415 | 4.1260 | 5.3120 | 4.6112 |
| Known-key existence | 0.0002 | 0.0002 | 0.0179 | 0.0190 | 0.1122 |
| Indexed-field update | 5.7986 | 3.1108 | 3.1100 | 3.1787 | 0.2271 |
| Delete by id | 2.0121 | 3.0813 | 3.0684 | 3.1323 | 0.1881 |

SegmentCore and SQLite are close on independent inserts, updates and deletes.
SegmentCore's exact-query median is lower, but that small cross-engine lead
does not hold in every process. SQLite is consistently faster on the range and
broad-conjunction workloads. Local SQL Server has the fastest individual writes
in this ExplicitFlush configuration; the later flush-strategy experiment isolates
that configuration choice.
InterfaceDB's contains query benefits from its substring index; these SQL
controls scan without a full-text/trigram index.

Existence measures known-key membership, with InterfaceDB envelope resolution
outside timing. It is not a payload integrity check. Sub-microsecond samples
are close to timer granularity; do not treat the displayed digits as precision
or use their ratios as a general database speedup.

The inherited prefix/suffix methods are culture-sensitive and scan in
InterfaceDB. SQL's prefix control is indexed. The separate ordinal measurements
below show the available InterfaceDB indexed path.

## What changed in the controlled before/current comparison

The retained **before** DLLs already include all original storage correctness
fixes and the cache-aware/numeric/ExplainSearch corrections. They precede only
live-revision retention and metadata existence lookup. This is not a comparison
against the released preview or against the original incorrect implementation.
Both labels run the same harness, runtime and dependencies, with different
core/provider DLLs.

The removed revision-cache capacity check called `ConcurrentDictionary.Count`
while materializing records. The .NET implementation acquires all dictionary
locks to compute that count. Inspection of the retained before/current DLLs
confirms that the former `ObserveCommittedRevision` calls `get_Count` and the
latter does not. Removing this repeated synchronization is a concrete contributor
to the read improvements; the same change also retains live revisions instead
of clearing their working set. This matrix does not isolate the contribution of
each mechanism. Do not reintroduce a whole-dictionary capacity check per returned
object. Full-scan envelope snapshots remain a separate cost.

| SegmentCore operation | Before ms | Current ms | Change | Repeated-run interpretation |
| --- | --- | --- | --- | --- |
| Independent insert | 3.1192 | 3.1008 | -0.6% | similar (within 10%; not proof of equivalence) |
| Exact query, 1 object | 0.0290 | 0.0180 | -37.9% | lower in all three pairs |
| Range, 948 objects | 16.1623 | 7.4756 | -53.7% | lower in all three pairs |
| Prefix, 1,000 objects (default string method) | 142.6332 | 44.2701 | -69.0% | lower in all three pairs |
| Suffix, 20 objects (default string method) | 139.8767 | 43.0753 | -69.2% | lower in all three pairs |
| Contains, 39 objects | 0.7367 | 0.3301 | -55.2% | lower in all three pairs |
| Conjunction, 3,251 objects | 56.5564 | 20.7415 | -63.3% | lower in all three pairs |
| Known-key existence | 0.0154 | 0.0002 | -98.7% | lower in all three pairs |
| Indexed-field update | 3.1835 | 3.1108 | -2.3% | similar (within 10%; not proof of equivalence) |
| Delete by id | 3.0942 | 3.0813 | -0.4% | similar (within 10%; not proof of equivalence) |

For FileSystem, the 10k existence median fell from **77.4 to 0.2 microseconds**,
with allocation falling from 3,512 bytes to zero. Its other timed operations
stayed within the descriptive 10% band.

SegmentCore allocation per operation:

| Operation | Before bytes | Current bytes | Change |
| --- | --- | --- | --- |
| exists-known-key | 1,896 | 0 | -100.0% |
| update-indexed-fields | 14,254 | 12,766 | -10.4% |
| delete-by-id | 9,887 | 8,444 | -14.6% |

Less allocation is not a claim of lower retained memory. The live-revision cache
now retains metadata proportional to current records; the proposed configurable
memory budget has not been implemented by this benchmarking task.

### Unchanged, worse and uncertain observations

- Individual write latency is largely unchanged: SegmentCore insert -0.6%,
  update -2.3%, delete -0.4%; File insert +1.1%, update +4.1%, delete -1.0%.
  The 10% band is a reporting convention, not a statistical equivalence test.
- The 1k opportunistic WeakObject SegmentCore range median increased from
  **0.0496 to 0.0971 ms**, but the direction differed between paired processes.
  This is retained as an inconsistent slowdown, not dismissed or declared a
  proven code regression. Weak references were neither pinned nor forced to
  collect in the main matrix.
- File StrongObject existence changed from 0.1 to 0.3 microseconds at 1k, also
  with mixed paired directions and near timer granularity.
- There were substantial individual-process write outliers. For example, the
  first current 1k File insert p50 was 34.0523 ms versus 5.2400 ms in its before
  pair; the unchanged SQLite direct control also moved from 3.1875 to 6.8501 ms.
  Those runs remain in the report. The later repeats and process medians are
  shown without assigning the outlier to a code change.
- At 10k the SQLite query controls were mostly within 10% between labels;
  direct prefix was +11.8% with mixed directions. This variation limits claims
  about small differences, while the larger repeated SegmentCore read reductions
  are a stronger signal.

## Explicit ordinal string queries

Three additional processes use the same production DLL hashes as the main
current runs. Each measures default and ordinal calls on the same 10k dataset
and records `ExplainSearch`. MetadataOnly, 50 measured calls after five warmups.
The prefix returns 1,000 objects and the suffix 20.

| Repository | Predicate | Default method ms | Explicit Ordinal ms |
| --- | --- | --- | --- |
| InterfaceDB | prefix | 246.7421 | 23.8305 |
| InterfaceDB | suffix | 239.1756 | 0.7391 |
| InterfaceDB SegmentCore | prefix | 47.7352 | 5.6426 |
| InterfaceDB SegmentCore | suffix | 45.4519 | 0.2219 |

Use `StartsWith(value, StringComparison.Ordinal)` or
`EndsWith(value, StringComparison.Ordinal)` when ordinal semantics are intended.
The probes assert that those plans use indexes. This is an application query
choice, not a production code change or a transparent replacement for linguistic
comparison. Even the indexed path still pays for materializing returned objects.

These focused probes ran separately from the SQL matrix; do not present their
differences from SQL as a newly paired SQL experiment.

## Historical full File repository comparison

For continuity, these are the original 2 September 1k MetadataOnly results
alongside current 1k medians on the same six-field fixture. The older run had five
query samples and a different runtime/package set; the new run uses fifty samples
and repeated processes. These differences are **historical observations, not
isolated effects of the latest fixes**.

| File operation | 2 September ms | 20 October ms | Observed change |
| --- | --- | --- | --- |
| Independent insert | 4.1026 | 5.4970 | +34.0% |
| Exact query, 1 object | 0.4837 | 0.1611 | -66.7% |
| Range, 90 objects | 8.7639 | 2.0741 | -76.3% |
| Prefix, 1,000 objects (default string method) | 95.3568 | 21.8460 | -77.1% |
| Suffix, 2 objects (default string method) | 86.3507 | 19.5748 | -77.3% |
| Contains, 4 objects | 0.6169 | 0.2721 | -55.9% |
| Conjunction, 324 objects | 26.5764 | 6.5348 | -75.4% |

The historical File insert is slower now (about 34%). It should remain visible
alongside the read improvements. The controlled comparison above shows that this
increase did not occur between the two last follow-up builds.

## Historical storage-core/SQLite workload rerun

Same recorded options as 5 September: 5,000 objects, five samples, batches
1/10/100/1,000, plus 1,000 independent saves; eight stripes, mutation WAL,
WriteThrough. Complete-object decoding and checksums are validated. Values below
are **milliseconds for the whole workload**, not per object.

| Workload | September Segment write | Current Segment write | Current SQLite write | September Segment read | Current Segment read | Current SQLite read |
| --- | --- | --- | --- | --- | --- | --- |
| Atomic transaction, batch 1 | 1380.23 | 1435.36 | 15964.79 | 13.17 | 13.48 | 6.46 |
| Atomic transaction, batch 10 | 238.79 | 236.50 | 1550.94 | 6.01 | 5.70 | 6.66 |
| Atomic transaction, batch 100 | 34.65 | 33.48 | 171.31 | 6.92 | 6.53 | 6.69 |
| Atomic transaction, batch 1000 | 11.52 | 10.76 | 30.70 | 7.29 | 6.93 | 6.84 |
| Independent autocommit, batch 1 | 177.50 | 174.48 | 3077.96 | 1.23 | 1.19 | 1.99 |

This workload excludes IRepository and secondary indexes and uses WriteThrough,
whereas the full repository matrix uses explicit flush. Do not transfer its write
ratios to full repository operations or subtract its costs from repository times.
First reads follow reopen with a warm operating-system cache and include JIT/
first-read effects. The raw report also retains checkpoint costs, storage sizes,
and maintenance/recovery outcomes. Runtime and dependency versions changed since
September, so this is historical continuity rather than a code-only A/B test.

The storage-core write/read medians remain within about 7% of September in these
rows. Its write advantage over SQLite remains: 2.85–11.12x across the atomic batch
sizes and 17.64x for independent saves. Those ratios exclude checkpointing.
For batch 1,000, adding one checkpoint after all 5,000 writes gives 19.19 ms for
SegmentCore versus 36.18 ms for SQLite (1.89x). These totals are sums of separate
medians, not separately sampled total-time medians. Checkpoint alone remains
slower for SegmentCore: 8.42 versus 5.48 ms. Its batch-1 first read is also slower:
13.48 versus 6.46 ms. The read and maintenance costs remain part of the comparison.

## Method, provenance and reproduction

- Windows 10.0.19045, Ryzen 9 3950X, Samsung 980 PRO NVMe;
  .NET SDK 10.0.401/runtime 10.0.12.
- SQLite 3.53.4; Microsoft.Data.Sqlite 10.0.12; Dapper 2.1.89.
  Local SQL Server 16.0.1000.6 / Developer Edition (64-bit), delayed durability **DISABLED**;
  Microsoft.Data.SqlClient 7.1.1.
- 1,000 records: three cache modes, three processes per build. 10,000 records:
  MetadataOnly, three processes per build. 50 measured queries after five warmups;
  2,000 existence calls after sixteen warmups; 1,000 indexed updates and deletes
  per configuration. All seeds are independent durable writes.
- SQL has Id, Age, Role+Age and Name indexes. SQLite query plans prove range and
  prefix index use. These are improved SQL controls relative to the historical
  production gate. SQL has no dedicated suffix/contains index.
- SegmentCore uses four stripes, mutation WAL and explicit flush. File retains
  durable publication. Background maintenance, notifications, watchers and
  telemetry are disabled for these measurements; none was removed from the product.
- SQL Server uses a pooled connection per operation. SQLite retains open
  connections. InterfaceDB update/delete adapters locate entities and preserve
  revision safeguards; SQL executes mutations by primary key without emulating
  InterfaceDB's application revision token.
- Processes ran sequentially; we did not schedule concurrent builds or benchmarks.
  Ordinary workstation activity was not fully controlled. No cold-media,
  physical power-cut, multi-client load or production percentile claim is made.
- Existing transitive PKCS package advisories NU1902/NU1903 from the benchmark build remain
  in the successful build logs; this task did not update dependencies.

Main harness SHA-256: `A346AD8B15CA620AD88920998845ACD2DE8CA4FD8E642EF05DDC0248EEAE7431`.

| Production assembly | Before SHA-256 | Current SHA-256 |
| --- | --- | --- |
| Idb.Libraries | 3A6480B21BB39EB4532987822A1D1C9B5D06AFE2B91530F46C09D1D7F6D6923C | 6E134417E1586AA0AD3626AB814610CF70201E4807665B8B27B05871E60E259C |
| SegmentCore | 632669C733B063D631BEFD0BACD5B6A10E3D23EE9091F09F0D20EC9E330DFC8A | 13C24F0FF7F639232ADCE5C8BF758C9E9E67E659E49FDFDB43BEE04B246E21B9 |

The final source includes additional opt-in ordinal probe switches added after the
main matrix was frozen; the main matrix and ordinal probe each retain their own
benchmark assembly hash. Production hashes are asserted equal between them.
Raw samples, immutable binaries, source snapshots and the validated summary are
retained locally under `.codex-sql-comparison-20261020-v3` in the historical
artifacts workspace. Earlier preliminary directories are excluded.

Reproduction commands and detailed boundaries:
[SQL comparison guide](../StorageBenchmarks.Transactions/ProductionGate/SQL_COMPARISON.md).
The main summarizer validates all twelve reports, matching dependencies/options,
query plans, field contracts, and the three paired runs.

## Later review follow-up

The [review follow-up report](review-followup-20261020.md) records regression-tested
directory-publication and notification fixes, the new string-search analyzer,
and a separate comparison of SegmentCore `ExplicitFlush` and `WriteThrough`.
Its safety-change **before** build is this report's **current** build; the
experiments must not be combined into one before/after claim.
