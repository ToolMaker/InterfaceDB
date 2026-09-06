# Repository notifications: measured costs — 5 September 2026

Historical first-implementation results. The broad write gate described below has since been
replaced; see the [parallel-write follow-up](repository-notifications-parallel-performance-20260905.md).
Retained timings below have not been rewritten to describe the newer implementation.

## Result in brief

The free repository notification implementation is complete in source for File and Segment Core,
with Created/Changed/Deleted payloads, query filters, committed-property change sets, bounded
asynchronous dispatch, explicit subscriber lifetimes and causal feedback-loop protection.
The envelope notification API and disk format remain unchanged. Nothing was committed, packaged
or published as part of this work; the checked-in package version remains the existing preview.

**Default disabled path:** no detectable regression in the isolated wrapper measurement and zero
per-save wrapper allocation, before and after. This does not mean the entire database allocates
nothing, or that added instance fields/DI registrations have no one-time memory cost.

**One matching Changed subscriber:** final sequential medians added **3.3% File / 8.5% Segment Core**
against the corresponding current disabled update controls. This is sampled performance on this
host, not a universal overhead bound. Timing variation is significant for small differences.

**Important limitation:** enabling notifications currently serializes mutations per repository,
even with zero subscribers. In the 16-writer probe, enabled-empty was **72.5% slower on File and
105.0% slower on Segment Core**. The default disabled path retains existing parallelism.
Do not enable this initial version indiscriminately on highly concurrent write repositories.

Usage, semantics, origin scopes and limitations: [repository notifications guide](repository-notifications.md).
Reproduction: [benchmark README](../StorageBenchmarks.Notifications/README.md).

## Environment and method

- Windows build 19045, .NET 10.0.9, Release; AMD Ryzen 9 3950X, 16 cores / 32 logical processors.
- C: NTFS on this host; installed drives identify as Samsung SSD 980 PRO 1TB.
- File Durable; Segment Core Durable, WindowsNvme profile / write-through. No durability,
  coordination, sidecar lock or flush setting was weakened.
- StrongObject cache, Id exact search index; initialized long-lived repository. Physical envelope
  notifications and file watchers disabled. Setup, registration, seed writes and disposal excluded.
- Payload: Id, Value, Sequence, 512-byte byte array. Updates rotate through 128 seeded records;
  the first 128 updates have cold notification baselines. Inserts/deletes use matched controls.
- Baseline: clean released preview.6 source at
  `f748e1bc9678d2d1be2d66117ecf3930a0bfeb8e`, built with the same timed-operation driver
  using its pre-feature conditional compilation option. The baseline worktree was not modified.
- Before control: five measured repetitions of 1,000 operations. Final-source main matrix:
  three measured repetitions of 1,000 operations, after one discarded warm-up. Case order rotates.
  An earlier five-repetition after run also completed; tables below use the **final-source** rerun
  after callback-drain/deadlock hardening. Tests/builds did not run concurrently with timing runs.
- Reported times are medians of repetition totals, including completion of all expected callbacks.
  Save percentiles are medians of per-repetition percentiles, not pooled population percentiles.
- Bounded callback concurrency = 8; test mailboxes enlarged to 1,001 entries and subscriber limit
  to 1,100 for the measured bursts. Default overflow behavior is tested separately.
- Each receiving subscriber materializes a separate full typed object, consumes its values and
  completes. No application I/O, business processing or handler-originated saves are timed here.
  Million-delivery cases validate every expected callback, rather than measuring enqueue alone.

These are direct stopwatch qualification runs, not BenchmarkDotNet confidence intervals. OS
scheduling, storage state, runtime allocation and case ordering affect samples. Registration cost,
cold repository opening, remote delivery and mobile/AOT performance are not measured.

## 1. What do applications pay when notifications are disabled?

### Real durable repository operations

Milliseconds per 1,000 operations; both columns have notifications disabled.

| Operation | File before ms | File now disabled ms | Segment before ms | Segment now disabled ms |
| --- | ---: | ---: | ---: | ---: |
| Update | 2,720.61 | 2,626.12 | 306.05 | 301.54 |
| Insert | 2,516.87 | 2,509.91 | 314.53 | 313.16 |
| Delete | 5,162.72 | 5,183.72 | 272.59 | 271.82 |
| Identical save | 2,573.12 | 2,580.03 | 308.35 | 291.74 |

The apparent before/after improvements are **not** claimed as gains from notifications. Disabled
update ranges were File 2,560.22–2,733.67 ms before versus 2,529.30–2,676.69 ms after; Segment Core
293.56–345.92 versus 298.47–320.78 ms. Those ranges overlap. All-thread update allocations were
about 8,812/8,813 B per operation on File and 4,798/4,799 B on Segment before/after; this includes
harness/runtime noise and existing storage work.

### Isolated CPU-only wrapper control

Real repository facade and passthrough index manager over a trivial synchronous store. Nine
repetitions of three million calls, warmed first; `DOTNET_TieredCompilation=0` for both builds to
prevent a JIT-tier transition mid-sample. These are **not disk-save latencies**.

| Mode | Median ns/save | Allocated B/save |
| --- | ---: | ---: |
| Before feature | 10.700 | 0 |
| Current, feature disabled | 10.750 | 0 |
| Current, enabled with zero subscribers | 227.447 | 112 |

The disabled difference is 0.050 ns (about 0.5%), inside observed run-to-run variation. Enabling
an empty hub adds about 0.217 microseconds in this synchronous control, including mutation-gate
and async-flow reentry bookkeeping. Real asynchronous durable updates allocated **360 additional
bytes/write** in enabled-empty versus current disabled. No snapshots, comparisons, predicates,
event queues or notification workers run on that empty path.

The API is opt-in through `StoragePolicy.RepositoryNotifications`; null stays on the old direct
storage route. Enabling it and having no subscribers is deliberately measured separately.

## 2. Sequential notification cost

Total milliseconds per 1,000 operations, including callback drain. Created compares with the
disabled insert control, Deleted with disabled deletion, no-op with disabled identical saves;
Changed and enabled-empty compare with disabled updates.

| Case | File ms / 1,000 | vs matched disabled | Segment ms / 1,000 | vs matched disabled |
| --- | ---: | ---: | ---: | ---: |
| Disabled update | 2,626.12 | — | 301.54 | — |
| Enabled, zero subscribers | 2,535.27 | -3.5% | 295.65 | -2.0% |
| One Created | 2,579.70 | +2.8% | 324.45 | +3.6% |
| One Deleted | 5,201.07 | +0.3% | 294.90 | +8.5% |
| No-op save, one Changed | 2,788.19 | +8.1% | 330.88 | +13.4% |
| One matching Changed | 2,713.55 | +3.3% | 327.21 | +8.5% |
| One rejecting Changed filter | 2,710.89 | +3.2% | 328.72 | +9.0% |
| 10 matching Changed | 2,750.20 | +4.7% | 335.24 | +11.2% |
| 100 matching Changed | 2,866.62 | +9.2% | 473.91 | +57.2% |
| 1,000 matching Changed | 4,262.54 | +62.3% | 2,545.83 | +744.3% |

Negative enabled-empty percentages are **measurement variation, not a speed benefit**: the CPU
probe and allocation measurements show a real additional cost. File Created/Deleted differences
are also small relative to some sample ranges; do not read their percentages as precise guarantees.

A rejecting filter does not make tracking free. Correct before/after capture and isolated
materialization still happen; the predicate prevents application callback execution only.
Likewise, detecting that a save was a no-op requires comparing committed values.

### Allocation and sampled memory pressure

Allocation includes underlying storage, snapshots, equality work, mailboxes, scheduling and all
recipient objects. Bytes are **per repository write**, not per delivered notification. Sampled
peak is additional managed heap relative to the start of the timed region, not an exact retained
mailbox/baseline size.

| Case | File allocated B/write | Segment allocated B/write | File sampled peak MiB | Segment sampled peak MiB |
| --- | ---: | ---: | ---: | ---: |
| Disabled update | 8,813 | 4,799 | 7.57 | 4.62 |
| Enabled, zero subscribers | 9,173 | 5,159 | 7.88 | 4.95 |
| One Created | 17,653 | 12,637 | 8.63 | 7.76 |
| One Deleted | 13,051 | 9,040 | 7.21 | 7.74 |
| No-op save, one Changed | 15,342 | 10,203 | 7.65 | 7.54 |
| One matching Changed | 18,785 | 13,646 | 7.28 | 7.59 |
| One rejecting Changed filter | 18,753 | 13,614 | 7.26 | 7.58 |
| 10 matching Changed | 44,561 | 39,371 | 7.73 | 7.33 |
| 100 matching Changed | 336,652 | 331,935 | 7.97 | 8.00 |
| 1,000 matching Changed | 3,425,203 | 2,974,539 | 9.19 | 21.98 |

For one Changed subscriber, additional allocation was about **9,972 B/write File** and
**8,847 B/write Segment Core** over their disabled controls. At 1,000 subscribers, allocations
reached about **3.43 GB File / 2.97 GB Segment Core per 1,000 writes** (decimal GB).
The snapshot-per-recipient ownership guarantee is expensive at extreme fan-out.

Raw JSON also contains post-GC retained-heap deltas. They are aggregate noisy observations,
not a leak proof or component attribution. The raw Gen0 count includes the explicit post-run
retained-heap measurement collection, so it is not a natural-GC-only counter. Do not interpret
allocation volume as simultaneous retained memory or the sampled peak as a strict bound.

### Save and notification tails

| Case | File p50 / p95 / p99 save ms | Segment p50 / p95 / p99 save ms | File / Segment p95 notification ms |
| --- | ---: | ---: | ---: |
| Disabled update | 2.578 / 2.960 / 3.250 | 0.287 / 0.387 / 0.491 | — |
| Enabled, zero subscribers | 2.507 / 2.796 / 2.926 | 0.282 / 0.389 / 0.516 | — |
| One matching Changed | 2.651 / 2.985 / 3.388 | 0.305 / 0.424 / 0.634 | 3.005 / 0.436 |
| 100 matching Changed | 2.803 / 3.353 / 3.962 | 0.433 / 0.721 / 1.271 | 3.455 / 0.810 |
| 1,000 matching Changed | 4.056 / 5.349 / 5.780 | 0.458 / 3.204 / 4.447 | 5.276 / 1,684.595 |

Notification latency is measured **from save start**, so includes storage time. It observes the
first subscriber, not the slowest of all subscribers. Total drain validates everyone.

In the 1,000-subscriber Segment Core case, median time to finish the save loop was **783.63 ms**,
but total time to complete delivery was **2,545.83 ms**. Reporting only the first figure would hide
roughly 1.76 seconds of remaining delivery work. Median p95 first-subscriber notification latency
was **1,684.60 ms**. File writes pace dispatch more slowly: save loop 4,259.39 ms and total
4,262.54 ms, which does not mean callbacks have zero CPU/allocation cost.

## 3. Parallel writers and batches: the main trade-off

Three repetitions after discarded warm-up, 1,000 distinct seeded objects with 512-byte payloads.
Every entity starts with a cold notification baseline. Compare modes within the same row.
`parallel16` uses sixteen independent writers; `batch100` uses ten batches of 100.

| Provider / shape (1,000 updates) | Disabled ms | Enabled-empty ms | One Changed ms | Empty penalty | Changed penalty |
| --- | ---: | ---: | ---: | ---: | ---: |
| File / parallel16 | 1,551.86 | 2,676.21 | 2,889.58 | +72.5% | +86.2% |
| File / batch100 | 1,507.29 | 1,483.66 | 1,676.13 | -1.6% | +11.2% |
| Segment Core / parallel16 | 140.66 | 288.33 | 343.66 | +105.0% | +144.3% |
| Segment Core / batch100 | 11.80 | 11.64 | 27.64 | -1.3% | +134.2% |

The single enabled mutation gate is the reason independent writes lose concurrency. It provides
an unambiguous predecessor/commit/result sequence for snapshots and avoids comparing mutated
cached objects. It is a conservative correctness choice, **not** an optimization.

Batch-internal provider behavior is preserved, so batch100 still benefits from File bulk work and
Segment Core transaction amortization. Snapshot/delivery overhead is more conspicuous relative to
Segment Core's fast batch baseline: one subscriber added about 15.84 ms per 1,000 objects
(+134.2%), while remaining much faster than 1,000 separate observed saves in this workload.
The Changed batch sample ranged 26.52–54.71 ms; the median is not a tail bound.

Future performance work should first address safe per-key ordering and a quiescent transition
between an unobserved parallel fast path and observed mutations. Simply bypassing the gate when
a racy subscriber count reads zero can corrupt predecessor tracking during registration/disposal.
That optimization is **not implemented or claimed here**.

## 4. Where the active costs lie

Changes implemented during this work to limit overhead:

- Direct disabled branch and cached save/delete/batch delegates on enabled owners; no per-call
  method-group allocation.
- Bounded immutable encoded baseline cache (256 entries / 16 MiB by default), instead of
  rereading hot entities' previous values every time. No shallow cache-object comparison.
- Trusted providers transfer newly read byte buffers into immutable snapshots without another
  constructor copy. Each materialization still receives isolated backing bytes.
- Cached compiled property getters, value equality and vectorized byte-array comparison.
- Read-only property-name sets shared per committed event; separate entity materialization for
  every recipient. No metadata-only or no-op Changed callback.
- Bounded subscriber queues and callback concurrency, on-demand workers, no timer per subscriber.

Actual internal stage microbenchmarks, five repetitions of 10,000 operations with warm-up and
tiered compilation disabled, report the following medians:

| Isolated stage | 512 B payload: ns / allocated B | 64 KiB payload: ns / allocated B |
| --- | ---: | ---: |
| snapshot-materialization | 1,072.26 / 1,992 | 6,616.70 / 132,040 |
| property-diff | 211.91 / 208 | 3,130.55 / 208 |
| predicate | 4.72 / 0 | 5.22 / 0 |

A normal observed Changed save materializes old/new snapshots for comparison and a further
snapshot for each potential recipient's filter/callback, plus provider reads and dispatch.
One full materialization costs much more than this simple predicate. Large byte-array equality
also becomes significant. These stage measurements omit I/O, scheduler contention, encrypted
fields and arbitrary application code: **they cannot be summed into exact full-save percentages**.

### 64 KiB payload sensitivity

Three repetitions of 256 updates, same 128 seed keys. The cold-baseline fraction is therefore
larger than the 1,000-update main matrix. These are matched large-payload modes, not a pure
payload-size scaling experiment.

| Provider / case (256 updates) | Median ms | Min–max ms | Allocated B/write |
| --- | ---: | ---: | ---: |
| File / disabled | 756.73 | 683.92–893.01 | 73,712 |
| File / empty | 725.04 | 698.68–971.14 | 74,072 |
| File / changed | 805.72 | 781.45–867.06 | 572,118 |
| File / filtered | 827.67 | 750.86–886.74 | 572,018 |
| Segment Core / disabled | 131.35 | 128.59–141.03 | 69,747 |
| Segment Core / empty | 135.34 | 126.54–140.67 | 70,107 |
| Segment Core / changed | 169.12 | 167.62–432.26 | 567,847 |
| Segment Core / filtered | 169.73 | 169.63–177.69 | 567,814 |

For one subscriber, allocation rose to roughly 572 KB/write on File and 568 KB/write on
Segment Core (decimal KB, including storage). The Segment Changed outlier of 432.26 ms is
retained in the reported range, not discarded. Larger objects and high fan-out deserve workload-
specific qualification before enabling broad subscriptions.

## 5. Feedback-loop protection and its limits

A subscription's callback-originated writes are automatically tagged with a bounded in-process
causal chain. That subscription, and earlier subscriptions in the same chain, do not receive
the descendant notifications by default. Other subscribers still receive them. This avoids
self-triggering and A -> B -> A loops without comparing CLR object references or adding
anything to stored envelopes.

For edits initiated outside the callback, use:

```csharp
using (subscription.BeginWriteScope())
{
    entity.Points += 10;
    await repository.SaveAsync(entity);
}
```

Scopes follow ordinary async execution. Explicit `receiveOwnWrites: true` opts back into echoes;
the default maximum causal depth of 64 remains a failsafe. An unchanged save still produces no
Changed event. The deliberate trade-off is that an initiator also misses corrections caused
by its own descendant chain; opt in only with a convergent handler when those are needed.

This is local provenance, not authentication, remote idempotency or universal loop detection.
Context-suppressed work/remote messages need separate correlation. Drain calls from callbacks
are rejected to avoid callback-slot deadlocks. Synchronous physical callbacks cannot reenter
an observed mutation; use repository callbacks after commit instead.

## 6. Verification and remaining boundaries

| Verification | Result |
| --- | --- |
| Full solution Release build | Passed, 0 warnings / 0 errors |
| Core tests | 183 passed, 2 platform-dependent skips |
| Segment Core tests | 138 passed, 2 platform-dependent skips |
| Analyzer tests | 5 passed |
| Executable integration harness | All scenarios passed |
| New repository notification cases (included in core count) | 50 passed |
| Final main matrix | 78 measured cases; expected delivery/filter behavior verified |
| Contention/batch probe | 36 measured cases; expected deliveries verified |
| Large-payload probe | 24 measured cases; expected deliveries verified |
| Stable CPU-only probes | Before/after controls and component measurements completed |

Tests cover in-place cached edits, precise inherited/collection property names, metadata/no-op
silence, detached deletion, encrypted fields, independent recipients, index readiness, database-key
isolation, cancellation, keep-alive, callback faults, overflow/byte bounds, reopen, batch/concurrent
writes, partial File commits, atomic Segment failures, reentrant saves, explicit origin scopes,
async scope restoration, direct/indirect feedback and bounded echo opt-in.

The Segment exported-API review found exactly two added descriptions: implementation of
`ICommittedEnvelopeSnapshotSource<T>` and its `CaptureCommittedAsync` method. Removing those
lines reproduces the previous approved API fingerprint exactly. The additive preview baseline
was updated; no existing signature, package version or disk-format identifier was changed.

This remains a local, single-owner observation system. Delivery is not durable or replayable.
Filters are not authorization. Bypass writers, migrations/restores and external watcher histories
are not exact repository-event coverage. Custom serializers/comparers must respect snapshot/value
contracts. File Performance mode is not upgraded to Durable by enabling notifications.

No new Android/iOS/macOS hardware or AOT run was performed for this feature. Other storage
adapters require their own committed-snapshot integration and qualification. No remote transport,
commercial distribution or public package publication is part of this result.

## Reproduction and evidence

See [the executable harness commands](../StorageBenchmarks.Notifications/README.md).
Raw evidence is retained outside Git in the task's `QualificationArtifacts` directory:

- `notification-before.json` — released-source durable controls, five repetitions.
- `notification-after.json` — initial five-repetition qualification before final drain hardening.
- `notification-after-final.json` — final-source three-repetition matrix used in these tables.
- `notification-before-micro-stable.json`, `notification-after-micro-stable.json` — stable JIT controls.
- `notification-contention.json`, `notification-large.json` — final-source supplementary probes.
- Test projects' `TestResults/repository-notifications-final.trx` — core/Segment/analyzer outcomes.

The earlier `notification-before-micro.json` had an in-run tiered-JIT transition and is **not**
used here. Initial/optimized short-run development artifacts likewise are not final performance
claims. Keep raw JSON, build outputs and transient test stores out of preview commits.
