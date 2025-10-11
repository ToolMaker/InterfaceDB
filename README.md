# Idb Sandbox

This repository contains the sample storage stack that powers the `Idb` projects and the `MigrationTool` CLI. The CLI lets you generate versioned storage contracts ("baselines") and scaffold migration stubs that describe how legacy payloads move forward when contract shapes evolve.

## Getting Started

1. **Install requirements**
   * .NET 9 SDK (preview) � the solution targets `net9.0`.
2. **Build everything**
   ```bash
   dotnet build MigrationTool/MigrationTool.csproj
   ```
3. **Run the CLI**
   * Launch without arguments to execute the canned baseline sequence for the `Person` contract:
     ```bash
     dotnet run --project MigrationTool/MigrationTool.csproj
     ```
   * Use subcommands for ad-hoc work (examples below).
   * Want to experiment with the local repository and indexes interactively? Use the playground console:
     ```bash
     dotnet run --project IdbConsolePlayground/IdbConsolePlayground.csproj
     ```

## Baseline Workflow

The `baseline` command creates or advances a contract under `MigrationTool/Contracts/<Contract>/<version>/`.

```bash
dotnet run --project MigrationTool/MigrationTool.csproj -- baseline Dog --contracts-dir Abstractions --storage-dir Migrations
```

Key switches you can supply:

- `--contracts-dir <path>`: where versioned interfaces live (relative to the project root by default).
- `--storage-dir <path>`: base folder used for generated migration stubs.
- `--existing <prompt|skip|force>`: control how the tool behaves when a target version folder already exists.
- `--increment <auto|prompt|skip>` (alias `--increment-mode`): choose whether to auto-increment, prompt, or skip when a previous version is present.
- `--version`, `--increment major|minor|patch`, `--namespace`, `--impl`: fine-tune the semantic versioning and namespace layout.

### Dog Example

> Whenever a new baseline builds on top of an existing version, the tool automatically scaffolds the migration stubs into the directory supplied via `--migration-dir`.

The repo currently ships with a simple `Dog` contract that demonstrates the flow:

1. Generate an initial baseline:
   ```bash
   dotnet run --project MigrationTool/MigrationTool.csproj -- baseline Dog --version 1.0.0 --impl JackRussel
   ```
2. Evolve the contract (adds an `Age` property) and generate the next baseline:
   ```bash
   dotnet run --project MigrationTool/MigrationTool.csproj -- baseline Dog
```
   The folder `MigrationTool/Contracts/Dog/v1_0_1/` (or the path you supplied via `--contracts-dir`) now holds the updated interface and implementation stub.
3. Scaffold migrations only after you are happy with the contract shape:
   ```bash
   dotnet run --project MigrationTool/MigrationTool.csproj -- migrate scaffold Dog --contracts-dir Contracts --storage-dir Storage --migration-dir Migrations
```
   This creates `Migrations/Dog/IDog_v1_0_0_To_v1_0_1_Migration.cs` (relative to the migration directory you specified), which reads legacy payloads and writes the upgraded version.

> **TODO**: Flesh out the `Dog` migration so real data can be upgraded. The sample currently maps the `Name` across and hard-codes `Age` to zero. Add whatever transformation logic your scenario requires and remove the console trace once finalized.

## Migration Workflow

`baseline` deliberately does **not** scaffold migrations. You decide when the contract is ready and invoke:

```bash
dotnet run --project MigrationTool/MigrationTool.csproj -- migrate scaffold <ContractName>
```

The tool walks adjacent versions and creates missing migration stubs under `MigrationTool/Migrations/<Contract>/`. Each stub:

* Derives from `StorageMigrationBase<TContract>` so version metadata is validated at runtime.
* Uses `IStorageMigrationContext.ReadSourceAsync<T>()` to stream legacy payloads.
* Persists new payloads with `WriteAsync` and deletes the old files when done.

Use the generated comment blocks as placeholders for your conversion logic.

## Contributing

* Keep generated code ASCII-only to avoid surprises in reviewers' editors.
* Run `dotnet build MigrationTool/MigrationTool.csproj` before committing.
* When adding new samples, mirror the structure used by `Dog` or `Person` so the scaffolding features stay consistent.

Happy migrating!




