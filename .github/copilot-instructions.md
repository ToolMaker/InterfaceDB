# Repository instructions

InterfaceDB targets .NET 10 and is an interface-first object repository with a file-backed provider.

## Design boundary

Preserve this dependency direction:

```text
IRepository<TContract>
  -> IIndexManager<TContract>
    -> IEnvelopeRepository<TContract>
      -> provider implementation
```

Application callers should not depend on `FileObjectReader`, `FileObjectWriter`, paths, MessagePack, or index implementation classes. Provider work belongs below `IEnvelopeRepository<TContract>`.

Search indexes return physical envelope keys. The complete predicate remains the final correctness check unless a planner can prove full semantic coverage. Do not route culture-sensitive string operations through ordinal indexes.

## Build and verification

```powershell
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore
dotnet test InterfaceDb.Tests\InterfaceDb.Tests.csproj -c Release --no-build --no-restore
dotnet test Idb.Analyzers.Tests\Idb.Analyzers.Tests.csproj -c Release --no-build --no-restore
dotnet run -c Release --no-build --project InterfaceDb.IntegrationTests
```

The executable integration harness covers file lifecycle, cardinality, caches, indexes, external change reconciliation, cross-process writes, recovery, migrations, and encryption.

Run the embedded SQLite comparison with:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick
```

Benchmark comparisons must materialize complete objects for every engine. A SQL scalar/select timing is not equivalent to an InterfaceDB query.

## Project layout

- `Idb.Libraries`: public abstractions and file-provider implementation.
- `InterfaceDb.Tests`: MSTest structure and locking tests.
- `InterfaceDb.IntegrationTests`: executable end-to-end harness.
- `Idb.Analyzers` and `Idb.Analyzers.Tests`: immutable contract baseline diagnostics.
- `MigrationTool`: baseline, migration, and encryption maintenance CLI.
- `IdbDemoStorage` and `InterfaceDb.ConsoleApp`: executable example.
- `StorageBenchmarks.Transactions`: fair object materialization, stress, recovery, and storage comparisons.
- `ContainsIndexBenchmarks`: isolated contains-index research.
- `documentation`: public product and developer documentation.

## Storage conventions

- Contracts inherit `IStoredContract<TContract>` and have `[StorageScope]`.
- Implementations expose `IObjectEnvelope<TContract>? Envelope { get; private set; }`.
- Search indexes are declared with `[SearchIndex]`; there is no configuration index collection.
- `StorageDurabilityMode.Performance` writes canonical files directly.
- `StorageDurabilityMode.Durable` uses writer leases, sidecar locks, and proposal publication/recovery.
- Do not remove a lock, publication step, flush policy, or candidate validation based only on a microbenchmark. Add a correctness test and rerun the production gate.
- Version folders use `v1_0_0` naming in source and `1.0.0` in persisted active-version directories.

## Repository hygiene

Do not commit `bin`, `obj`, raw BenchmarkDotNet artifacts, `TestResults`, `.nettrace`, SpeedScope output, test data, secrets, connection strings, or encryption keys. Curated benchmark results belong in `documentation/performance-results.md` with environment and methodology.

The default branch may contain unrelated user work. Preserve it and keep changes scoped.
