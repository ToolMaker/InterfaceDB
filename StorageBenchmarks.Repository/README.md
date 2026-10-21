# Full-repository performance harness

This standalone, non-packable executable measures the complete FileSystem and SegmentCore repository, index, envelope and storage path. It intentionally has no SQLite control.

For the full repository comparison with SQLite and SQL Server, see the separate
[SQL comparison runner](../StorageBenchmarks.Transactions/ProductionGate/SQL_COMPARISON.md).

```powershell
dotnet run -c Release --project StorageBenchmarks.Repository -- --smoke --records 200 --batch 32
dotnet run -c Release --project StorageBenchmarks.Repository -- --run --records 2000 --iterations 7 --warmup 2 --batch 128 --payload-bytes 512 --output artifacts/repository-current.json
```

Run timed processes alone, use new output filenames, and alternate baseline/current order for credible comparisons. Every result consumes all fields and payload bytes and verifies count/checksum; final durable reopen is checked. Smoke mode records no elapsed-time or allocation measurements.

All current providers return independent result objects, including cache hits. StrongObject avoids storage reads while materializing caller copies from committed bytes. WeakObject is opportunistic and explicitly collected before read measurements, with a separate externally pinned case. A live hit materializes one caller copy; a miss can additionally materialize a private cache copy. Forced GC does not guarantee identical temporary-root lifetimes on every platform.

Repository-open includes DI, recovery and index initialization, not merely opening a connection. It is a lifecycle operation, not a per-query cost. The default matrix has 56 cases across two providers and three cache modes. Files use durable per-object proposal publication; SegmentCore uses mutation-WAL transactions and explicit flush. Their commit units differ.

The report records options, assembly hashes, all samples, medians, nearest-rank p95/p99 and process-wide allocations. Five or seven samples do not establish production tail guarantees. The OS page cache is not evicted, and no cold-media claim is made. Source data and field mutations for saves are prepared outside timing; encoding, commit and consumption of returned complete objects are inside timing.

See [preview.6 measurements](../documentation/performance-preview6-20260905.md) for the retained development snapshot and limitations.

## Storage-review baseline

`--review-baseline` measures exact point searches, ten-record ranges, physical existence and individual indexed-field updates. It uses the same repository configuration and model as this executable, with a serializer decorator supplied through DI to count revision reads and materializations. Every operation checks its result; each case ends with a full checksum/count check after reopening the durable store.

```powershell
dotnet run -c Release --project StorageBenchmarks.Repository -- --review-baseline --smoke --records 32 --engines SegmentCore
dotnet run -c Release --no-build --project StorageBenchmarks.Repository -- --review-baseline --run --records 50000 --engines SegmentCore --iterations 30 --warmup 5 --update-limit 10000 --output artifacts/review-50000.json
./Scripts/Measure-RepositoryReview.ps1 -BaselineDirectory baseline-bin -CurrentDirectory current-bin -OutputDirectory artifacts/review-comparison
python Scripts/summarize_repository_review.py artifacts/review-comparison --output artifacts/review-summary.json
```

The comparison script runs three separate processes per build and record count (2k, 10k, 50k), alternating order. Supply immutable Release output directories with the same harness and dependencies and the intended core/provider assemblies. Reports fingerprint those assemblies. Do not run builds, other benchmarks or test suites concurrently.

Update measurements use MetadataOnly and 64 warmup saves, then one pass over up to 10,000 evenly spaced records. Thus 50k is the store size, not the number of updates; increase `--update-limit` for a full pass. Every update changes an indexed field. StrongObject and pinned WeakObject searches retain all seeded objects. The additional collected WeakObject cases reopen the repository and force GC before **each** query, outside timing; each query requires one deserialization per result plus at most one for an independent private cache copy. Reopening isolates these measurements from references held by prior operations, but temporary roots can survive collection. The count/checksum and durable-reopen checks remain mandatory. Current isolated providers search committed values; unsaved caller edits do not alter search results.

Each JSON contains raw samples, within-run p50/p95/p99, allocations and serializer counters. Summarize repeated processes separately from individual-operation percentiles. The workload has 512-byte payloads by default and normal segment sizing; it is not numerically interchangeable with the external review's small-segment test fixture. Report any observed regressions and allocation tradeoffs alongside improvements. No timing thresholds are imposed on correctness tests.

Collected WeakObject uses a fresh repository scope per query; the other query cases reuse a warm scope. Opening/disposal are outside timing, but the query lifecycle still differs. Use those results to compare identical baseline/current cases, not to attribute every cross-case difference solely to cache mode.
