# Full-repository performance harness

This standalone, non-packable executable measures the complete FileSystem and SegmentCore repository, index, envelope and storage path. It intentionally has no SQLite control.

```powershell
dotnet run -c Release --project StorageBenchmarks.Repository -- --smoke --records 200 --batch 32
dotnet run -c Release --project StorageBenchmarks.Repository -- --run --records 2000 --iterations 7 --warmup 2 --batch 128 --payload-bytes 512 --output artifacts/repository-current.json
```

Run timed processes alone, use new output filenames, and alternate baseline/current order for credible comparisons. Every result consumes all fields and payload bytes and verifies count/checksum; final durable reopen is checked. Smoke mode records no elapsed-time or allocation measurements.

MetadataOnly creates fresh result objects; StrongObject measures an object-cache hit path. WeakObject is opportunistic and explicitly collected before read measurements, with a separate externally pinned case. Do not label cache-hit results as fresh materialization.

Repository-open includes DI, recovery and index initialization, not merely opening a connection. It is a lifecycle operation, not a per-query cost. The default matrix has 56 cases across two providers and three cache modes. Files use durable per-object proposal publication; SegmentCore uses mutation-WAL transactions and explicit flush. Their commit units differ.

The report records options, assembly hashes, all samples, medians, nearest-rank p95/p99 and process-wide allocations. Five or seven samples do not establish production tail guarantees. The OS page cache is not evicted, and no cold-media claim is made. Source data and field mutations for saves are prepared outside timing; encoding, commit and consumption of returned complete objects are inside timing.

See [preview.6 measurements](../documentation/performance-preview6-20260905.md) for the retained development snapshot and limitations.
