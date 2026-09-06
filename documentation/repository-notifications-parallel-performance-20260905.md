# Repository notifications: parallel-write follow-up — 5 September 2026

## Outcome

The enabled-but-empty repository no longer serializes storage IO. The original **+105.0% Segment Core elapsed-time regression with 16 writers was not reproduced** after the change. Default-disabled notifications still use the original direct path.

The user's proposed detached old/new comparison is implemented in the notification dispatcher: writers retain immutable encoded committed states; a worker materializes detached old/new objects and computes the property list once per event. Each recipient receives its own isolated after object. SaveAsync returns the saved entity and still waits for the configured durable commit, not for comparison or callbacks.

This is not zero-cost notification processing. Snapshot reads, safe copies, coordination, materialization, filters and fan-out still use this process's CPU and memory. All totals below include the completion of the expected notifications.

## 16 concurrent writers: the original problem

Milliseconds per 1,000 durable updates; median of five repetitions, after a discarded warm-up. A positive percentage means longer elapsed time, not percentage throughput lost.

| Provider | Feature disabled ms | Enabled, zero subscribers ms | Empty cost | One Changed subscriber ms | Changed cost |
| --- | ---: | ---: | ---: | ---: | ---: |
| File | 1,521.01 | 1,511.75 | -0.6% | 1,517.06 | -0.3% |
| Segment Core | 155.30 | 152.59 | -1.7% | 152.11 | -2.1% |

Small negative values are not claimed as improvements: they lie within the variation of these runs. File and Segment Core show no detectable empty-path penalty in this particular parallel workload.

Historical first-implementation results (three repetitions; same operation shape and durability):

| Provider | Old disabled ms | Old enabled-empty ms | Old empty cost | New enabled-empty ms |
| --- | ---: | ---: | ---: | ---: |
| File | 1,551.86 | 2,676.21 | +72.5% | 1,511.75 |
| Segment Core | 140.66 | 288.33 | +105.0% | 152.59 |

Historical and current disabled controls differ, so use the within-run percentages to judge cost. At 2.05 times the duration, the old Segment result meant roughly half the throughput, not a literal loss of more than 100% of throughput.

## Sequential operations and fan-out

Milliseconds per 1,000 operations, median of three repetitions. Changed/empty/filtered costs use the matching disabled update control. Created, Deleted and No-op use their own matching disabled operation controls.

| Operation | File ms / cost | Segment Core ms / cost |
| --- | ---: | ---: |
| Disabled update | 2,605.91 | 319.17 |
| Enabled, zero subscribers | 2,680.03 / +2.8% | 305.06 / -4.4% |
| One Created | 2,760.19 / +4.1% | 342.64 / +9.4% |
| One Deleted | 5,362.51 / +4.6% | 313.40 / +1.3% |
| No-op save, Changed subscriber | 2,795.89 / +6.1% | 331.82 / +5.8% |
| One Changed | 2,756.95 / +5.8% | 345.38 / +8.2% |
| One Changed, rejecting filter | 2,804.77 / +7.6% | 335.89 / +5.2% |
| 10 Changed subscribers | 2,787.66 / +7.0% | 340.56 / +6.7% |
| 100 Changed subscribers | 2,989.45 / +14.7% | 481.59 / +50.9% |
| 1,000 Changed subscribers | 4,266.82 / +63.7% | 2,528.36 / +692.2% |

Matching disabled controls for inserts/deletes/identical saves:

| Operation | File ms | Segment Core ms |
| --- | ---: | ---: |
| Insert | 2,652.61 | 313.30 |
| Delete | 5,128.99 | 309.33 |
| Identical save | 2,634.70 | 313.50 |

One thousand subscribers means one million independently materialized deliveries for this 1,000-write run. It is deliberately expensive; moving detection off the writer does not remove fan-out cost.

| 1,000-subscriber workload | Save loop ms | Full completion ms | Total managed allocation MiB |
| --- | ---: | ---: | ---: |
| File | 4,263.72 | 4,266.82 | 3,274.31 |
| Segment Core | 777.59 | 2,528.36 | 2,840.25 |

Save and completion columns are separate medians, not a promise that subtracting them reproduces the median drain. The JSON retains every sample and drain time. Allocations are total allocations, not simultaneous retained memory.

## Batches: important remaining costs

Ten batches of 100 distinct existing objects, 1,000 updates total. Five repetitions. These have cold notification baselines and very fast provider batch commits.

| Provider | Disabled ms | Empty ms / cost | One Changed ms / cost |
| --- | ---: | ---: | ---: |
| File | 1,447.56 | 1,470.28 / +1.6% | 1,633.85 / +12.9% |
| Segment Core | 11.28 | 11.80 / +4.7% | 23.31 / +106.7% |

**The active batch cost is not the old zero-subscriber bug.** Batch persistence is amortized over 100 objects, but precise notifications still capture up to two encoded committed states per object and deliver individual events. Per-key coordination and per-event allocations remain. Diffing happens once in the background; it cannot erase this work.

## Larger objects

64 KiB payload, 256 updates, three repetitions. Full completion milliseconds; percentages against the matching disabled update.

| Provider | Disabled ms | Empty ms / cost | One Changed ms / cost | Rejecting filter ms / cost |
| --- | ---: | ---: | ---: | ---: |
| File | 795.13 | 764.51 / -3.9% | 819.20 / +3.0% | 839.77 / +5.6% |
| Segment Core | 137.52 | 137.70 / +0.1% | 169.52 / +23.3% | 174.27 / +26.7% |

Large-object comparison preserves correctness by using owned serialized buffers and independently materialized objects. It does not lend a caller's mutable cached instance as a historical record. A rejecting filter saves callback/application work, not snapshot capture or the comparison needed to determine whether a change exists.

## What the feature costs with no notifications in use

Fresh micro-control against released pre-feature commit `f748e1bc9678d2d1be2d66117ecf3930a0bfeb8e`: real repository facade, passthrough index manager, trivial synchronous store, no IO, 3,000,000 calls per sample, nine repetitions, tiered compilation disabled for both builds.

| Path | Median ns/call | Wrapper bytes/call |
| --- | ---: | ---: |
| Released pre-feature, disabled | 10.80 | 0 |
| New disabled | 10.79 | 0.00 |
| New enabled, zero subscribers | 198.57 | 112.00 |

The default-disabled wrapper has **no detectable regression and zero per-call wrapper allocation** in this isolated control. This is not zero database allocation or zero one-time instance cost. Enabled-empty still pays admission/async-flow bookkeeping. Actual asynchronous durable sequential runs allocated approximately **408 additional bytes per write** on File and Segment Core, not zero; whole-process allocation is noisier on other providers.

## Where active-notification work lies

| Stage, isolated CPU probe | 512 B ns / allocated B | 64 KiB ns / allocated B |
| --- | ---: | ---: |
| One attached recipient object | 1,109.78 / 1,992.00 | 8,490.02 / 132,040.00 |
| One detached comparison object | 1,423.93 / 2,016.00 | 9,101.98 / 132,064.00 |
| Compare already materialized old/new | 220.63 / 208.00 | 3,156.86 / 208.00 |
| One simple predicate | 5.26 / 0.00 | 5.28 / 0.00 |

These are independent micro-measurements, **not additive profiler percentages** of a database write. In this payload, materialization costs more CPU than comparing the already materialized properties. Changed generally needs two detached materializations once per event, plus one recipient materialization per receiving subscriber. Keeping safe snapshots also adds provider reads and owned buffers. The writer no longer performs that property comparison, but all workers share the application's process and CPU.

For the 16-writer workload, total allocated bytes per operation (including the underlying repository and async runtime):

| Provider | Disabled B/op | Empty B/op | One Changed B/op |
| --- | ---: | ---: | ---: |
| File | 8,536.30 | 8,944.06 | 21,622.76 |
| Segment Core | 4,609.84 | 5,013.09 | 15,832.14 |

## Implementation and semantics

- No subscribers: normal parallel storage operations, short admission accounting, no snapshots/diff/per-entity gates/event queues.
- First registration / last unregistration: a phase handover waits for previously admitted writes before new-phase writes proceed. Subscribe/Dispose themselves do not block on those writes. Cancelled intermediate phases cannot bypass an earlier write. Baselines are phase-owned and discarded after observation ends.
- Active subscribers: same object reference and same physical storage key are ordered; unrelated existing keys can overlap. Overlapping batches acquire identities in stable order. Key entries are removed after their last user and reference entries are weakly held.
- An observed insert with no physical identity yet, or a batch containing one, uses a conservative exclusive publication barrier. This protects insert capture against a subsequent query-and-update. The empty/disabled path bypasses it. This follow-up does not claim parallel scaling for observed inserts.
- Stable encoded before/after states go to a bounded background mailbox. One lazy comparison per event creates detached objects, computes immutable property names and shares them across recipients. Created/Changed recipients retain their attached envelope; Deleted recipients receive detached prior values.
- Queued Changed candidates charge **both** snapshots. No-op candidates can occupy capacity until detection suppresses them. Comparison, filtering and callbacks are included by WaitForIdleAsync. Compare/materialization faults detach observation without failing an already committed save.
- Causal origin suppression and BeginWriteScope remain intact. A callback may save without receiving its own causal echo. Comparers must remain pure and cannot reenter enabled repository mutation. Physical envelope notifications and payloads are unchanged.
- One owning repository remains the observation boundary. No external-writer history, remote replay, distributed authentication or new transaction scope is implied.

## Qualification and reproduction

Release solution build: zero warnings/errors. Test results:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core (includes 68 File/Segment notification cases) | 201 | 2 |
| Segment Core provider | 138 | 2 |
| Analyzers | 5 | 0 |

The full integration executable passed. Skips concern OS-dependent path/symlink cases. Added deterministic cases cover parallel empty/observed writes, subscriber handovers and cancellation, same-key aliases, reversed batches, query/update racing insert publication, deferred comparison, once-per-event work across ten subscribers, detached inputs, both-snapshot byte limits, impure comparer failure, and 512 committed revisions across sixteen concurrent writers.

Environment: Windows build 19045, .NET 10.0.9 Release; Ryzen 9 3950X (16 cores / 32 logical processors); C: NTFS, Samsung SSD 980 PRO hardware reported on host. Durable WindowsNvme provider profiles, StrongObject cache, exact Id index, physical notifications/watchers/eager hydration off; background maintenance/dispose checkpoint excluded. No durability, flush, provider transaction or lease setting was weakened.

Seed, initialization, registration and disposal are outside timings. Main updates rotate 128 objects; the contention case uses 1,000 distinct preseeded objects. Queue capacity was increased for the measured burst; callbacks consume complete objects and every expected delivery is checked. Timed runs did not overlap test/build processes. JSON allocation includes runtime noise; heap sampling may miss peaks. Gen0 counts in the new driver are taken before its forced retained-heap measurement, unlike the original report.

Samples are stopwatch qualification, not statistical confidence intervals or universal overhead guarantees. No UI work, remote transport, application-side callback IO, mobile runtime, startup or subscriber-churn throughput is benchmarked.

Reproduce using the [public benchmark instructions](../StorageBenchmarks.Notifications/README.md), including `--contention --repetitions 5`. The public driver includes File/Segment Core only; external-provider adapters are not bundled. Historical evidence remains in the [initial performance report](repository-notifications-performance-20260905.md); current API and limits are in the [usage guide](repository-notifications.md).

No commit, push, package creation or publication was performed. Review these measured trade-offs before a release decision.
