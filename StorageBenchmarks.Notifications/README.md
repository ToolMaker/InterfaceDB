# Repository notification cost benchmark

Standalone, non-packable .NET 10 benchmark for the free File and Segment Core providers.
It compares complete repository operations and includes recipient materialization and callback
drain time. It is not a SQLite comparison, server benchmark or remote event-bus benchmark.

## Run current source

```powershell
dotnet build StorageBenchmarks.Notifications -c Release -o ..\notification-after-bin
dotnet ..\notification-after-bin\StorageBenchmarks.Notifications.dll --iterations 1000 --repetitions 5 --payload 512 --output ..\notification-after.json
```

Options: `--providers file,segment`, `--modes` (comma-separated names below), `--iterations`,
`--repetitions`, `--payload` (bytes), `--output` (JSON path). The runner discards an initial
warm-up and rotates case order. Run it without other benchmarks/tests competing for CPU or disk.
Results are samples on the host, not statistically guaranteed performance bounds.

| Mode | Operation / subscriptions |
| --- | --- |
| disabled | Update, feature disabled |
| empty | Update, enabled with zero subscribers |
| create-off / created | Insert, disabled / one Created subscriber |
| delete-off / deleted | Delete, disabled / one Deleted subscriber |
| noop-off / noop | Identical save, disabled / one Changed subscriber |
| changed | Update, one matching Changed subscriber |
| filtered | Update, one Changed subscriber whose predicate rejects every event |
| changed10 / changed100 / changed1000 | Update with that many matching subscribers |

Seed/setup/repository initialization occur outside timings. Updates rotate through 128 records;
the first update for each has no retained notification baseline. Creates insert fresh records;
deletes have one preseeded record per operation. Record properties are Id (exact index), Value,
Sequence and Payload. Updates alter Value/Sequence; payload stays equal, exercising byte equality.
Warm-up is up to 150 operations; measured repetitions use the requested count.

Settings: File Durable and Segment Core Durable, WindowsNvme hardware profile, StrongObject
cache, no physical notifications/watchers/eager hydration, one write buffer, eight callback slots.
Segment background maintenance/dispose checkpoint are excluded. Subscription queue capacity is
raised to `iterations + 1`, subscriber limit to 1,100, to measure complete bounded bursts rather
than overflow policy. Tests separately verify default-limit overflow. Subscriber callbacks consume
the materialized entity; they do not do application work or additional storage writes.

JSON includes total save time, end-to-end time, remaining drain time, p50/p95/p99 save latency,
p95 notification latency for the first recipient (from save start, not commit), successful deliveries,
filtered counts, all-thread allocation deltas, sampled managed-heap growth, post-GC retained delta
and GC counts. New runs capture `Gen0Collections` before the explicit post-run retained-heap
measurement; the initial implementation report included that forced GC. Heap deltas are not exact
per-component retention and sampling can miss peaks.
Allocation includes runtime/scheduling noise. Every subscription must remain healthy and deliver
the expected number of events or the run fails. No-op and filtered cases expect no callbacks.

## Genuine before/after disabled control

Build the same driver against clean pre-feature source with the conditional baseline switch:

```powershell
git worktree add --detach ..\InterfaceDB-notification-baseline f748e1bc9678d2d1be2d66117ecf3930a0bfeb8e
dotnet build StorageBenchmarks.Notifications -c Release -p:NotificationBaseline=true -p:InterfaceDbSourceRoot=C:\path\to\InterfaceDB-notification-baseline -o ..\notification-before-bin
dotnet ..\notification-before-bin\StorageBenchmarks.Notifications.dll --modes disabled,create-off,delete-off,noop-off --iterations 1000 --repetitions 5 --output ..\notification-before.json
```

The baseline switch removes all references to new notification APIs, not storage behavior. It
only permits disabled/control cases. Source worktree remains unchanged; binaries/artifacts are
separate from the current build. Rebuild normally (with restore) when switching back, because
the driver project assets must resolve the intended source root.

## Isolate wrapper CPU cost and component work

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet ..\notification-before-bin\StorageBenchmarks.Notifications.dll --micro --output ..\notification-before-micro.json
dotnet ..\notification-after-bin\StorageBenchmarks.Notifications.dll --micro --output ..\notification-after-micro.json
Remove-Item Env:\DOTNET_TieredCompilation
```

Run those in a task-specific shell, or restore an existing tiering setting afterwards. Disabling
tiered compilation prevents the measured method switching JIT tiers midway through this very
short benchmark. The normal durable-I/O matrix uses the runtime's default tiering.

Micro mode makes three million calls per repetition (nine repetitions) through the real repository
facade and passthrough index manager into a trivial synchronous store, with no disk IO. It throws
if the enabled-empty path attempts a committed snapshot. This isolates dispatch/wrapper cost;
its absolute ns numbers are **not** database save latencies. The after build also measures actual
snapshot materialization, property comparison and a simple predicate, at 512 B/64 KiB, five
repetitions of 10,000 operations after warm-up. These isolated stages are not additive profiling
percentages of full save time. The benchmark is an internal friend for these nonpublic probes.

## Concurrency, batches and larger payloads

```powershell
dotnet ..\notification-after-bin\StorageBenchmarks.Notifications.dll --contention --output ..\notification-contention.json
dotnet ..\notification-after-bin\StorageBenchmarks.Notifications.dll --modes disabled,empty,changed,filtered --iterations 256 --repetitions 3 --payload 65536 --output ..\notification-large.json
```

Contention mode uses 1,000 distinct preseeded objects, 512-byte payloads, one discarded warm-up
and three measured repetitions, alternating mode order. `parallel16` has sixteen independent
writers; `batch100` uses ten batches of 100. Compare disabled/empty/changed **within** each shape.
Every entity has a cold notification baseline here, unlike repeated updates in the main matrix.
The revised empty path preserves parallel writes, and observed existing-key writes use per-entity
ordering. End-to-end timings include background detection and the subscriber's full drain.
Use `--repetitions 5` to increase samples. A separate harness may link these driver sources and
implement the optional partial registration hook for another provider; no external provider
assembly is referenced or bundled by this public project. Unknown provider names fail explicitly.

Temporary GUID store directories are created under `InterfaceDB-NotificationBench` in the OS
temp directory and removed on normal completion. Raw JSON/output stays outside source control.
Curated results: [parallel-write follow-up](../documentation/repository-notifications-parallel-performance-20260905.md)
and [initial notification report](../documentation/repository-notifications-performance-20260905.md).
