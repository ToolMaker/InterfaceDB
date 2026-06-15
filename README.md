# InterfaceDB

InterfaceDB is a sandbox for an interface-first object storage stack. The current baseline is a simple flat file-backed repository for desktop/mobile style local storage, plus a contract baseline and migration scaffolding CLI, a sample storage project, and performance benchmarks.

## Solution layout

- `Idb.Libraries/`
  Core abstractions and the default file-system implementation. The library uses MessagePack envelopes, an in-memory envelope cache, and optional migration strategies.
- `MigrationTool/`
  CLI for creating versioned contract baselines and scaffolding migration classes between adjacent versions.
- `IdbDemoStorage/`
  Sample `Person` contract, concrete storage types, contract options, and a sample migration.
- `InterfaceDb.ConsoleApp/`
  Small console demo that writes and queries `Person` records through `IRepository<TContract>`.
- `PerformanceCommon/`
  Shared benchmark data generation helpers.
- `StorageBenchmarks/`
  Throughput comparison between the file-system repository, SQL Server, Dapper, and EF Core.
- `StorageBenchmarks.Transactions/`
  Transaction-oriented benchmark variant for the same `Person` sample payloads.

## Build and run

1. Install the .NET 10 SDK.
2. Build the solution:

```bash
dotnet build InterfaceDB.sln
```

3. Run the console demo:

```bash
dotnet run --project InterfaceDb.ConsoleApp/InterfaceDb.ConsoleApp.csproj
```

The console app writes 500 sample `Person` records to a local `Data/` folder under its output directory, then runs a few simple existence and query checks.

4. Run the migration tool:

```bash
dotnet run --project MigrationTool/MigrationTool.csproj -- <command>
```

## Storage model

The storage stack is built around versioned interfaces:

1. Define a contract interface, make it inherit `IStoredContract<TContract>`, and decorate it with `[StorageScope(VersionTag = "...")]`.
2. Implement the contract in one or more concrete classes.
3. Register the repository with `AddRepository<TContract>(...)` or `AddFileRepository<TContract>(...)`.
4. Persist and query contracts through `IRepository<TContract>`.
5. When the contract shape changes, generate the next baseline and implement the scaffolded migration logic.

`IStoredContract<TContract>` exposes a getter-only `Envelope`. Concrete implementations should use a non-public setter such as `public IObjectEnvelope<TContract>? Envelope { get; private set; }`. The library sets and clears that property internally during wrap and unwrap operations.

The file-backed implementation stores MessagePack payloads on disk under a versioned directory layout.

Flat file-system layout:

```text
Data/
  1.0.0/
    <key>.mpk
```

This flat layout is the only active filesystem layout. Higher-throughput storage backends, such as SQL/document/object database envelope stores, are future roadmap work. `StoragePolicy` controls write concurrency, cache hydration, notifications, sidecar write locks, and file change watching.

Storage identity is factory-driven. `StorageObjectCardinality.Single` allows one object per factory-resolved path and rejects detached duplicate creation, while `Multiple` gives every new envelope-less object a unique physical key. Custom `IEnvelopeKeyAndLocationFactory<TContract>` implementations can derive keys from object state without requiring a prescribed ID property.

Repository queries support streaming and bounded cardinality:

```csharp
await foreach (var person in repository.QueryManyAsync(x => x.Role == "Student"))
{
}

var first = await repository.QueryFirstAsync(x => x.Role == "Student");
var exactlyOne = await repository.QuerySingleAsync(x => x.Name == "Jane");
var optional = await repository.QuerySingleOrDefaultAsync(x => x.Name == "Jane");
```

Single-result methods accept optional exception factories for cardinality violations.

Optional encryption at rest is applied to the complete serialized envelope. The default `EncryptionNone` implementation keeps normal `IDB2` files unchanged. AES-GCM can be enabled before repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption(keyProvider);
services.AddRepository<IMyContract>(options);
```

AES-GCM files use an authenticated `IDBE` outer frame containing the encrypted `IDB2` payload.

## Sample contract in this repo

The sample storage project currently centers on `Person`:

- Contracts live under `IdbDemoStorage/Contracts/Person/`.
- Runtime implementations live under `IdbDemoStorage/Storage/Person/`.
- Contract options live in `IdbDemoStorage/Options/PersonOptions.cs`.
- Migration stubs live under `IdbDemoStorage/Migrations/Person/`.

The sample includes two contract versions:

- `v1_0_0` defines `Id`, `Name`, `Age`, and `Role`.
- `v1_0_1` adds `Surname`.

There is already a scaffolded migration at `IdbDemoStorage/Migrations/Person/Person_v1_0_0_To_v1_0_1_Migration.cs`. It is still sample code and should be treated as a starting point, not finished migration logic.

## Migration tool workflow

The `baseline` command creates or advances a contract under a contracts root such as `IdbDemoStorage/Contracts/<Contract>/vX_Y_Z/`.

The command requires exactly one of:

- `--contracts-dir <path>`
- `--config-file-dir <path>`

Important behavior:

- `--contracts-dir` should point at the contracts root folder, not a single version folder.
- `--config-file-dir` can point at a specific `*Options.cs` file or a directory that contains one.
- When the tool increments an existing contract version, it also scaffolds any missing adjacent migration files automatically.
- `migrate scaffold` can be used later to backfill missing migration files without creating a new baseline.
- If a contract options file does not already exist, the tool scaffolds one into a sibling `Options/` folder.

### Use absolute paths for CLI examples

The tool normalizes user-supplied paths from its executable directory. In practice, the least surprising way to run it is to pass absolute paths.

PowerShell example:

```powershell
$repo = (Resolve-Path .).Path
$contracts = Join-Path $repo "IdbDemoStorage\Contracts"
$storage = Join-Path $repo "IdbDemoStorage\Storage"
$migrations = Join-Path $repo "IdbDemoStorage\Migrations"
$personOptions = Join-Path $repo "IdbDemoStorage\Options\PersonOptions.cs"
```

### Create an initial contract baseline

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  baseline Dog `
  --contracts-dir $contracts `
  --storage-dir $storage `
  --migration-dir $migrations `
  --impl Dog `
  --version 1.0.0
```

This creates a new `Dog/v1_0_0/` baseline folder under the contracts root, prepares matching storage and migration folders, and scaffolds a contract options file if one does not already exist.

### Increment an existing contract

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  baseline Person `
  --config-file-dir $personOptions `
  --increment patch
```

If a previous baseline exists, the tool copies the latest version into the next semantic-version folder, updates the namespace and `VersionTag`, and scaffolds any missing adjacent migration classes.

### Scaffold migrations without creating a new baseline

```powershell
dotnet run --project MigrationTool/MigrationTool.csproj -- `
  migrate scaffold Person `
  --config-file-dir $personOptions
```

This walks the known adjacent baseline versions and creates any missing migration stubs under the configured migrations root.

## Benchmarks

The benchmark projects are useful for comparing the file-backed repository against database-backed inserts, but they are not zero-config:

- `StorageBenchmarks/` and `StorageBenchmarks.Transactions/` expect a SQL Server connection string in their `appsettings.json` files or environment variables.
- The file-system benchmark setup currently writes under `G:\PerformanceTest\...`. Update the code if that path does not exist on your machine.

Run them with:

```bash
dotnet run --project StorageBenchmarks/StorageBenchmarks.csproj -c Release
dotnet run --project StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj -c Release
```

## Current state

- The solution builds cleanly with `dotnet build InterfaceDB.sln`.
- The repository currently has no dedicated test project in the solution.
- Some sample namespaces still use the `MigrationTool.Contracts.*` naming pattern even when the files live under `IdbDemoStorage/`. Follow the actual project paths when navigating the repo.
