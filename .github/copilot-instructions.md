# Copilot / AI agent instructions for InterfaceDB

Purpose: provide short, accurate repo-specific guidance so an AI coding agent can work productively without rediscovering the current project layout.

## Quick start

- Target framework: .NET 9 (`net9.0`).
- Build the full solution:
  - `dotnet build InterfaceDB.sln`
- Run the console demo:
  - `dotnet run --project InterfaceDb.ConsoleApp/InterfaceDb.ConsoleApp.csproj`
- Run the migration CLI:
  - `dotnet run --project MigrationTool/MigrationTool.csproj -- <command>`

## Current repo shape

- `Idb.Libraries/`
  Core abstractions plus the file-backed repository implementation.
- `MigrationTool/`
  CLI for contract baseline generation and migration scaffolding.
- `IdbDemoStorage/`
  Sample `Person` contracts, storage implementations, options, and migrations.
- `InterfaceDb.ConsoleApp/`
  Demo app for repository usage.
- `PerformanceCommon/`
  Shared benchmark data helpers.
- `StorageBenchmarks/` and `StorageBenchmarks.Transactions/`
  Performance comparisons against SQL Server, Dapper, and EF Core.

Do not assume older projects still exist. `IdbConsolePlayground`, `RepoTcpHost`, `RepoTcpConsole`, and test projects referenced in older docs are not part of the current solution.

## Storage and migration conventions

- Version folders use underscores: `v1_0_0`, `v1_0_1`, `v2_0_0`.
- Storage-facing contract interfaces should inherit `IStoredContract<TContract>`.
- Concrete implementations should expose `Envelope` with a non-public setter, for example `public IObjectEnvelope<TContract>? Envelope { get; private set; }`.
- The active sample contract is `Person` under `IdbDemoStorage/Contracts/Person/`.
- Storage implementations for the sample live under `IdbDemoStorage/Storage/Person/`.
- Migration classes for the sample live under `IdbDemoStorage/Migrations/Person/`.
- Some namespaces still use the `MigrationTool.Contracts.*` pattern even when the files are stored under `IdbDemoStorage/`.

## MigrationTool behavior

- `baseline` requires exactly one of:
  - `--contracts-dir <path>`
  - `--config-file-dir <path>`
- `--contracts-dir` points at the contracts root, not an individual version folder.
- `--config-file-dir` may point to a single `*Options.cs` file or a directory containing one.
- When `baseline` increments an existing contract, the tool also scaffolds missing adjacent migration files automatically.
- `migrate scaffold` backfills missing migration files without creating a new baseline.
- The tool can scaffold a contract options file into a sibling `Options/` folder when one does not already exist.

Prefer absolute paths in examples and tooling. The CLI normalizes user-supplied paths from its executable directory, so repo-relative arguments are easy to misread.

## Key files

- CLI entry point: `MigrationTool/Program.cs`
- Baseline generation: `MigrationTool/BaselineGenerator.cs`
- Migration scaffolding: `MigrationTool/MigrationScaffolder.cs`
- Repository registration: `Idb.Libraries/Implementation/Extentions/RepositoryServiceCollectionExtensions.cs`
- File repository core: `Idb.Libraries/Implementation/Repositories/FileEnvelopeRepository.cs`
- Writer and reader: `Idb.Libraries/Implementation/Serialization/FileObjectWriter.cs`, `Idb.Libraries/Implementation/Serialization/FileObjectReader.cs`
- Sample options: `IdbDemoStorage/Options/PersonOptions.cs`
- Demo app: `InterfaceDb.ConsoleApp/Program.cs`

## Build and verification guidance

- Build solution:
  - `dotnet build InterfaceDB.sln`
- Run console demo:
  - `dotnet run --project InterfaceDb.ConsoleApp/InterfaceDb.ConsoleApp.csproj`
- Run benchmarks:
  - `dotnet run --project StorageBenchmarks/StorageBenchmarks.csproj -c Release`
  - `dotnet run --project StorageBenchmarks.Transactions/StorageBenchmarks.Transactions.csproj -c Release`

There is no dedicated test project in the current solution. Validation is primarily build plus targeted app or benchmark execution.

## Benchmark prerequisites

- SQL benchmarks require a valid connection string in:
  - `StorageBenchmarks/appsettings.json`
  - `StorageBenchmarks.Transactions/appsettings.json`
- File benchmarks currently write under `G:\PerformanceTest\...`. Change the code if that drive is unavailable.

## Safety notes

- Do not describe the old README as authoritative without checking the code first.
- Do not assume relative CLI paths are repo-relative.
- Treat scaffolded migrations as placeholders until the conversion logic is implemented.
