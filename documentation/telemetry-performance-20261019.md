# Telemetry measurements and validation — 19 October 2026

These are development measurements on Windows x64, .NET 10.0.12 / SDK 10.0.401, Release builds.
They are not mobile, cloud, sustained-load or tail-latency qualification. The unchanged pre-telemetry
assemblies were preserved before source edits; their module IDs differ from the measured new build.
Every measured read validates its result. Three process runs per mode were alternated in order;
tables show the median of the three run summaries. OS caches were not evicted. This was a shared
development machine; nearby build activity and scheduling noise limit small timing comparisons.

## Final policy check

After the refinements, three fresh process runs per mode alternated disabled telemetry with the
`recommended` profile: metrics, repository traces at 1%, TCP/storage spans off, warning/error
logging enabled. Workloads and queue capacity match the initial measurements below. Tests and
builds had finished before these runs. Values are medians of the three run summaries.

| Workload | Disabled median (ms) | Recommended median (ms) |
| --- | ---: | ---: |
| TLS TCP durable write + verified read | 1.2859 | 1.3520 |
| File cached exact first match | 0.0184 | 0.0244 |
| SegmentCore cached exact first match | 0.0167 | 0.0239 |
| File scan 128 objects | 0.5174 | 0.5351 |
| SegmentCore scan 128 objects | 0.5146 | 0.5266 |
| File durable batch of 16 | 16.0118 | 16.1107 |
| SegmentCore durable batch of 16 | 3.6643 | 3.6080 |

TCP allocated bytes per pair were 142,329 disabled and 146,571 recommended (about 3% more).
Median pair latency rose about 5.1%; individual recommended-run medians ranged from 1.2667 to
1.4191 ms, so these are development estimates. Small apparent improvements are measurement
noise, not a performance benefit. Cached reads added roughly 6–7 microseconds; reducing trace
sampling does not remove the observation/metrics cost. Null telemetry remains the cheapest path.

All runs had zero telemetry drops/export failures. Successful workloads produced zero log entries.
Native recommended runs emitted 4, 7 and 4 spans. The raw encoded TCP harness has no repository
facade and correctly emitted zero spans with TCP tracing off. These runs do not measure the cost
of frequent failures or external exporters. The historical measurements below remain unchanged.

## Initial profiles

- **Baseline:** pre-change binaries, no telemetry.
- **Disabled:** new binaries, telemetry options null.
- **Metrics:** bounded dispatcher, in-process counter/histogram listeners, no tracing/log output.
- **Full (initial measurement):** the same metrics plus 100% TCP/repository trace sampling and
  formatted lifecycle logs; storage tracing off. This measured the initial implementation.
  The final policy uses repository traces at 1%, TCP spans off, and warning/error logs only.
  Historical results below are retained; they are not measurements of that final policy.

Enabled benchmarks used capacity 65,536 to include exporter costs without saturation. All enabled
runs reported **zero dropped records and zero exporter failures**. Listeners counted observations
in memory; they did not export over a network or write a log file. The separate blocking-consumer
test exercises the drop policy. These results do not measure an external exporter's overhead.

## TLS TCP with persistent SegmentCore

512 measured pairs of a 512-byte durable write followed by a verified read, after 100 warmup pairs.
Client and host run in one process over loopback with mutual TLS. One pair is two TCP operations.
The allocation interval includes background dispatcher drain at the end, so it includes that work.

| Profile | Pair median (ms) | Pair p95 (ms) | TCP operations/s | Allocated bytes/pair |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 1.275 | 1.648 | 1,567 | 143,031 |
| Disabled | 1.261 | 1.517 | 1,620 | 143,192 |
| Metrics | 1.319 | 1.739 | 1,431 | 147,607 |
| Full | 1.337 | 1.704 | 1,459 | 152,311 |

Relative to baseline, full instrumentation added approximately **0.062 ms (4.9%) to median pair
latency** and **9,279 bytes (6.5%) per pair**; measured throughput was 6.9% lower. Metrics-only
median latency rose about 3.5%, allocations about 3.2%. Throughput differences between the two
enabled modes are not evidence that tracing improves performance. Disabled allocation increased
by about 161 bytes per pair (0.11%); its small measured speed improvement is not a claimed benefit.

## Native repository

128 complete objects with 512-byte payloads; StrongObject cache; 21 samples per operation after
five warmups; durable storage; unchanged-index batches of 16. Background maintenance and watching
were disabled. File bulk writes and SegmentCore transactions have different commit boundaries.

Median milliseconds per operation:

| Engine / operation | Baseline | Disabled | Metrics | Full |
| --- | ---: | ---: | ---: | ---: |
| File exact first match | 0.0188 | 0.0172 | 0.0239 | 0.0237 |
| SegmentCore exact first match | 0.0178 | 0.0167 | 0.0221 | 0.0248 |
| File scan 128 objects | 0.5146 | 0.5166 | 0.5370 | 0.5299 |
| SegmentCore scan 128 objects | 0.5030 | 0.4928 | 0.5131 | 0.5232 |
| File durable batch of 16 | 15.8604 | 16.3043 | 15.9814 | 16.5480 |
| SegmentCore durable batch of 16 | 3.5851 | 3.6507 | 3.6437 | 3.7034 |

Mean allocated bytes per operation (median of run means):

| Engine / operation | Baseline | Disabled | Metrics | Full |
| --- | ---: | ---: | ---: | ---: |
| File exact first match | 3,575 | 3,575 | 4,351 | 5,436 |
| SegmentCore exact first match | 3,448 | 3,448 | 4,224 | 5,400 |
| File scan 128 objects | 3,864 | 3,864 | 4,640 | 5,816 |
| SegmentCore scan 128 objects | 4,056 | 4,056 | 4,832 | 6,008 |
| File durable batch of 16 | 150,633 | 152,044 | 166,322 | 167,282 |
| SegmentCore durable batch of 16 | 75,784 | 76,933 | 78,638 | 82,001 |

Very fast cached queries show a noticeable relative cost: full instrumentation adds roughly 5–7
microseconds in these samples. Null telemetry preserves the cached-query allocation count. Native
allocation counters are process-wide over each call, while dispatcher work is asynchronous; some
export allocations can fall into a neighboring interval. Treat those per-case numbers as estimates,
not exact allocation attribution. The drained TCP trial provides the stronger total-cost measurement.

## Reproduce

Use `StorageBenchmarks.Repository` for the native workload:

```powershell
$env:INTERFACEDB_BENCH_TELEMETRY = 'recommended' # disabled, metrics, recommended, full
dotnet run --project StorageBenchmarks.Repository -c Release -- --run --records 128 --iterations 21 --warmup 5 --batch 16 --payload-bytes 512 --read-concurrency 2 --engines FileSystem,SegmentCore --cache-modes StrongObject --output repository-metrics.json
dotnet run --project StorageBenchmarks.Telemetry -c Release -- tcp-metrics.json
```

The TCP harness accepts its report path as the first argument. For an old-binary baseline, build
the same harness with `-p:TelemetryBuild=false -p:BaselineDirectory=<preserved transport test output>`
into a separate output folder. Its baseline code has no diagnostics dependency. Run that saved
program without rebuilding. Preserve the original native benchmark executable as well. Reports
include module IDs; native reports also include assembly hashes, detailed samples and configuration.
Alternate process order and keep the machine idle for stronger evidence. Do not mix measurements
with active benchmarks, tests, builds or deployment work.

## Correctness validation

After the final policy refinements: **331 core, 184 public TCP and 198 SegmentCore tests passed**,
with four Windows platform skips across core/SegmentCore and no failures. These supersede the
initial public validation counts below. Added cases verify the 1% default and TCP opt-in, no
success/cancellation logs, logging independent of sampling, minimum severity, one log with metrics
at both boundaries for a rethrown exception, lazy query failures, and callback failure after a
successful durable save. The runnable host and both benchmark projects build without warnings.

- Core repository suite: **329 passed**, two case-sensitive-filesystem cases skipped on Windows.
- Public TCP suite: **176 passed**, including seven telemetry isolation/privacy/DI/TLS cases.
- Final targeted TCP checks: **nine passed**, adding null-option, original-method-entry and remote
  failure-category checks.
- SegmentCore suite: **197 passed** plus two platform skips initially; its API snapshot failed for
  the new optional property. Removing exactly `SegmentCoreOptions.Telemetry` from the snapshot
  reproduced the old approved hash. After that additive review, the API gate passed: **198 tests
  passed in aggregate**, with no unresolved failure.

The reviewed SegmentCore change is additive; storage and wire formats remain unchanged. Original
encoded-store/versioned-host method signatures remain as forwarding overloads for binary callers.
Tests cover blocked/throwing consumers, queue saturation, bounded shutdown, original exception
identity, lazy queries, safe labels, excluded baggage/tracestate, File/SegmentCore composition and
mutual-TLS CRUD. The feature remains opt-in and has not been published to NuGet.
