# Mixed-store paging follow-up — 28 October 2026

The designer's paging concern reproduces on Windows. A shared TCP session correctly hid tenant
records, but repeatedly asked the provider for physical pages until it reached shared records.
Each physical page sorted all keys and read payloads that the host then discarded. This matters
when shared keys sort after tenant keys; the recommended separate-store configuration avoids it.

## Measured change

| Hidden tenant records | Requested page size | Before | After | After, hint deliberately removed |
|---:|---:|---:|---:|---:|
| 2,000 | 50 | 25.343 ms | 0.347 ms | 25.026 ms |
| 2,000 | 64 | 21.104 ms | 0.401 ms | 21.051 ms |
| 10,000 | 50 | 555.306 ms | 1.089 ms | 545.399 ms |
| 10,000 | 64 | 437.145 ms | 0.979 ms | 421.960 ms |

The provider now examines the key snapshot once, filters tenant keys before sorting, and reads
only shared payloads. With 10,000 hidden records, a 50-item request that returns ten shared
records makes one physical call instead of 201. Returned physical payload bytes fall from
640,640 to 640. At page size 64, calls fall from 157 to one. Warm tenant reads remain about
0.32 ms. Shared-only reads were about 0.15 ms, with no meaningful improvement demonstrated.

These are five-trial medians after two warmups per mode, with alternating mode order against
the same live store. Windows 10.0.19045, .NET 10.0.12, 32 logical processors, tiered compilation
disabled; client/server loopback TLS/JWT, 64-byte opaque records, 500 records per tenant and ten
shared `zz-*` keys. Connection setup, seeding and exact result assertions are outside timing.
The original baseline ran before the paging change; the patched run also deliberately removed
the hint to retain the previous fallback as a same-process control. Durability is unchanged.

Every run checked exact shared payloads, ordering and continuation, tenant reads, and trusted
raw enumeration of all 10 / 2,010 / 10,010 records. The runner uses a 60-second diagnostic timeout
to observe slow pages. It does not change production timeouts. This is not a SQL comparison,
WAN test, typed-query benchmark, concurrent workload or a general 500-fold database speedup.
The reported 100,000-record mixed-store timeout was not measured here. Filtering still scans a
whole-store key snapshot per shared page; global capacity checks on writes are unchanged.

## Preserved contracts

`EncodedStoreRequest.UnpartitionedOnly` is an optional server-assigned hint, readable by custom
providers but absent from the wire. The host derives it from the authorized scope and retains
response validation and filtering. Providers that ignore it remain correct through the existing
fallback. Nonempty tenant scopes clear it. Shared selection bypasses injected partition indexes
because their empty-scope contract describes physical enumeration, not shared-only selection.

Direct physical enumeration defaults to all partitions, preserving server-side migrations,
adoption, reopen validation and revision clocks. The global physical-key ceiling, page/frame
limits, cursor ordering, cancellation, durable writes, transactions and access policies remain
in force. This adds no permission, changes no persisted format and introduces no wire version.

Prefer separate shared and partitioned registrations as documented in
[the data-policy guide](data-access-policies.md). Unrestricted access is not an administrator
grant to another tenant's records.

## Evidence and reproduction

- [Runner instructions](../StorageBenchmarks.AccessPolicy/README.md)
- [Before measurements](../StorageBenchmarks.AccessPolicy/mixed-paging-baseline-20261028.json)
- [After measurements and fallback control](../StorageBenchmarks.AccessPolicy/mixed-paging-patched-20261028.json)
- [Release validation and qualification limits](preview13-validation.md)

The raw reports include all trials, physical-work counters and the loaded transport module ID.
The source changes are included in the published preview.13 packages. The unchanged source
subsequently passed Linux correctness validation as recorded in the release validation document;
the timing table remains Windows-only. Android, physical power-loss and release qualification
are not granted by these measurements.
