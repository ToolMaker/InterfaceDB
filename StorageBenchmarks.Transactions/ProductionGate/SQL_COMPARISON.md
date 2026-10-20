# Full repository comparison with SQL

The `--sql-comparison` runner measures the public FileSystem and SegmentCore
repositories, SQLite through Microsoft.Data.Sqlite and Dapper, and optionally the
configured SQL Server. It uses `IProductionGateStore` adapters, an optional
`IExistenceGateStore` capability, and a registration delegate for the SegmentCore
composition root. It does not change database product code.

```powershell
dotnet build StorageBenchmarks.Transactions -c Release
dotnet run --no-build -c Release --project StorageBenchmarks.Transactions -- --sql-comparison --records=64 --iterations=3 --mutation-limit=16 --include-sqlserver --output=artifacts/sql-smoke.json
dotnet run --no-build -c Release --project StorageBenchmarks.Transactions -- --sql-comparison --records=10000 --iterations=50 --cache=MetadataOnly --mutation-limit=1000 --include-sqlserver --output=artifacts/sql-current.json
```

SQL Server uses `Benchmarks:SqlServer:ConnectionString` and creates and drops only
its own randomly named benchmark table. Its database must already exist. The
runner rejects enabled delayed durability. Omit `--include-sqlserver` to run only
the embedded engines. Use a dedicated benchmark database; the runner clears its
SQL connection pools for the connection-reopen measurement.

## Measurements and assertions

- Independent durable inserts, six complete-object query shapes, exact existence,
  indexed-field updates, deletes, and provider-specific initialization/reopen.
- Every query consumes all six fields and verifies both count and checksum.
  Every mutation checks its result. Full field-by-field snapshots are checked
  after seeding, reopening, updating, reopening the updated store, and finally
  deleting/reopening. A captured existence probe must become false after deletion.
- Five query warmups and configurable measured iterations. Existence has sixteen
  warmups and 2,000 measurements. Updates have one warmup and up to 1,000
  evenly spaced distinct keys by default. The first timed update changes the
  warmup value again.
- Raw timing samples, process-wide allocations, assembly hashes, runtime,
  engine versions, SQLite query plans and SQL Server delayed-durability status
  are saved in JSON. An exception fails the run.
- Allocation counters measure managed allocations in the benchmark process.
  They do not measure retained memory, SQLite native memory, or SQL Server's
  separate process; they cannot establish an overall memory-efficiency ranking.
- The fixed `GateRecord` fixture has short text fields, unlike the separate
  512-byte repository review fixture. Absolute times from those fixtures are
  not interchangeable.

## Configuration and comparison boundaries

File uses its durable per-object publication protocol. SegmentCore uses four
stripes, mutation WAL, explicit flush, and disabled background maintenance.
Both retain revision checks and index maintenance. Notifications, file watchers
and telemetry are disabled for this benchmark. SQLite uses WAL and synchronous
FULL; the adapters retain open connections between calls. SQL Server obtains a
pooled connection per call. No operating-system cache eviction is performed.

SQL has an Id primary key and Age, Role+Age, and Name indexes. SQLite's fixed
prefix predicate uses case-sensitive `GLOB`, and the runner verifies that its
range and prefix queries use the intended indexes. The older production gate
retains its original index/query settings; the new options are opt-in to this
runner. Historical SQL results therefore are not a controlled code-only baseline.

InterfaceDB also has dedicated suffix/contains search indexes. These SQL
controls use ordinary B-tree indexes, without full-text/trigram or reversed-text
indexes. Suffix/substring comparisons consequently include a difference in
index capabilities. The generated fixture is ASCII; this does not establish
equivalent Unicode/collation semantics.

The inherited prefix/suffix workloads call the one-argument `StartsWith` and
`EndsWith` methods. Those are culture-sensitive and InterfaceDB uses a scan to
preserve their meaning. The ordinal prefix/suffix indexes require explicit
`StringComparison.Ordinal`. Preserve the inherited rows when comparing history;
measure the ordinal alternatives separately:

```powershell
dotnet run --no-build -c Release --project StorageBenchmarks.Transactions -- --sql-comparison --records=10000 --iterations=50 --cache=MetadataOnly --only-interfacedb --queries-only --compare-ordinal-strings --output=artifacts/sql-ordinal-probe.json
```

This probe measures the default and ordinal predicates on the same fresh-object
dataset, records their `ExplainSearch` plans, verifies counts/checksums for each,
and reopens to check the dataset again. Repeat in separate processes and reverse
engine order with `--reverse`. Use ordinal comparison when those are the intended
application semantics; changing it is not a transparent linguistic optimization.

MetadataOnly is the fresh-object comparison. StrongObject can reuse mutable
instances. WeakObject is opportunistic, with no forced collection or pinning in
this runner; report it separately. Validation snapshots and reopen can populate
caches. Cached query results are not fresh SQL row materialization.

The update adapter locates the InterfaceDB entity and then saves it with revision
protection. SQL executes UPDATE by primary key; it does not emulate InterfaceDB's
application revision token. Deletion similarly includes InterfaceDB entity
lookup. These are application adapter costs, not identical low-level work.
Existence resolves the InterfaceDB object's envelope before timing and measures
key membership on every engine, not payload integrity.

Repository startup includes recovery and index initialization; relational reopen
opens connections without rebuilding their indexes. Do not rank these as
equivalent startup operations. Neither is a per-query charge.

## Repeated before/current runs

### Isolating the SegmentCore flush strategy

`--segment-durability=ExplicitFlush` (default) or `WriteThrough` changes only
SegmentCore's configured commit barrier. The report records the chosen strategy.
`--skip-file` omits File from this specific experiment; it leaves the SQLite and
optional SQL Server controls intact. Run the same frozen binaries for both modes:

```powershell
./Scripts/Measure-RepositoryFlushModes.ps1 -BinaryDirectory current-bin -OutputDirectory artifacts/flush-modes -IncludeSqlServer
```

This runs three processes per mode, alternates the mode order, and reverses engine
order in the second round. All full-object, update/delete and reopen assertions
remain enabled. Run it without concurrent builds or tests. A latency advantage
does not establish equivalent behavior under a physical power cut or authorize
changing the product's default durability strategy.

### Profiling a single query shape

`--query-kind=Range` selects just the range query. Combine it with
`--queries-only --only-interfacedb --skip-file` to profile SegmentCore returning
complete fresh objects:

```powershell
dotnet-trace collect --profile dotnet-sampled-thread-time --format Speedscope --output artifacts/range.nettrace -- dotnet current-bin/StorageBenchmarks.Transactions.dll --sql-comparison --records=10000 --iterations=2000 --cache=MetadataOnly --only-interfacedb --skip-file --queries-only --query-kind=Range --segment-durability=WriteThrough --output=artifacts/range.json
```

The process also seeds, initializes and verifies the store; separate those stacks
from the measured query loop when interpreting the trace. The sampled profile
locates hot paths; it does not precisely apportion per-query CPU or I/O latency.
Keep profiled timings separate from unprofiled benchmark results.

### Comparing production builds

Prepare two immutable Release output directories with exactly the same harness
and dependencies and the intended core/provider DLLs. Record how those DLLs
were obtained; a retained development build is not a released NuGet package.

```powershell
./Scripts/Measure-RepositorySql.ps1 -BaselineDirectory before-bin -CurrentDirectory current-bin -OutputDirectory artifacts/sql-comparison -IncludeSqlServer
python Scripts/summarize_repository_sql.py artifacts/sql-comparison --output artifacts/sql-summary.json
```

The script runs three fresh processes for each build and size, alternates build
and size order, and reverses engine order in the second round. The 1,000-record
matrix covers all three cache modes; the 10,000-record matrix covers MetadataOnly.
Run sequentially without competing builds or benchmarks. Ordinary workstation
activity can still affect measurements.

The summary reports medians of per-process p50s and paired directions. Its 10%
descriptive band is not a statistical equivalence test. SQL control timings can
also move between the two labels even though their code is identical; use that
variation when interpreting small changes. Three processes do not establish
production p95/p99 guarantees.

The standalone SegmentCore transaction runner remains a separate storage-core
comparison. It includes batch commits, checkpointing and full-object decoding,
but excludes IRepository and secondary indexes. Do not transfer its rankings
to the full repository.
