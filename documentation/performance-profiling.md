# InterfaceDB Performance Profiling

This document defines the current profiling workflow for finding InterfaceDB bottlenecks before making more performance changes.

## Why Trace Instead Of Guessing

The benchmark results show that InterfaceDB file storage is competitive enough to continue, but the remaining write gap is not obviously caused by one thing. The write path includes:

- envelope creation;
- storage identity resolution;
- MessagePack envelope serialization;
- optional encryption;
- sidecar write coordination;
- file open/write/replace behavior;
- envelope cache updates;
- optional search index updates;
- lifecycle notifications and file watcher reconciliation.

`dotnet-trace` lets us see where CPU time and allocations are actually spent before changing code.

## Install Tooling

Install the tracing tool once:

```powershell
dotnet tool install --global dotnet-trace
```

Optional viewers:

- Visual Studio can open `.nettrace` files.
- PerfView can open `.nettrace` files and is useful for CPU stacks and allocation analysis.

## Direct Profiling Targets

`StorageBenchmarks.Transactions` includes direct profiling targets. These are not BenchmarkDotNet measurements. They run the same workload shape directly so `dotnet-trace` captures the hot process instead of the BenchmarkDotNet launcher.

Supported targets:

```text
filesystem-store
filesystem-indexed-store
filesystem-indexed-query
filesystem-indexed-query-hot
filesystem-selective-indexed-query
filesystem-selective-indexed-query-hot
```

Run one without tracing first to verify setup:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- --profile filesystem-store
```

For cleaner traces, build once and trace the compiled DLL directly. This avoids capturing `dotnet run` and MSBuild launcher activity:

```powershell
dotnet build .\StorageBenchmarks.Transactions -c Release
```

## CPU Trace

Collect a CPU sampling trace for the non-indexed file write path.

`dotnet-trace` 9 defaults to `dotnet-common` plus `dotnet-sampled-thread-time` when no profile or providers are supplied. That default is the CPU sampling trace we want here.

```powershell
dotnet-trace collect `
  --output TestResults\interface-db-filesystem-store.nettrace `
  -- dotnet .\StorageBenchmarks.Transactions\bin\Release\net10.0\StorageBenchmarks.Transactions.dll --profile filesystem-store
```

Collect the indexed write path:

```powershell
dotnet-trace collect `
  --output TestResults\interface-db-filesystem-indexed-store.nettrace `
  -- dotnet .\StorageBenchmarks.Transactions\bin\Release\net10.0\StorageBenchmarks.Transactions.dll --profile filesystem-indexed-store
```

Collect the selective indexed query path:

```powershell
dotnet-trace collect `
  --output TestResults\interface-db-selective-indexed-query.nettrace `
  -- dotnet .\StorageBenchmarks.Transactions\bin\Release\net10.0\StorageBenchmarks.Transactions.dll --profile filesystem-selective-indexed-query
```

The normal query targets include setup, which writes and indexes 50k objects before running the query batch. For query-specific profiling, use the hot target so setup is amortized and the query path dominates the trace:

```powershell
dotnet-trace collect `
  --output TestResults\interface-db-selective-indexed-query-hot.nettrace `
  -- dotnet .\StorageBenchmarks.Transactions\bin\Release\net10.0\StorageBenchmarks.Transactions.dll --profile filesystem-selective-indexed-query-hot
```

## Allocation Trace

Use the GC verbose profile when the question is allocation pressure:

```powershell
dotnet-trace collect `
  --profile gc-verbose `
  --output TestResults\interface-db-filesystem-indexed-store-gc.nettrace `
  -- dotnet .\StorageBenchmarks.Transactions\bin\Release\net10.0\StorageBenchmarks.Transactions.dll --profile filesystem-indexed-store
```

Convert a trace to Speedscope flame graph format:

```powershell
dotnet-trace convert .\TestResults\interface-db-filesystem-store.nettrace --format Speedscope
```

## What To Inspect

For write traces, inspect whether time is concentrated in:

- `FileObjectWriter<TContract>` and file stream operations;
- `CoalescingFileWriteCoordinator<TContract>`;
- `BinaryObjectSerializer<TContract>` or MessagePack internals;
- `EncryptingObjectSerializer<TContract>`;
- `EnvelopeLifecycle<TContract>`;
- `SearchIndexManager<TContract>` / `SearchIndexStore<TContract>`;
- notification dispatch or file watcher reconciliation;
- `Directory.CreateDirectory`, `File.Move`, `File.Replace`, or metadata-heavy filesystem calls.

For indexed query traces, inspect:

- `SearchIndexStore<TContract>.TryEvaluate`;
- expression compilation and fallback predicate evaluation;
- envelope materialization through `GetPhysicalAsync`;
- object/envelope cache lookup cost;
- allocations caused by `HashSet<string>` candidate sets.

## Expected Next Decisions

The trace should tell us which direction is worth pursuing:

| Finding | Likely Action |
|-------- |-------------- |
| File IO dominates | Defer large gains to a future storage engine provider. Keep file provider simple. |
| Serialization dominates | Keep `IObjectSerializer<TContract>` stable and consider optional serializer providers later. |
| Index update allocation dominates | Optimize `SearchIndexStore<TContract>` value tracking and candidate set handling. |
| Expression planning dominates | Profile and tune the cost-aware query-plan layer, expression-shape compilation cache, and candidate materialization. |
| Notifications/watchers dominate | Batch or suppress internal notification paths during bulk save where safe. |

## Current Architecture Boundary

Performance work should preserve the public repository surface:

```text
IRepository<TContract>
  -> IIndexManager<TContract>
    -> IEnvelopeRepository<TContract>
      -> IReaderAndWriter<TContract>
        -> IObjectSerializer<TContract>
        -> IFileWriteCoordinator / IStorageWriteLock
```

This is the reason native or alternative engines remain possible later. The high-level API can stay C# and interface-first while lower layers are swapped for:

- a different serializer;
- a persistent index engine;
- a SQL/document/object-store envelope provider;
- a memory-mapped or native storage provider.

The current recommendation is to profile first, then optimize the layer that actually shows up in the trace.

## Initial Trace Observations

The first clean direct-DLL traces were collected for:

- `filesystem-store`
- `filesystem-indexed-store`
- `filesystem-selective-indexed-query`
- `filesystem-selective-indexed-query-hot`

Both traces show the same broad shape: write time is dominated by file-system and async IO coordination rather than MessagePack serialization.

Important sampled hotspots:

- `File.OpenHandle`
- `SafeFileHandle.CreateFile`
- `Interop.Kernel32.CreateFilePrivate`
- `RandomAccess.WriteAsync`
- IO completion polling
- handle release/close
- `File.Exists`
- thread-pool waits around the asynchronous write workload

In the non-indexed write trace, `MessagePackSerializer.Serialize` was only a small sampled contributor. The indexed write trace still showed file IO and async coordination as the dominant cost, which means the current search index update path is not the primary write bottleneck in this workload.

The first selective indexed query trace was dominated by setup rather than querying because the target creates and indexes 50k records before running the fast query batch. Use `filesystem-selective-indexed-query-hot` when the purpose is to inspect the query path itself.

The hot selective indexed query trace confirms that the query path is small compared with the file-write setup path. The visible query-specific costs are:

- `FileSystem_SearchIndexSelectiveQueryBatch`;
- expression compilation through `Expression.Compile`;
- index expression evaluation through `SearchIndexStore<TContract>.TryEvaluateNode`;
- candidate search/merge operations.

This suggests a possible future micro-optimization: cache or avoid compiled predicate validation for expressions that are fully satisfied by supported indexes. That should be handled carefully because the current validation step is a safety net against stale or incorrectly planned index results.

Current conclusion: do not spend the next optimization pass on serializer replacement or IL tricks. The likely large write gains require either reducing per-file filesystem overhead in the existing provider or designing a new storage-engine provider. The existing file provider should stay focused on simple, reliable local object storage.
