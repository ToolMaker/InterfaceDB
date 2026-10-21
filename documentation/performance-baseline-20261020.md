# Committed baseline performance check — 20 October 2026

## Result and decision

Keep the reviewed correctness fixes. This comparison found no greater-than-10%
change in the main 10,000-record File or SegmentCore operation medians. It does
not establish a new speed improvement or prove performance equivalence.
There are costs and uncertainties to retain: slightly higher File initialization
times, higher File tail latencies in two cases, and variable short WeakObject
query timings.

The complete matrix passed **12 processes, 84 engine/cache configurations and
1,008 measurement rows**. All timed queries checked full-object count and checksum;
mutations were checked against complete snapshots, including after reopen.
Six additional focused WeakObject processes also passed. These are benchmark
correctness checks, not a fresh run of every unit/integration suite.

No production code or runtime setting was changed for this experiment.

## Versions and comparable conditions

- Before: `0b567f275ca92b26c9601a46af8ea28e10dee511`.
- Current: `fde3b5392b006cb7c87f0147215a43ac65d249be`.
- Both already include the earlier read/existence optimizations and original
  review corrections. This measures the subsequent safety/diagnostic follow-up.
- The before source was rebuilt from an unmodified Git archive. Both directories
  use the current benchmark and dependencies; only the core/provider DLLs and
  their PDBs differ. Assembly hashes were checked against every report.
- Release, SDK 10.0.401, .NET 10.0.12, Windows 10.0.19045; SQLite 3.53.4 and local
  SQL Server 16.0.1000.6 Developer Edition.
- 1,000 records in MetadataOnly, StrongObject and WeakObject modes; 10,000
  records in MetadataOnly. Three fresh processes per version and size.
- Six query shapes, five warmups and 50 timed iterations per query. Up to 1,000
  timed updates/deletes; one insert per operation. Full objects are materialized.
- File Durable publication and SegmentCore **ExplicitFlush** in both versions.
  SQLite uses WAL/FULL; SQL Server delayed durability is disabled.
- Build/size ordering alternates; engine order reverses in round two. Processes
  are sequential, with no concurrent build/test scheduled by this task.

### Restart handling

Windows restarted at **18:49:20 SAST**. Four completed pre-restart runs were
preserved separately and excluded. An incomplete run stopped shortly before
shutdown and produced no complete report; its log is also preserved.

All 12 final runs occurred in one subsequent boot session, **19:42–20:15 SAST**,
starting over 52 minutes after boot. Actual round order was 2, 1, 3, retaining
each round's original build/size order. Boot timestamps, exit codes, non-overlap,
options and hashes were validated. The six focused runs followed at 20:15–20:17.
The restart boundary is therefore controlled; normal workstation, cache, JIT,
GC and scheduling variability remains.

## Main before/current results

10,000 records, MetadataOnly. Values are the median of three process p50s,
**milliseconds per operation**. Negative changes are faster.

| Operation | File before → current | Change | SegmentCore before → current | Change |
| --- | ---: | ---: | ---: | ---: |
| Independent insert | 5.1720 → 5.2087 | +0.7% | 3.1120 → 3.0212 | −2.9% |
| Exact query, 1 object | 0.1419 → 0.1414 | −0.4% | 0.0163 → 0.0152 | −6.7% |
| Range, 948 objects | 19.5513 → 19.9785 | +2.2% | 7.5503 → 8.1395 | +7.8% |
| Prefix, 1,000 objects | 213.4193 → 209.2188 | −2.0% | 47.7309 → 44.6527 | −6.4% |
| Suffix, 20 objects | 209.5001 → 211.3453 | +0.9% | 42.8120 → 42.4310 | −0.9% |
| Contains, 39 objects | 0.8194 → 0.8255 | +0.7% | 0.3151 → 0.3301 | +4.8% |
| Conjunction, 3,251 objects | 68.7326 → 69.3465 | +0.9% | 19.3703 → 19.6573 | +1.5% |
| Indexed-field update | 5.4571 → 5.5834 | +2.3% | 3.0967 → 3.1529 | +1.8% |
| Delete by id | 1.8116 → 1.8800 | +3.8% | 3.0971 → 3.0935 | −0.1% |

Known-key existence remained 0.0002 ms in both providers. These tiny intervals
are near timer granularity and are not useful speedup claims.

Across both sizes and all cache modes, 77 of 80 non-lifecycle InterfaceDB cases
were within the descriptive ±10% band, including eight existence cases. One was
higher in all three pairs, and two had larger but mixed changes. Sixteen lifecycle
cases are reported separately. Direction agreement and a 10% band are screening
rules, not statistical significance or an equivalence test.

### Costs and variation that the medians do not show

- At 1,000 records, File empty initialization with StrongObject rose from
  **1.8645 to 2.7030 ms**, and WeakObject from **2.1628 to 2.7375 ms**. All three
  pairs increased. These are single lifecycle observations per process. The new
  durable directory creation is a plausible contributor, not an isolated cause
  established by this experiment.
- At 10,000 records, File exact-query p95 rose from **0.1723 to 0.3623 ms**, and
  update p95 from **6.1135 to 7.0116 ms**. Each pair's p95 increased, although
  the magnitude varied. Do not describe all latency as unchanged.
- Main 10,000-record read/write allocation medians varied from **−0.6% to +3.6%**.
  Some 1,000-record exact-query allocation figures varied more. These counters
  measure process-wide allocation during a workload, not retained memory per
  record, and do not identify the source of background allocation.
- The unchanged SQL Server exact-query control also rose **40.5%**
  (0.1131 to 0.1589 ms), in the same direction in every pair. SQLite prefix
  timings varied too. This limits causal claims from timing direction alone;
  it does not prove that every InterfaceDB difference is machine noise.

## Focused WeakObject check

The original 1,000-record File WeakObject broad query rose from **0.1974 to
0.3041 ms (+54.1%)**. Pair changes were +54.1%, +3.1% and +46.6%.
The original SegmentCore WeakObject range median rose 97.3%, but only one pair
was slower. File WeakObject prefix was 20.8% lower with mixed directions.
All three original observations remain in the main results.

To investigate, six more processes ran both public providers in WeakObject mode
with all six queries, **1,000 timed iterations per query**, the same binaries,
durability, default JIT/GC, alternating labels and reversed round-two engine
order. SQL and timed update/delete workloads were omitted. This is a separate
diagnostic workload, not a replacement for the original matrix.

| Case | All 1,000 iterations, before → current | Last 500 iterations, before → current |
| --- | ---: | ---: |
| File broad query | 0.3474 → 0.36225 ms (+4.3%) | 0.2717 → 0.2759 ms (+1.5%) |
| SegmentCore range | 0.0434 → 0.0438 ms (+0.9%) | 0.0432 → 0.0435 ms (+0.7%) |

One longer File process still had a +50.7% whole-run difference; its later half
was only +0.8%. The other later-half File pair changes were +1.5% and −1.9%.
The later-half window was used as a diagnostic of settling behavior; every early
sample remains retained. It must not be presented as the original workload.

These checks did not reproduce a sustained large regression. Timings varied
across processes and within a run; these checks do **not** identify JIT, GC, weak-cache
occupancy or another mechanism as the cause. Short-burst predictability and File
tail latency remain profiling questions. No speculative product fix was made.

## Relationship to SQL

Current 10,000-record medians in milliseconds:

| Operation | SegmentCore | SQLite direct | SQLite Dapper | SQL Server |
| --- | ---: | ---: | ---: | ---: |
| Independent insert | 3.0212 | 3.1522 | 3.1250 | 0.2196 |
| Exact query | 0.0152 | 0.0226 | 0.0269 | 0.1589 |
| Range, 948 objects | 8.1395 | 1.5970 | 1.7405 | 3.0106 |
| Prefix | 44.6527 | 1.1985 | 1.5481 | 3.7931 |
| Suffix | 42.4310 | 1.0316 | 1.0386 | 15.8086 |
| Contains | 0.3301 | 0.9243 | 0.9234 | 24.0745 |
| Conjunction, 3,251 objects | 19.6573 | 4.1155 | 5.2095 | 4.3827 |
| Indexed-field update | 3.1529 | 3.1209 | 3.1959 | 0.2224 |
| Delete | 3.0935 | 3.0773 | 3.0886 | 0.1982 |

The previous broad picture remains: SegmentCore's independent ExplicitFlush
writes are close to SQLite's; SQL Server's write-through log has much lower
single-write latency here. Large result materialization remains slower than SQL.
No new write-through improvement is claimed: that separate experiment changed
the flush strategy, while this comparison holds it fixed.

Default InterfaceDB prefix/suffix methods are culture-sensitive scans; the SQL
prefix control is indexed. InterfaceDB's contains workload benefits from its
substring index; these SQL controls have no equivalent substring index.
These are workload/configuration comparisons, not general engine rankings.
See the [earlier comparison](performance-sql-20261020.md) for ordinal probes and
the [review follow-up](review-followup-20261020.md) for flush-strategy measurements.

## Evidence and next work

Evidence is retained in `.codex-committed-comparison-20261020` in the historical
artifacts workspace: archived baseline source, frozen binaries, `manifest.json`,
`matrix/`, `summary.json`, `pre-reboot/`, `failed-attempts/`, boot/exit records,
`weak-cache-check/`, `weak-cache-summary.json`, and runner/validation scripts.
The identical harness SHA-256 is
`54C7AA539B7B58CD25A6941619758F9768E6386B9365164729118DECF1B22BF3`.
All repetitions and outliers are retained. The main matrix uses
[`Measure-RepositorySql.ps1`](../Scripts/Measure-RepositorySql.ps1);
the local runner adds boot/exit receipts after the interruption.

Next, use profiling to separate query warmup/GC/cache-state effects and File tail
latency before changing runtime code. Any proposed read optimization should have
its own matched before/current trial against this committed snapshot.
The already verified safety fixes need not be reverted to pursue that work.

This Windows experiment does not measure the POSIX permission-fix cost, commercial
Atlas/transport, Android, physical power loss, more than 100,000 records, or
representative concurrent-client/process workloads. Package publication remains
gated on the agreed Android-device and physical power-loss qualification.
