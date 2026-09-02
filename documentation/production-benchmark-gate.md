# Production Benchmark And Stress Gate

The production gate is the repeatable decision point for InterfaceDB performance,
concurrency, durability, and embedded-database positioning. Its query cases compare
complete application objects instead of timing only index candidates or SQL scalar
values. Provider-open and raw storage-core cases have different boundaries and are
labelled separately.

The latest curated development snapshot is in [Current Performance Results](performance-results.md). Generated gate reports under `TestResults` are deliberately ignored.

## What The Gate Covers

The gate runs the same deterministic records through:

- InterfaceDB file storage with configurable strong, weak, or metadata-only object caching;
- SQLite in WAL mode through direct `Microsoft.Data.Sqlite` commands and through Dapper;
- optional SQL Server using the configured benchmark connection string.

Every engine is tested for:

- object-per-operation seed throughput, latency, and allocation;
- provider-specific startup time: InterfaceDB object deserialization and in-memory
  index reconstruction versus relational connection-open, before a separate query;
- exact, range, prefix, suffix, literal substring, and broad conjunction queries;
- complete record materialization, with count and checksum validation;
- mixed concurrent reads, inserts, updates, and deletes;
- final whole-dataset equivalence after stress;
- p50, p95, p99, throughput, managed allocations, storage bytes, and file counts;
- forced process termination during a Durable InterfaceDB bulk write;
- startup proposal recovery, stale-lock handling, and a post-recovery sentinel write/read.

## Object-Materialization Contract

Query comparisons do not use `COUNT(*)`, scalar projections, or data-reader row
counts. SQLite and SQL Server select all six stored columns (`Id`, `Name`, `Age`,
`Role`, `Content`, and `Revision`). Every returned row is read column-by-column,
constructed as a complete `GateRecord` C# object, and retained in a `List<GateRecord>`
until the entire result set has been read. The checksum is calculated from those
objects only after materialization completes, and `GC.KeepAlive` keeps the list
observable through the end of the operation.

InterfaceDB likewise retains every returned `IProductionGateRecord` in a result
list and touches every property after enumeration. SQL `WHERE` evaluation remains
inside the SQL engine, just as InterfaceDB index candidate evaluation remains inside
its index manager; only matching result objects cross the application boundary.

Cache mode is part of the comparison contract. InterfaceDB's startup index rebuild
deserializes all stored records and, in `StrongObject` mode, retains them before the
post-startup validation and timed queries. Those query rows therefore reuse live
InterfaceDB object instances while SQL still constructs a new `GateRecord` for every
returned row.
Those rows represent legitimate hot application-cache behavior, but they are not the
canonical storage-to-new-object comparison. `MetadataOnly` is canonical for fresh
managed materialization because every InterfaceDB result is read and deserialized
again. The operating-system page cache can still be warm in that mode; a physical
cold-media claim requires a separately controlled cold-cache run. `WeakObject` is an
opportunistic-cache profile and must not be presented as deterministically hot or cold.

The mixed workload is 70% reads, 15% inserts, 10% updates, and 5% deletes.
Latency sampling uses bounded per-worker reservoir samples so a long soak does not
grow memory without limit.

## Object-Write Contract

The primary seed comparison does not place multiple SQL writes inside one
transaction and does not use SQL bulk-copy APIs. The runner invokes the normal
single-object insert operation once for every source object and waits for it to
complete before starting the next object. SQLite therefore performs one
autocommit transaction per object. InterfaceDB performs one independent object
write per call. Durable comparisons use SQLite `synchronous=FULL` and InterfaceDB
`StorageDurabilityMode.Durable`.

Direct `Microsoft.Data.Sqlite` and Dapper use separate databases with the same WAL,
schema, indexes, durability, records, and query materialization requirements. This
keeps mapper overhead visible without changing SQLite's transaction boundaries.
SQL Server, when enabled, uses the same one-command-per-object seed contract.
Batch transactions and `SqlBulkCopy` are separate capabilities and are not
included in the primary seed metric.

For diagnostic write-cost isolation, add `--include-write-ablations`. The gate
then runs two additional InterfaceDB configurations: one bypasses all search
indexes through the passthrough index manager, and one retains all indexes but
disables cross-process sidecar lock files. These configurations isolate costs;
they are not recommendations for production durability or concurrency.

## Quick Validation

Run a 1,000-record, two-second smoke workload for InterfaceDB Durable/StrongObject
and SQLite FULL durability:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick `
  -Output TestResults\production-gate-quick.json
```

The command writes both JSON and Markdown reports. It exits non-zero for query
equivalence errors, stress exceptions, final-data differences, crash-recovery
failures, or configured baseline regressions.

The default quick run is a correctness/regression smoke and deliberately exercises
the hot `StrongObject` profile. For the canonical fresh-object comparison, run:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache metadata `
  -Output TestResults\production-gate-quick-metadata.json
```

## Production Soak Matrix

The following command runs the recommended 10k and 100k matrix. Each stress
duration applies to every engine/cache/durability/concurrency configuration, so
this is intentionally a long-running test:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- `
  --production-gate `
  --records=10000,100000 `
  --iterations=100 `
  --duration=00:30:00 `
  --concurrency=1,4,16 `
  --cache=strong,weak,metadata `
  --durability=performance,durable `
  --crash-cycles=10 `
  --output .\TestResults\production-gate-full.json
```

Run one million records as a separate capacity test so a failure does not discard
the smaller, more diagnostic matrix:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- `
  --production-gate `
  --records=1000000 `
  --iterations=30 `
  --duration=00:30:00 `
  --concurrency=4,16 `
  --cache=strong,metadata `
  --durability=durable `
  --no-crash-recovery `
  --output .\TestResults\production-gate-million.json
```

## SQL Server

Both direct and Dapper SQLite runs execute by default because SQLite is the closest
operational comparison: both products are embedded/local and do not require a
database service. To add SQL Server, configure `Benchmarks:SqlServer:ConnectionString` in
`StorageBenchmarks.Transactions/appsettings.json` or the equivalent environment
configuration, then add:

```text
--include-sqlserver
```

The SQL Server runner creates an isolated, randomly named table with primary-key,
name, and `(Role, Age)` indexes. It drops that table at the end unless `--keep-data`
is supplied. SQL Server is labelled `Committed`; InterfaceDB Performance mode is
not presented as equivalent to a committed SQL transaction.

## Regression Baselines

Promote a successful JSON report to a baseline, then fail later runs whose matching
p95 metric regresses beyond the allowed percentage:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- `
  --production-gate --quick `
  --baseline .\TestResults\production-gate-baseline.json `
  --max-regression-percent=20
```

Only metrics with the same engine, record count, cache mode, durability mode, and
operation are compared. Keep hardware, power plan, runtime, storage device, and
background workload stable when enforcing a performance baseline.

Each report also carries a versioned materialization contract. A baseline created
before the current six-field FNV object sink is rejected instead of being silently
compared with a different timed workload; regenerate it with the current benchmark code.

## Durability And Crash-Recovery Policy

The gate's InterfaceDB profile explicitly uses:

- `StorageDurabilityMode.Durable` when durable behavior is requested;
- sidecar write locks;
- a 15-second write-lock acquisition timeout;
- a 10-millisecond retry delay;
- a two-second stale-lock threshold.

The shorter stale threshold makes restart behavior explicit and testable. The
library default stale-lock threshold is five minutes, which can delay immediate
restart after an unclean process termination. Production applications should set
this value according to their longest legitimate write and restart objective.
Setting it too low can let a second process mistake a slow live writer for a dead
one. The forced-termination gate waits for its configured two-second threshold
before opening the recovery repository; after that point no `.ready` or `.writing`
proposal may remain.

Logical storage keys generated by this gate end in `-id`. A trailing `-<number>`
is reserved by the current multiple-cardinality file layout for physical object
suffixes and should not be emitted directly by a custom identity factory.

## Cold Versus Warm

`provider-specific-startup-os-cache-warm-pre-query` closes and reconstructs the
database provider. InterfaceDB enumerates and deserializes the stored objects while
rebuilding its catalog and indexes; in `StrongObject` mode that startup work also
populates the strong object cache. The corresponding SQLite or SQL Server measurement
only reopens a connection and executes no row query. The metric ends before any
separately issued result query, so it is a deliberately provider-specific startup
comparison rather than an equal-work object-query comparison. It also does not flush
the operating-system disk cache. A true cold-disk run
must be performed after a machine restart or with an independently controlled cache
procedure and must be reported separately.

## Automated Wrapper

The repository wrapper builds, runs analyzer/unit/integration coverage (including
the existing terminated-process recovery scenarios), and then executes this gate:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick
```

The wrapper uses `--no-restore` so a long gate cannot change its dependency graph
or fail because a package feed becomes unavailable. Run `dotnet restore` explicitly
once after cloning or changing package references.

Use `-IncludeSqlServer` only on a machine where the benchmark SQL Server is
configured and disposable test-table creation is permitted.
