# Review follow-up — 20 October 2026

## What changed and why

This follow-up implements confirmed correctness and diagnostic gaps from the
developer's re-review. It is source work after preview.11, not a NuGet release.
Performance proposals are measured before changing the corresponding algorithms.

### Subsequent review correction: directory permissions and analyzer scope

The first directory-publication fix flushed every ancestor on POSIX systems.
That was a regression: a process can traverse an ancestor without permission to
open it for reading, which directory `fsync` requires in this implementation.
The earlier native tests used ordinary readable directories and did not catch it.

The corrected implementation publishes only newly created directories and their
containing directories, stopping at the nearest existing containing directory.
Existing, owner-provisioned paths do not cause ancestor flushes. Failed barriers
remain pending on the same publication instance: a retry cannot acknowledge that
directory or a descendant until the failed barrier succeeds.

Ten regression cases fail against the frozen pre-correction library and pass
with the corrected library on non-root Linux. Eight cover File and SegmentCore,
Performance and Durable modes, and existing and newly created store roots under
an enter-only ancestor. Each saves, closes, reopens, reads and saves again. The
other two check the publication boundary and failure/retry behavior.
Android device validation remains outstanding; the Linux reproduction does not
establish that every Android environment was affected or has been qualified.

`IDB005` now follows the runtime's default string index modes: Exact and Prefix.
Default-index `EndsWith` calls do not warn; an explicit member Suffix/All index
is required for that warning. Type-level index modes only enable discovery and
do not replace member defaults. Four corrected expectations failed before the
analyzer fix; the full analyzer suite now has 31 passing tests.

`CommitJournalFlushFailure_FaultsScopeAndRequiresReopenForOutcome` now targets the
commit journal's file signature instead of a global flush count. It checks that
the journal-specific exception occurs exactly once, faults the transaction and
store, and preserves all-or-nothing recovery. It runs with one and four stripes.
The production engine's shutdown error reporting has not changed.

Fresh full public core, SegmentCore and analyzer suites: **609 passed, 12 skipped
on Windows; 619 passed, 2 skipped on Linux**. Linux ran as UID 10001 with a
two-CPU limit, so the permission tests actually enforced access restrictions.
Eight Windows skips are the new Linux-only permission cases; the remaining skips
cover existing OS-specific behavior. The journal test also passed ten separate
two-CPU Linux runs (20 cases across the two stripe configurations).
Authentication and transport suites were not repeated for this correction.
Logs and TRX results are retained in `.codex-directory-boundary-20261020` in the
historical artifacts workspace.

The benchmark results and assembly hashes below describe the earlier frozen
builds, before this correction. This correction makes no new performance claim.

### Independent re-review received on 20 October 2026

The reviewer reports that all 16 original findings and all subsequent findings
requiring code changes are resolved on the latest uncommitted working tree.
Their separate Linux checks confirm the enter-only-directory reproduction and
all ten boundary/permission cases as a non-root user, all 31 analyzer tests,
and the journal test in 12 CPU-loaded runs (24 cases). Their runtime check also
confirms that default-index `EndsWith` still scans, supporting removal of the
incorrect warning. Their original and later edge-case probes remain passing.
These are reviewer-supplied results, separate from the local runs recorded above.

No additional code fix was requested by this re-review. Remaining qualification
includes an Android device, physical power loss, more than 100,000 records and
representative concurrent-client/process workloads within each provider's
documented ownership model. Existing parallel-operation regression tests do not
replace that workload qualification. Read-performance experiments remain separate.
The accepted limitations are live StrongObject cache rechecks, index rebuilding
through reopen, and the reflected getter's `TargetInvocationException` diagnostic.

### Baseline commit checks

Before committing this snapshot, the public authentication suite was rebuilt and
rerun in Release (61 passed), as was the public transport suite (184 passed).
Neither had failures or skips. These runs use the final directory correction;
their logs and TRX results are in `.codex-baseline-commit-20261020` in the historical
artifacts workspace. No production code changed during this commit preparation.

This snapshot is a local baseline for subsequent measured optimization work.
Package publication remains gated on Android-device and physical power-loss
qualification, as agreed for this snapshot. A local commit does not satisfy
those publication gates.

| Finding | Change | Verification |
| --- | --- | --- |
| New File directories were created without the configured durable publication step | Added `IFilePublication.EnsureDirectory`; the repository, coordinator, watcher and revision clock use it before acknowledging durable work | Injected ancestor-barrier failure prevents payload preparation/revision issue; retry succeeds; native nested-path checks run on Windows and Linux |
| Early watcher resolution could create the root before publication | Path-case discovery probes an existing ancestor; watcher startup uses the injected publication service | An additional regression failed before this ordering fix and passes afterward |
| Legacy subscribers were silently detached on failure | Callback/keep-alive failures go through configured telemetry with failure logging; other subscribers continue | Failure injection checks delivery, persistence and the telemetry outcome |
| Losing an index-maintenance subscription could leave stale index plans enabled | Disable index plans and report the reason through `ExplainSearch`; use authoritative scans until reopen | A physical write after forced subscription loss remains searchable |
| Missing File envelope location produced a misleading `Path.Combine` exception | Check the location before allocating its identity | The error names the missing location and no payload is written |
| Culture-sensitive indexed prefix/suffix searches were easy to overlook | Added compiler warning `IDB005` for supported inline search predicates | Analyzer tests cover ordinal/non-ordinal calls, index modes, excluded/encrypted members and unrelated APIs |
| Generic `QueryAsync` upgrade guidance omitted the explicit implementation constraint | Documented `ValueTask<TImplementation?>` and `where TImplementation : class` | Documentation checked against the interface |

Nine initial regression cases failed against the original production code before
the fixes were applied. The early-watcher test then exposed a second initialization
path, which was fixed before the final measurements. Existing constructor
signatures remain available. The directory-publication capability has a default
interface implementation so existing custom implementations remain compatible.

No query result semantics, mutable cache semantics, revision protection,
transaction behavior or default durability strategy were removed to obtain
performance numbers. The analyzer does not automatically change string comparison
semantics. Logging uses the configured diagnostics service.

## Original follow-up verification (before the correction above)

Final public Windows suites: **843 passed, 4 skipped**, covering core, analyzers,
SegmentCore, authentication and public transport.

Final public Linux storage/analyzer suites: **600 passed, 2 skipped**, covering
core, SegmentCore and analyzers. The integration child process used its Linux
apphost with the current managed assemblies. Network authentication/transport
suites were not repeated on Linux in this follow-up.

The skips exercise operating-system-specific filesystem behavior. The new native
directory publication and injected failure/retry checks pass on both systems.
These are software checks, not physical power-cut qualification.

## Performance experiments

The repeated measurements use frozen Release binaries, the same harness and
dependencies, fresh processes, alternating label order and reversed engine order
in round two. There are no concurrent scheduled builds or tests. Normal workstation
activity remains a source of variation.

1. **Safety changes:** three paired processes at 1,000 records, MetadataOnly,
   50 measured calls per query, 256 updates and deletes. File and SegmentCore.
   Here, **before** is commit `0b567f275ca92b26c9601a46af8ea28e10dee511`, which is
   the **current** product build in the earlier SQL report. Only the core and
   SegmentCore production DLLs differ between labels.
2. **Flush configuration:** three paired processes at 10,000 records,
   MetadataOnly, 50 measured calls per query, 1,000 updates and deletes.
   Identical current product binaries; SegmentCore `ExplicitFlush` versus
   `WriteThrough`. SQLite direct, SQLite Dapper and SQL Server remain unchanged
   controls. File is excluded from this particular experiment.
3. **Range profile:** a separate sampled trace of 2,000 range queries returning
   948 complete fresh objects each. Seeding, initialization and validation also
   appear in the process trace and must be distinguished from the query path.
   Profiled timings are not substituted into the unprofiled comparison.

Every timed query validates full-object count and checksum. Mutation runs verify
the complete dataset and reopen it. Results use medians of three process p50s and
retain all three paired directions; this is descriptive evidence, not a statistical
equivalence test or a production latency guarantee.

An earlier partial run against the first fix was stopped when the watcher ordering
gap was discovered. Its completed runs and outliers are retained separately. It is
excluded because the product changed, not because its timings were inconvenient.

### Effect of the safety changes

Milliseconds per operation; median of three process p50s, at 1,000 records.

| Operation | File before | File fixed | SegmentCore before | SegmentCore fixed |
| --- | --- | --- | --- | --- |
| Independent insert | 5.2267 | 5.3504 | 3.1183 | 3.1281 |
| Indexed-field update | 5.6491 | 5.7451 | 3.2645 | 3.1970 |
| Delete by id | 1.9319 | 1.9958 | 3.1803 | 3.1611 |
| Range, 90 objects | 2.0242 | 1.9736 | 0.6615 | 0.6914 |
| Broad conjunction, 324 objects | 6.5079 | 6.4674 | 1.9131 | 1.9675 |

Across all 24 engine/operation cases, the absolute change in the median was at
most 5.4%. This does not demonstrate a substantial performance improvement or
regression. Individual process differences remain visible: SegmentCore's range
p50 in round two was 1.1301 → 1.3398 ms, whereas round one was
0.6615 → 0.6592 ms. Three process medians do not prove equivalence, nor do
these 1,000-record runs establish scaling or concurrent-write behavior.

### Effect of the flush configuration

All six 10,000-record processes passed. Each cell shows the median for the
ExplicitFlush-labelled processes → the WriteThrough-labelled processes, in
milliseconds. **Only SegmentCore's configuration changes**; the other engines
are controls with identical settings in both columns of each pair.

| Engine | Independent insert | Indexed-field update | Delete by id |
| --- | --- | --- | --- |
| SegmentCore | 3.0938 → 0.2099 | 3.1557 → 0.2220 | 3.1039 → 0.1919 |
| SQLite direct | 3.1065 → 3.1525 | 3.1798 → 3.1398 | 3.1115 → 3.1029 |
| SQLite Dapper | 3.1008 → 3.1118 | 3.0814 → 3.1012 | 3.0884 → 3.0855 |
| SQL Server | 0.2119 → 0.2221 | 0.2158 → 0.2291 | 0.1862 → 0.1837 |

SegmentCore write latency fell **93.0–93.8%**, with a reduction in every pair for
each operation. Its WriteThrough medians are in the same range as these SQL
Server controls. This supports the reviewer's interpretation: the previous write
ranking depended strongly on flush configuration. These are existing configuration
choices in the same production binary, not a new engine optimization.

The default remains unchanged. Both modes retain the software commit/recovery
checks, but this experiment does not establish equivalent physical power-loss
behavior on the tested filesystem/device. `ExplicitFlush` requests a file flush;
the actual device/cache behavior must not be inferred solely from its latency.

Broad reads still trail the SQL controls:

| Range query, 948 full objects | ExplicitFlush-labelled runs | WriteThrough-labelled runs |
| --- | --- | --- |
| SegmentCore | 11.7604 ms | 10.8937 ms |
| SQLite direct | 1.5611 ms | 1.6292 ms |
| SQLite Dapper | 1.6871 ms | 2.2318 ms |
| SQL Server | 2.9907 ms | 3.2663 ms |

These read differences are not an implemented read optimization. Even the
unchanged controls vary: Dapper's range median was 32.3% higher in the second
label, with mixed paired directions. Its round-two p50s were 3.0886 → 2.2318 ms;
round three was 1.6335 → 2.3018 ms. Those values remain included.

Do not compare these absolute read medians directly with the earlier full SQL
matrix as a code-regression test: this experiment omits File, changing engine
execution and warmup context. The direct before/fixed comparison above uses the
same engine list and options for both product builds.

### Range profile: evidence for the next experiment

The separate 2,000-query trace passed all 948-object count/checksum checks and
the final reopen check. It shows asynchronous file-read scheduling, read
continuations, encryption-context construction (including hashing and assembly
metadata access), candidate sorting and the benchmark's object-checksum work.
The current workload still allocates about **2.2 MB per 948-object query**, measured
in the unprofiled flush-mode runs.

The whole-process profile includes idle thread-pool and I/O-poller threads,
startup, seeding and final validation. Async continuations may lack the original
query caller on their sampled stack. Consequently, neither whole-process
percentages nor caller-filtered samples are a precise breakdown of query latency.
The trace does not prove that MessagePack decoding is the dominant cost.

A useful next isolated experiment is to measure repeated immutable contract
metadata preparation and per-record read scheduling, preserving encryption-context
bytes, ordering, cancellation and result semantics. Candidate collection and
serialization changes should likewise earn their place through a paired test.
No production optimization of those paths is included in this follow-up.

## Remaining proposals

- Use the range profile to choose an isolated before/after trial before changing
  candidate collection, record lookup, payload copying or deserialization.
  A lower-level workload cannot establish the improvement of a repository-layer change.
- Keep mutable StrongObject cache checks unless a separate, explicit saved-values
  or copy-on-read contract is designed. Removing those checks would change results.
- Rebuilding indexes in place remains a separate lifecycle/API feature; the
  supported recovery path is coordinated dispose and reopen.
- A throwing reflected index getter still reaches the caller wrapped in
  `TargetInvocationException`; improving the property-specific diagnostic remains
  follow-up polish. The save is rejected before writing.
- Million-record memory/startup measurements, concurrent-writer measurements and
  physical power-cut qualification remain outside this round.
- The September-to-October File insert difference is historical, not an isolated
  measurement of directory publication. The safety comparison here does not
  remove publication barriers to estimate their cost.
- The benchmark build retains its existing transitive
  `System.Security.Cryptography.Pkcs` 9.0.18 NU1902/NU1903 advisories. Dependencies
  were held constant for the comparison; package remediation is separate work.

## Evidence and reproduction

Frozen binaries, raw samples, TRX files, failing pre-fix regressions, source
snapshots and the range trace are retained under
`.codex-review-followup-20261020-v2` in the historical artifacts workspace.
The original partial run remains in `.codex-review-followup-20261020`.
Only the v2 repeated processes contribute to this report.

| Assembly | Before SHA-256 | Fixed SHA-256 |
| --- | --- | --- |
| Idb.Libraries | 6E134417E1586AA0AD3626AB814610CF70201E4807665B8B27B05871E60E259C | CEB5A19FA01FC704D2D5C1E5437E9EC2BF7987F7E764D070B6D983B841931EEC |
| SegmentCore | 13C24F0FF7F639232ADCE5C8BF758C9E9E67E659E49FDFDB43BEE04B246E21B9 | B221C673239D78480F0DAC8FCCE66B5BE7972940B4A833462685E572EAEA3E7E |

Both labels use benchmark assembly
`D637237495DC897A2480B170BB9C9D3AAFDAED6BC4E9F7342548ED9B678392D3`.
All flush-mode processes use the fixed product DLLs. The JSON reports record
runtime, OS, dependencies, query kinds, SQL settings and every latency sample.

For each frozen build, run the safety workload three times:

```powershell
dotnet before-bin/StorageBenchmarks.Transactions.dll --sql-comparison --records=1000 --iterations=50 --mutation-limit=256 --cache=MetadataOnly --only-interfacedb --label=before-r1 --output=artifacts/safety-performance/before-r1.json
dotnet current-bin/StorageBenchmarks.Transactions.dll --sql-comparison --records=1000 --iterations=50 --mutation-limit=256 --cache=MetadataOnly --only-interfacedb --label=current-r1 --output=artifacts/safety-performance/current-r1.json
```

Use current/before order and add `--reverse` in round two; use before/current
order in round three. Keep distinct `-r2`/`-r3` labels and output paths.
The retained local `run-measurements.ps1` automates the exact sequence.

```powershell
./Scripts/Measure-RepositoryFlushModes.ps1 -BinaryDirectory current-bin -OutputDirectory artifacts/flush-modes -IncludeSqlServer
python Scripts/summarize_repository_followup.py artifacts
```

The summarizer rejects failures, differing fixture/options/dependency contracts,
overlapping benchmark processes, missing SQL controls, or unintended production
DLL differences. It retains p50s, p95s, allocation and paired directions.

See [upgrade guidance](storage-correctness-upgrade.md),
[the earlier SQL comparison](performance-sql-20261020.md) and
[reproduction commands](../StorageBenchmarks.Transactions/ProductionGate/SQL_COMPARISON.md).
