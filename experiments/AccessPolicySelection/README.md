# Access-policy selection experiment — 28 October 2026

**Historical experiment:** the production integration now lives in the public transport.
Use `StorageBenchmarks.AccessPolicy` for current-code comparisons and see
[production validation](../../documentation/access-policy-production-20261028.md).
The measurements below remain the original isolated experiment. Its generator targets the
pre-integration source snapshot identified by `inputs.json`; it deliberately refuses the
new production adapter. The archived handoff is `access-policy-selection-20261028.zip`.

## Outcome

Indexing the existing, server-authorized tenant partition is worthwhile. Recommend a
**per-tenant ordered key index behind a focused abstraction**, with bounded lazy loading
as the general-purpose option and eager loading available for deployments that accept
its startup/memory cost. This is the best measured balance here, not proof of a
universally fastest design. The prototypes are isolated; production code, authorization
rules, storage format, serializer, durability settings and package versions are unchanged
by this experiment.

The array variant wins the pure lookup microbenchmark but does not give a material
advantage over ordered sets in durable reads. Copying a large tenant's array on each
key insertion/deletion is a substantial allocation cost. Do not make it the general
default merely because its read-only lookup is fastest.

### Measured durable results

Windows, AMD Ryzen 9 3950X (16 cores / 32 logical processors), approximately 32 GiB RAM,
.NET 10 SDK 10.0.401, Release. Same existing adapter WriteThrough durability for all
variants; successful payload/journal mutations still use the engine transaction. These
are transport-adapter measurements, **not network/TLS/JWT or SQLite/SQL comparisons**.
There are 100 tenants, 256-byte opaque payloads, and 1,000 records per tenant in the
100,000-record run. Pages mix first, middle and final positions (maximum 64 records);
complete tenant enumeration uses 128-record pages. Timed durable reads validate exact
keys, payloads and continuation cursors equally for every variant.

Medians of four rotated runs:

| 100,000-record store | Current scan | Array | Ordered set | Bounded lazy ordered set |
|---|---:|---:|---:|---:|
| Warm page, ms | 59.589 | 0.235 | 0.236 | 0.239 |
| First page after reopen, ms (excludes open) | 62.123 | 0.768 | 0.777 | 55.263 |
| Full tenant, warm, ms | 475.770 | 4.556 | 4.652 | 4.665 |
| Durable update, ms | 71.443 | 73.409 | 73.333 | 73.338 |
| Durable insert + delete, ms | 144.776 | 144.604 | 145.887 | 145.563 |
| Open, ms | 21.96 | 109.57 | 116.90 | 18.40 |

Lazy indexing reduced warm full-tenant enumeration by **99.0%**, about **102x**, in
this specific workload. At 10,000 total records (100 per tenant), the same operation
fell from 0.977 ms to 0.257 ms, about 74%. These are enumeration gains, not a claim that
every query or the whole database became 102x faster. First access is still a scan in
the lazy variant. Open measurements include run-to-run variation; lazy does not
deliberately optimize engine startup.

Writes did not improve: lazy updates were about **2.7% slower** at 100,000 records,
and insert/delete pairs about **0.5% slower** in these runs. At 10,000 records, write
differences were small. The existing full-store capacity scan remains unchanged and
dominates large-store writes. Index-backed capacity accounting is a separate candidate
for measurement, requiring transaction/failure/reopen tests before changing it.

### Memory and limits

Three fresh processes per variant, 100,000-record real store, retained managed heap:

| Variant | After open | After accessing two tenants |
|---|---:|---:|
| Current scan | 2.52 MiB | 2.53 MiB |
| Array | 21.63 MiB | 21.63 MiB |
| Ordered set | 25.44 MiB | 25.45 MiB |
| Bounded lazy ordered set | 2.53 MiB | 2.99 MiB |

The lazy cache added approximately **0.46 MiB for those two active tenants**. Eager
indexing added approximately 19–23 MiB because it also retains decoded key strings
from the paged primary index. These measurements exclude native and mapped memory.
Do not substitute the smaller synthetic directory-only memory estimates for these.

In the synthetic 100,000-key **single-tenant** case, an array insertion/deletion pair
allocated about 1.6 MB and took 296 microseconds; the ordered set allocated 448 bytes
and took about 0.9 microseconds. Those are index-maintenance costs, not durable writes.
The lazy trial's 10,000-key budget deliberately does not cache such a large tenant:
it retained scan performance, approximately 54.3 ms versus 52.4 ms for the baseline.
Thus the lazy choice has a real limitation, not a universal win. Frequent eviction
also brings back cold scans. Eager indexing or an appropriately larger bounded budget
may suit deployments with large or many simultaneously active tenants.

The 1-million-key runs were **synthetic key-directory tests only**, not a million-record
durable-store qualification. A future production cache should have explicit memory/key
budgets and hit/miss/eviction diagnostics. A persisted ordered selection capability is
another possible way to address cold scans; it was not implemented or measured here.

### Correctness and scope of confidence

All **195 tests passed for each of Scan, Array, Tree and Lazy**: 780 executions of the
same suite, not 780 distinct tests. This includes real local JWT/TLS transport tests,
claim/group isolation and authorization rejection, paging/frame limits, capacity,
mixed batches, duplicate-key rejection, cancellation, committed updates, reopen,
concurrent tenant operations, randomized directory-versus-oracle checks, and lazy
eviction/rebuild checks. No policy check was removed or cached as an authorization
grant. Benchmark reopen checks also verified records from every tenant.

No commercial-provider, live Keycloak, Android, power-cut, cross-process contention or
WAN performance qualification was performed. Server operations still serialize through
the existing adapter gate. This experiment neither fixes that scaling limit nor changes
the concurrency model. The first exploratory test used a duplicate-key batch which the
existing contract correctly rejects; the test was corrected to assert rejection before
testing valid mixed mutations. No product rule was weakened to make the test pass.

See [all measured tables](results-summary.md), the scripts below and local raw JSON/TRX
artifacts under `.tmp/access-policy-selection/results`. The three-variant and tiered
warm-up pilot outputs are retained separately. Final timing processes use the same
non-tiered JIT configuration; tests use ordinary runtime defaults. Baseline page medians
at 100,000 records ranged from 59.26–60.14 ms; lazy ranged from 0.233–0.249 ms.

## Question and requirements

Can mandatory policy selection use an index to avoid scanning unrelated tenants,
without changing authorization, durability, committed-read behavior, or public APIs?

The discussion proposed declaring policy criteria on the stored interface and indexing
those criteria, possibly using a read-only dictionary. A server-owned selection rule can
enforce authorization, provided callers cannot remove it and writes, direct reads,
enumeration, changes, subscriptions and caches respect the same boundary. Never store
JWTs or credentials as selection columns. Only trusted ownership identifiers belong in
data/index metadata. A claim-to-property mapping is a possible future extension, not an
implemented feature of this experiment.

The current policy already resolves one partition per session. This experiment therefore
first tests indexing that existing boundary rather than introducing another ownership
field, changing serialization, or weakening the rules. Production policies continue to
run before transport commands and notifications. The normal unrestricted database
benchmark remains separate from these secured, partitioned workloads.

## Variants

1. **Scan:** unchanged current SegmentCore transport adapter: copy all keys, filter the
   partition, sort its keys, select a page.
2. **Array:** dictionary keyed by server-resolved partition, with a sorted key array per
   partition; binary-search pages and copy the affected array on key insert/delete.
3. **Tree:** the same dictionary with an ordinal sorted set per partition; ordered range
   traversal for pages and incremental insert/delete.
4. **Lazy:** a bounded directory of ordered sets, populated on first access to a tenant;
   least-recently-used eviction at 10,000 retained keys or 16 partitions in this trial.
   Larger individual tenants retain scanning. This variant was added after the first
   three-variant trial exposed the cost of retaining keys from paged storage indexes.

The candidates depend on a focused key-directory abstraction, retain the existing
adapter gate, rebuild from committed keys on open (or first access for Lazy), and update after the durable commit.
An exception invalidates the directory; the next indexed read must rebuild from the
store, not trust stale membership. The directory contains keys only, never payloads or
security grants. Arbitrary noncanonical internal prefixes and unrestricted enumeration
retain the original implementation. Original write-capacity validation is unchanged so
this trial isolates enumeration indexing from capacity-count optimizations.

## Trial and decision criteria

- Same current working-tree code, Release build, same machine and durability settings.
- Correctness tests use normal runtime settings. Final timing processes disable tiered
  compilation equally for all variants (`DOTNET_TieredCompilation=0`) because pilot
  runs showed substantial within-run warm-up effects. Earlier pilot reports are kept.
- Key-selection microbenchmarks at 10k, 100k and 1m keys; report build time, retained
  memory, allocations, page lookup and insert/delete cost, including a large tenant.
- Real durable adapter runs at 10k and 100k records, with four rotating variant orders,
  repeated samples, first/middle/last pages, complete tenant enumeration, updates and
  insert/delete pairs. Validate exact keys, payloads, pagination and reopen results.
  Report first access separately from warm reads. Measure managed heap in three fresh
  processes per variant, both before and after accessing two tenants.
- Reuse transport correctness/security tests against the generated candidate assembly;
  add candidate-specific mixed mutation, cancellation, capacity and isolation checks.
- Do not compare these results with SQLite or SQL Server: this is a targeted four-variant
  experiment. Do not claim a database-wide speedup, power-loss qualification or a
  replacement for application/device testing.
- The microbenchmark uses an in-memory key map to isolate selection/index costs. It
  does not reproduce the engine's paged primary-index snapshot. Its memory numbers
  share pre-existing key strings; the fresh-process real-store measurements include
  keys retained by the new directory. Managed heap excludes native/mapped memory.
- Prefer measured gains with acceptable write/startup/memory cost. No production
  implementation is changed or published by this experiment.

## Reproduce

Run `./experiments/AccessPolicySelection/run.ps1` from PowerShell. It runs correctness
suites before benchmarks, sequentially, and stores a new timestamped result directory.
For individual commands, first run `python experiments/AccessPolicySelection/prepare.py`.
This creates isolated projects under `.tmp/access-policy-selection` and records hashes of
the exact source inputs. The generated transport assembly links current source except
for two generated adapter copies containing the experimental hooks. It is non-packable.
See the final report for the exact commands and collected results.

## How interface-declared criteria would fit later

The interface/server registration may declare trusted ownership criteria such as a
tenant identifier, plus the claim or server lookup supplying the permitted values.
An exact mandatory selection plan can request indexes through a storage capability
abstraction; concrete providers are registered through DI. The coordinator should not
downcast to File, SegmentCore or another provider. Index only declared criteria, not
every claim or property. A read-only dictionary view is useful to consumers, but does
not itself provide ordering, persistence, bounded memory or commit synchronization.

Start with precisely defined equality, bounded membership and conjunction rules. Do not
silently omit a rule that cannot be expressed by an index. Writes must validate both
existing ownership and proposed ownership, and direct reads, aggregates, transactions,
notifications and caches need the same enforcement. Existing unpartitioned data needs
an explicit ownership/backfill decision. Arbitrary per-object authorization remains a
separate extension; none of those API or storage-format changes is made here.

The experimental dictionary caches derived key membership, never a permission grant.
The server still reevaluates the access policy for operations/notifications. LRU eviction
can make selection slower but cannot broaden the authorized data boundary.
