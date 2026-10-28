# Access-policy production integration — 28 October 2026

The bounded partition index and interface-declared ownership policy are implemented through
DI and focused abstractions. The earlier isolated experiment selected the design; this follow-up
tests the production path over TCP with TLS and validated JWTs. No durability defaults, storage
format, revision protection, transaction boundaries or committed-read copy semantics changed.
The access-policy wire/cache migration remains the breaking preview change described in the
[integration guide](data-access-policies.md). Nothing in this work publishes a package.

For practical choices based on these results, see
[choosing a policy for performance](data-access-policies.md#choosing-a-policy-for-performance).
That guidance distinguishes measured index gains from unmeasured comparisons among policy types.

## Delivered behavior

- `IPartitionKeyIndex` and its factory select committed partition keys. The default bounded lazy
  ordered set has LRU eviction, key/partition/estimated-byte budgets, safe scan fallback, commit-time
  updates and invalidation/rebuild after failures. Disabled and eager modes are configurable.
- `[StorageAccessClaim]` declares ownership on an interface property and automatically adds an
  exact local query index. `ContractDataAccessPolicy<T>` validates matching token claims and checks
  all payload ownership fields before any batch data is written. A declaration alone is not an
  authorization policy; registration on the server is required.
- Keyed DI supports separate policies and serializers per registration. The same configured
  serializer decodes a record once for the policy, with authenticated encryption context when
  applicable. The server commits its original bytes. Reads use the authorized partition without
  domain decoding for this policy. Custom additional validators still run independently.
- No-policy local use and unrestricted transport selection retain their existing behavior.
  Token lifecycle and group-membership revocation remain identity-provider concerns. The index
  caches no permission grants, and each operation/notification still evaluates authorization.

## Production benchmark

Windows 10.0.19045, .NET 10.0.12, SDK 10.0.401, 32 logical processors, Release,
`DOTNET_TieredCompilation=0`. Client and server share a process and communicate over loopback TLS.
RS256 JWTs are cryptographically validated against a generated local signing key; no external
Keycloak service is contacted. The existing transport WriteThrough setting is identical in every
variant. No tests or other InterfaceDB benchmark ran concurrently.

Three alternating-order rounds compare **index disabled** with **bounded lazy indexing**, with
identical authorization and data. Every mode reopens the same store. Each record contains an
ownership string, integer and 256-character text inside its normal envelope. Exact bytes, keys,
ordering and cursors are checked in timed reads using prebuilt expectations. All tenants and
decoded fields are checked outside timing after seeding and again after a separate final reopen.
All checks passed. Raw results include all runs and outliers; `Complete` is true.

Medians of each round's median, milliseconds:

| Workload | Index disabled | Bounded lazy | Observation |
|---|---:|---:|---|
| 100,000 records / 100 tenants: warm page, 128 records | 9.801 | 0.745 | 92.4% lower latency |
| Same store: full tenant, 1,000 records | 81.505 | 6.653 | 91.8% lower latency, about 12.3x |
| Same store: single durable update | 20.840 | 21.103 | 1.3% higher median; no write-speed claim |
| Same store: batch of 128 durable updates | 22.582 | 21.815 | Small difference amid run variation |
| Same store: first page after reopen | 10.677 | 11.625 | Cold access still scans and builds the index |
| 10,000 records / 100 tenants: full tenant, 100 records | 1.255 | 0.612 | 51.2% lower latency |
| 10,000 records / one protected tenant: full 10,000 records | 427.844 | 63.246 | 85.2% lower latency |
| 10,000 unrestricted records: full enumeration | 245.422 | 247.644 | Existing path; approximately 0.9% variation |

The 100,000-record page allocated approximately 1,215,048 bytes with scanning and 409,714 bytes
with the index, a 66.3% reduction. This is process-wide allocation including client, server and
verification, not retained heap. The active 1,000-key partition's retained estimate was 260,398
bytes. Retained estimates for 100 and 10,000 keys were 26,398 and 2,600,398 bytes respectively.
These are the index's budget estimates, not a fresh process-memory measurement. Eviction and
oversized-partition fallback are exercised by correctness tests, not these warm timing cases.

The 100,000-record store took median 789 ms to open with indexing disabled and 820 ms in lazy
mode. TLS/JWT connect medians were about 6.8–8.6 ms across scenarios. Neither startup nor handshake
is an optimization claim. "First after reopen" does not flush OS or device caches. Write samples
vary considerably: one scanning round's update median was 31.5 ms versus about 20.8 ms in the
other two. All samples are retained; the small write differences do not establish an improvement.

### Cost of the policy itself

The unrestricted baseline still uses the same TLS/JWT authentication. Comparing identical
10,000-row, single-dataset workloads in lazy mode:

| Operation | Unrestricted | Contract policy | Difference |
|---|---:|---:|---:|
| Single Get | 0.102 ms | 0.123 ms | About 0.022 ms more |
| Single durable update | 3.966 ms | 5.382 ms | About 36% more |
| Batch of 128 updates | 4.729 ms | 6.310 ms | About 33% more |
| Full 10,000-row read | 247.644 ms | 63.246 ms | Faster because scoped selection is indexed |

Authorization is not free. These differences include policy evaluation, write ownership decoding,
longer physical partition keys and partition bookkeeping; they do not isolate one function's cost.
Existing capacity checks still scan store metadata and dominate large-store writes. No capacity
accounting or write-durability shortcut was introduced. The normal unrestricted database/SQL
benchmarks remain unrestricted; do not substitute policy timings into those comparisons.

This is encoded-envelope transport performance, not typed `SearchAsync`, cache, SQL or WAN
performance. WAN latency, live Keycloak issuance/refresh, high concurrency, million-record scale,
mobile hardware and real power loss were not measured by this run. The earlier prototype has a
different workload and must not be compared numerically as an additional before/after build.

## Verification

| Public suite | Passed | Skipped |
|---|---:|---:|
| Core | 393 | 10 |
| SegmentCore | 216 | 2 |
| Authentication | 61 | 0 |
| Transport | 204 | 0 |
| Total | 874 | 12 |

The skipped cases are platform-specific existing tests on this Windows host. New cases cover
automatic exact indexes, encrypted and unencrypted write checks, tenant sharing/isolation,
ownership-forgery rejection before any batch/clock write, no read-side domain decoding, composite
and inherited criteria, ambiguous declarations, keyed-registration isolation, randomized index
mutations, budgets/eviction, cancellation, rejected batches, reopen, post-commit index failure
and an injected index returning another tenant's key. Commercial verification is recorded in
the private repository. Live Keycloak tests were not run.

The first local-index fixture omitted repository initialization, and the first injected-index
fixture attempted to seed a reserved key directly. Those fixture errors were corrected to use
the normal lifecycle and scoped API. The final public transport suite passed all 204 tests.

## Reproduction and evidence

- [Runner and method](../StorageBenchmarks.AccessPolicy/README.md)
- [Raw production runs](../StorageBenchmarks.AccessPolicy/results-20261028.json)
- [Source hashes](../StorageBenchmarks.AccessPolicy/inputs-20261028.json)
- Local TRX files: `.tmp/access-policy-production/results/`.
- Local data and complete benchmark output: `.tmp/access-policy-production/benchmark-final/`.

The source manifest includes uncommitted files; HEAD alone does not identify this snapshot.
Private source is excluded. The implementation and documentation are ready for review; publishing
requires the normal release process and does not gain Android or power-cut qualification from
these tests.
