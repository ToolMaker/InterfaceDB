# Envelope revision qualification — 2026-09-06

The free revision implementation passes the core and Segment regression suites. It adds allocation and reopen costs. The results below describe local measurements of this uncommitted working tree, not a package release or a zero-overhead guarantee.

All elapsed figures below are medians of three samples unless noted; lower is faster. No samples were dropped. These are local .NET 10.0.9 Windows measurements on a 32-logical-processor host, with durable WindowsNvme provider settings. Before binaries contain the pre-existing notification work but precede the revision changes; they are not a clean historical notification baseline.

The common matrix seeds 512 complete entities with 512-byte payloads, exact ID/value indexes, and metadata-only object caches. Each case warms on 64 records. Timed sequential/parallel writes update 512 distinct records; parallelism is 16. Indexed point reads materialize 512 entities; scans materialize 2,048 entities in four passes. Setup, seeding, and correctness verification are outside timing. Each accepted update is verified through the index. Changed subscriptions consume entities and are drained inside timing; there are exactly 512 deliveries. Empty means notification support enabled with zero subscribers. Physical notifications, watchers, and background maintenance are disabled.


## File and Segment writes

| Provider | Notifications | Sequential ms, before → after | Parallel 16 ms, before → after | Sequential allocated bytes/op, before → after |
| --- | --- | ---: | ---: | ---: |
| File | disabled | 1299.33 → 1119.50 | 779.48 → 579.34 | 8204 → 8613 |
| File | empty | 1377.77 → 1278.50 | 769.71 → 545.49 | 8853 → 9222 |
| File | changed | 1573.14 → 1343.29 | 769.96 → 675.54 | 20756 → 21460 |
| Segment Core | disabled | 179.80 → 227.28 | 85.54 → 100.71 | 5062 → 6782 |
| Segment Core | empty | 178.83 → 217.54 | 87.62 → 89.83 | 5472 → 7206 |
| Segment Core | changed | 207.77 → 336.37 | 86.35 → 104.01 | 15520 → 18597 |

The disabled sequential median improved about 14% for File and increased about 26% for Segment. Distinct-key parallel medians improved about 26% for File and increased about 18% for Segment. File avoids its previous post-coalescing full-object reread. Segment adds about 1.7 KiB/op for staging, conditional coordination, and publication. These are whole-operation measurements; component explanations are inferred from the code, not an allocation profile.

## Materialized reads

| Provider | Workload | Disabled ms, before → after | Empty ms, before → after | Disabled allocated bytes/read, before → after |
| --- | --- | ---: | ---: | ---: |
| File | point-read | 53.38 → 54.49 | 74.71 → 77.85 | 7168 → 7238 |
| File | scan | 44.22 → 41.67 | 46.83 → 45.03 | 3643 → 3613 |
| Segment Core | point-read | 26.28 → 29.12 | 20.67 → 27.01 | 5760 → 5784 |
| Segment Core | scan | 78.74 → 103.41 | 102.00 → 107.75 | 2868 → 2898 |

Segment's first scan median increased from 78.74 to 103.41 ms, so the follow-up interleaved old/new binaries for five repetitions on 2,048 seeded records, 2,048 indexed point reads, and four complete scans (8,192 materialized reads):

| Provider | Workload | Median ms, before → after | Before min–max ms | After min–max ms | Allocation delta bytes/read |
| --- | --- | ---: | ---: | ---: | ---: |
| Segment Core | point-read | 79.80 → 79.95 | 74.25–104.00 | 76.44–86.58 | 24.20 |
| Segment Core | scan | 158.82 → 104.52 | 88.31–175.06 | 92.92–164.60 | 24.92 |

The larger sample did not reproduce the initial scan slowdown. Point-read medians were close; around 24–25 extra allocated bytes/read remain. Timing ranges overlap substantially, so no universal no-regression claim is made.

## Reopen and conflict costs

| Provider | First write after reopen ms, before → after | Increase ms |
| --- | ---: | ---: |
| File | 2.93 → 9.52 | 6.60 |
| Segment Core | 15.89 → 25.67 | 9.78 |

Reopen samples include the first write's required store open/recovery and durable revision-range reservation, using warm OS caches and an earlier loaded envelope. They do not force index initialization. The range reservation avoids a durable clock flush on every warm save.

| Provider | Same-key 512 attempts ms, before → after | Accepted before, all samples | Accepted / rejected after, each sample | 512 stale attempts ms, before → after |
| --- | ---: | --- | --- | ---: |
| File | 1292.57 → 818.39 | 512 accepted, 0 errors; 512 accepted, 0 errors; 510 accepted, 2 errors | 1 / 511 | 1262.95 → 509.55 |
| Segment Core | 93.05 → 10.02 | 512 accepted, 0 errors; 512 accepted, 0 errors; 512 accepted, 0 errors | 1 / 511 | 183.59 → 15.02 |

The sequential stale workload accepted all 512 attempts before and rejected all 512 after. These compare different outcomes; no overhead ratio is claimed.

## Strong cache control and reproduction

The existing public [notification benchmark](../StorageBenchmarks.Notifications/README.md) measured 500 saves, three repetitions, 512-byte payloads and 128 rotating strongly cached records:

| Provider | Mode | 500 saves ms, before → after | Change | Allocated bytes/op, before → after | Median sample p95 save ms, before → after |
| --- | --- | ---: | ---: | ---: | ---: |
| File | disabled | 1260.81 → 1228.53 | -2.6% | 8774 → 8969 | 2.88 → 2.99 |
| File | empty | 1295.29 → 1169.06 | -9.7% | 9182 → 9383 | 2.79 → 2.65 |
| Segment Core | disabled | 149.83 → 172.02 | +14.8% | 4780 → 6494 | 0.39 → 0.46 |
| Segment Core | empty | 155.10 → 163.27 | +5.3% | 5171 → 6893 | 0.40 → 0.43 |

Raw controls: [before](measurements/envelope-revisions/strong-before.json), [after](measurements/envelope-revisions/strong-after.json). Both JSON Build fields use the notification driver's existing label; filenames identify revision before/after. Build each source state into separate output folders, then run:

```powershell
dotnet <binary-folder>/StorageBenchmarks.Notifications.dll --modes disabled,empty --iterations 500 --repetitions 3 --payload 512 --output <result.json>
```

Do not rebuild the saved pre-revision binary folder from current source. The before measurement includes the pre-existing notification implementation; it is not a pre-notification baseline. The broader 512-record comparison used a separate local common provider harness. Neither tests nor builds overlapped benchmark timing. Samples were not dropped; workstation I/O, JIT, and GC still cause variation.

## Regression results and practical boundaries

```powershell
dotnet test InterfaceDb.Tests/InterfaceDb.Tests.csproj -c Release
dotnet test InterfaceDB.Storage.SegmentCore.Tests/InterfaceDB.Storage.SegmentCore.Tests.csproj -c Release
```

The final `revisions-complete.trx` runs passed 233 core tests and 138 Segment tests, with two platform-specific skips in each and zero failures. Tests exercise equality in both mismatch directions, simultaneous loaded copies, stale delete/update, missing/recreated keys, failure/cancellation staging, File partial-success publication, resolved candidates, cached-envelope edits, unsafe serializer/coordinator rejection, metadata imports/acknowledgements, legacy IDB5, clock rollback/reopen, and Segment backup. A physical callback writing again cannot replace the earlier operation's notification snapshot or regress its search index.

[Envelope revision documentation](envelope-revisions.md) describes the required serializer capability, IDB6 one-way upgrade, raw-storage ownership, metadata preconditions, backups, and application copy semantics. Whole-envelope encrypted File checks still read/decrypt the full encoded payload without entity deserialization; encrypted performance was not measured here. Editing baselines, automatic merge, distributed timestamp uniqueness, and power-loss certification are outside this implementation.
