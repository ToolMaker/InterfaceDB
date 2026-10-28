# Production access-policy benchmark

This non-packable runner uses the production transport and storage projects. It compares
`Disabled` with bounded `Lazy` partition indexing, with three alternating-order rounds on
the same data. Both paths retain the same policy, TLS, JWT validator and transport's existing
WriteThrough durability. No SQL comparison or alternative durability mode is included.

```powershell
dotnet build StorageBenchmarks.AccessPolicy/StorageBenchmarks.AccessPolicy.csproj -c Release
$env:DOTNET_TieredCompilation = '0'
dotnet StorageBenchmarks.AccessPolicy/bin/Release/net10.0/StorageBenchmarks.AccessPolicy.dll .tmp/access-policy-production/benchmark
```

Run from the repository root, without concurrent tests or benchmark work. Restore the original
`DOTNET_TieredCompilation` environment value afterwards. Append `--smoke` for a small functional
run; smoke timings are not evidence. Use a fresh output directory for each recorded run.

Scenarios are 10,000 unrestricted records, 10,000 records in one protected tenant, and 10,000 /
100,000 records across 100 tenants. Each record has an ownership string, integer and 256-character
text, inside the canonical envelope. All data is seeded through authenticated TCP. The server
uses locally generated RS256 credentials and an ephemeral trusted TLS CA; it contacts no identity
provider and installs no machine trust. Client and server run in the same process over loopback.

Measured operations are encoded-envelope Get, 128-record enumeration, full-tenant enumeration,
individual durable updates and batches of up to 128 updates. These are not typed repository query,
cache or conflict-check benchmarks. TLS/JWT connection time and first page after reopen are
reported separately. Each mode is reopened for every round. Operating-system/disk caches are
not flushed: "cold" means a new store/index instance, not cold hardware.

Exact bytes, ordering and continuation counts are checked in timed reads using prebuilt expected
payloads. Every record is additionally deserialized and checked outside timing, after seeding
and after an independent final reopen, for every tenant. Writes are updates of existing keys,
so no mode gets a growing dataset. The regression suites separately test inserts, deletes,
rejected/cancelled batches, eviction and uncertain-commit recovery.

`results.json` records per-round median, p95, process-wide allocated bytes per operation, index
counters, runtime and module IDs. Allocation includes both client and server plus verification;
it is not retained heap. `Complete: true` is written only after all final reopen checks succeed.
An interrupted run retains partial results with `Complete: false`. Data directories are retained
for inspection. No user database is opened or modified.

Compare equal workloads: the unrestricted and one-tenant 10,000-row scenarios show policy costs.
The 100-tenant cases show how selection scales; they return fewer rows and must not be compared
directly against the unrestricted scenario as a measure of authorization overhead. The old
isolated prototype measurements remain in `experiments/AccessPolicySelection`.

## Mixed shared/tenant paging regression

Append `--mixed-paging` to run the separate 0 / 2,000 / 10,000 hidden-tenant workload:

```powershell
dotnet StorageBenchmarks.AccessPolicy/bin/Release/net10.0/StorageBenchmarks.AccessPolicy.dll .tmp/policy-paging/new-run --mixed-paging
```

Build first and use the same quiet-machine and tiered-compilation settings above. Ten shared
64-byte records sort after all tenant keys. Page sizes 50 and 64 run with two warmups and five
alternating-order trials per mode. The forwarding mode preserves the server-assigned enumeration
hint; the fallback control reconstructs only the shared enumeration request without the hint.
Tenant operations and writes forward unchanged. The host enforces scope in both modes.

Assertions verify exact bytes, keys, cursors, tenant access and trusted raw physical enumeration.
The report counts provider calls, returned records and returned payload bytes; it does not
measure allocations or reopen performance. A 60-second diagnostic timeout allows the old slow
path to complete without changing production timeouts. See the
[measured follow-up](../documentation/policy-paging-review-20261028.md).
