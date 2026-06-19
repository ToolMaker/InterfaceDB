# Why InterfaceDB Exists

InterfaceDB is an interface-first object database for applications that want local object persistence without giving up polymorphism, version-aware storage, or storage lifecycle awareness.

The current implementation is file-backed and intentionally simple. The goal is not to imitate SQL table storage. The goal is to store objects through their contract interface and recover the correct concrete object later.

## Core Advantages

### Interface-Level Polymorphism

Most common persistence tools need a known concrete type or a manually mapped table shape. InterfaceDB starts from the interface:

```csharp
IRepository<IPerson>
```

The stored object can still be a concrete subtype:

```csharp
Student
Employee
```

The envelope stores the metadata needed to recover the correct implementation behind the interface. That gives the application a stable contract while allowing multiple concrete storage types.

### Rich Envelope Metadata

Every stored object is wrapped in an envelope containing storage metadata such as:

- storage key;
- provider location;
- version tag;
- object state;
- concrete implementation metadata;
- object instance reference when retained in cache.

This is deliberate overhead. It is what lets InterfaceDB support interface contracts, migration, concrete type recovery, deletion state, and repeat saves without rediscovering the backing storage location.

### Encryption Extensibility

InterfaceDB has a storage encryption layer below the repository and above the physical write. The default encryptor is a no-op, but AES-GCM can be enabled without changing repository code or contract usage.

Because encryption is applied to the serialized envelope payload, the file-backed provider can protect both the object data and the envelope metadata stored inside the payload. SQL Server has encryption features at the database/platform level, and application code can encrypt values manually before using Dapper, but Dapper itself does not provide a repository-level object encryption pipeline. InterfaceDB owns that pipeline as part of the storage model.

### Envelope Cache As The Primary Catalog

InterfaceDB keeps an in-memory envelope catalog. This avoids normal query flows scanning the file system.

The file payload is durable storage. The envelope cache is the repository's known-object catalog. Secondary indexes can later sit beside this catalog, but the envelope cache is the minimum object index.

Write behavior now always caches the envelope metadata. Full object instance retention is selective: the default keeps lightweight envelope skeletons, `[StorageCached]` opts in a concrete storage type, and `IObjectEnvelope<TContract>.CacheObjectInstance` opts in a specific stored object.

### Storage Lifecycle Notifications

InterfaceDB has built-in repository notifications:

- `Stored` when an object is saved or changed;
- `Added` when a new object is persisted;
- `Deleted` when an object is deleted;
- `Removed` when an object is removed or renamed away by storage synchronization.

These notifications are in-process repository events. They allow application code to react to object lifecycle changes without polling.

Relational databases can provide change notifications in some environments, but those mechanisms are usually table/row oriented and provider-specific. Dapper itself does not provide object lifecycle notifications; it is a data mapper over ADO.NET.

### External Storage Change Awareness

The file-backed repository can watch the storage location for external committed changes:

- created payloads;
- changed payloads;
- deleted payloads;
- renamed payloads;
- watcher errors and recovery rescans.

This makes multiple repository instances pointed at the same storage root aware of each other. A local write suppresses its own watcher echo, while external changes can update or remove envelope cache entries.

In plain terms: one process can write or delete an object and another process can observe the file-system change and reconcile its cache.

### Provider-Neutral Direction

The envelope location is an opaque provider locator. The file provider treats it as a path, but future providers can map it to SQL keys, document IDs, object-store keys, event-stream offsets, or in-memory dictionary keys.

The exception model is also provider-neutral, so file, SQL, document, event, and memory providers can report comparable storage failures while keeping provider-specific details as inner exceptions.

## Current Performance Baseline

Latest full stack benchmark imported from `TestResults/results.txt` on 2026-06-19.

Read benchmarks use 100 queries over 50k persons and materialize matching objects for SQL Server, Dapper, and Entity Framework Core. InterfaceDB file storage is represented by `FileEnvelopeRepository<TContract>` with `FileObjectReader<TContract>` / `FileObjectWriter<TContract>` underneath. The index axis is explicit: `PassthroughIndexManager<TContract>` means no secondary index, while `SearchIndexManager<TContract>` means the opt-in in-memory search index layer is active.

| Operation | Storage implementation | Index / access layer | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|---------- |----------------------- |---------------------- |-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Broad predicate scan | InterfaceDB `FileEnvelopeRepository<TContract>` | `PassthroughIndexManager<TContract>` | 1,320.54 ms | 46.084 ms | 135.880 ms | - | - | - | 38.20 MB |
| Broad materialized query | SQL Server | ADO.NET with SQL indexes | 1,053.77 ms | 13.809 ms | 12.917 ms | 9000.0000 | 5000.0000 | 3000.0000 | 266.19 MB |
| Broad materialized query | SQL Server | Dapper with SQL indexes | 1,173.13 ms | 20.863 ms | 28.558 ms | 10000.0000 | 5000.0000 | 3000.0000 | 349.81 MB |
| Broad materialized query | SQL Server | Entity Framework Core with SQL indexes | 1,368.51 ms | 27.364 ms | 76.730 ms | 15000.0000 | 6000.0000 | 4000.0000 | 563.22 MB |
| Broad indexed search | InterfaceDB `FileEnvelopeRepository<TContract>` | `SearchIndexManager<TContract>` | 880.51 ms | 16.956 ms | 34.253 ms | 1000.0000 | - | - | 84.18 MB |
| Selective indexed search | InterfaceDB `FileEnvelopeRepository<TContract>` | `SearchIndexManager<TContract>` | 64.49 ms | 1.152 ms | 1.183 ms | 1000.0000 | 500.0000 | - | 8.41 MB |
| Selective materialized query | SQL Server | ADO.NET with SQL indexes | 161.59 ms | 0.541 ms | 0.479 ms | 1250.0000 | 250.0000 | - | 10.63 MB |
| Selective materialized query | SQL Server | Dapper with SQL indexes | 164.82 ms | 1.278 ms | 1.196 ms | 1666.6667 | 333.3333 | - | 13.84 MB |
| Selective materialized query | SQL Server | Entity Framework Core with SQL indexes | 184.85 ms | 2.499 ms | 2.337 ms | 3333.3333 | 1000.0000 | - | 29.10 MB |

Write benchmarks store 50k persons. SQL Server, Dapper, and Entity Framework Core use object-by-object insert loops in a transaction. `SqlBulkCopy` is included as an upper-bound bulk-ingest reference, not as the object-by-object comparison.

| Operation | Storage implementation | Index / access layer | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|---------- |----------------------- |---------------------- |-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Object transaction write | InterfaceDB `FileEnvelopeRepository<TContract>` | `PassthroughIndexManager<TContract>` | 5,240.45 ms | 103.265 ms | 141.351 ms | 5000.0000 | 1000.0000 | - | 230.43 MB |
| Object transaction write | InterfaceDB `FileEnvelopeRepository<TContract>` | `SearchIndexManager<TContract>` | 5,533.27 ms | 110.223 ms | 97.710 ms | 6000.0000 | 1000.0000 | - | 325.46 MB |
| Object transaction write | SQL Server | ADO.NET without extra SQL indexes | 3,796.24 ms | 75.517 ms | 165.762 ms | 16000.0000 | - | - | 159.84 MB |
| Object transaction write | SQL Server | ADO.NET with SQL indexes | 4,183.05 ms | 83.078 ms | 105.067 ms | 16000.0000 | - | - | 159.84 MB |
| Object transaction write | SQL Server | Dapper without extra SQL indexes | 3,980.96 ms | 77.546 ms | 116.067 ms | 47000.0000 | - | - | 404.74 MB |
| Object transaction write | SQL Server | Dapper with SQL indexes | 4,409.31 ms | 85.179 ms | 204.084 ms | 47000.0000 | - | - | 404.74 MB |
| Object transaction write | SQL Server | Entity Framework Core without extra SQL indexes | 1,682.89 ms | 33.551 ms | 47.034 ms | 6000.0000 | 2000.0000 | 1000.0000 | 355.90 MB |
| Object transaction write | SQL Server | Entity Framework Core with SQL indexes | 1,896.17 ms | 22.350 ms | 19.813 ms | 6000.0000 | 2000.0000 | 1000.0000 | 355.90 MB |
| Bulk ingest reference | SQL Server | `SqlBulkCopy` | 179.34 ms | 1.266 ms | 1.123 ms | - | - | - | 23.11 MB |

Current file benchmark settings:

```text
INTERFACEDB_BENCH_MAX_CONCURRENCY=16
INTERFACEDB_BENCH_BULK_BATCH_SIZE=16384
INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER=true
```

These results are encouraging because InterfaceDB is doing more than scalar row insertion:

- interface-based object persistence;
- concrete implementation recovery metadata;
- envelope creation and lifecycle;
- version-aware storage;
- file payload writes;
- envelope catalog updates.

Despite that richer storage model, write allocation is significantly lower than the current Dapper transaction benchmark.

The query results show why indexing matters. The non-indexed InterfaceDB predicate scan is slower than raw SQL Server and Dapper for broad reads, but the indexed InterfaceDB path beats the SQL Server, Dapper, and EF Core materialized-object benchmarks in both broad and selective indexed reads. The selective indexed case is the clearest win because the index can return a small candidate set before object filtering.

The remaining InterfaceDB performance gap is on writes. InterfaceDB indexed writes currently add about 293 ms over the non-indexed InterfaceDB write path for 50k objects in this run. That overhead is acceptable for maintaining the search index, but write allocation and serialization cost remain the next optimization targets.

Entity Framework Core gives a useful high-level ORM comparison. On broad materialized reads it is behind raw SQL Server and Dapper, but ahead of the non-indexed InterfaceDB scan. On selective indexed reads it is close to SQL Server and Dapper, but still slower and higher allocation. On writes it is currently the fastest object-by-object SQL path in this benchmark, significantly ahead of raw ADO.NET and Dapper, although it still allocates more than raw SQL Server and less than Dapper.

## Benchmark Interpretation

SQL Server raw ADO.NET is a useful lower-level storage baseline, but it is not a full apples-to-apples comparison. The current SQL benchmark reads properties from pre-created `IPerson` instances and writes scalar columns. It does not store the interface object model, envelope metadata, concrete implementation metadata, or migration metadata.

Dapper is the closer mainstream comparison because it maps objects to database rows. Even then, the current Dapper benchmark uses a flat reusable object and scalar columns. It does not reproduce InterfaceDB's interface-level polymorphic storage unless additional discriminator and subtype mapping logic is added.

Entity Framework is a valuable benchmark because it brings a richer object model and change tracking. The current read results show the cost of that richer ORM layer: EF Core is easier to use than raw ADO.NET, but it allocated substantially more memory and ran slower than SQL Server and Dapper for these materialized query shapes.

## Practical Positioning

InterfaceDB is strongest where the application wants:

- interface-first storage contracts;
- local desktop/mobile object persistence;
- polymorphic storage without hand-written table mapping;
- versioned objects and migrations;
- low memory overhead;
- application-level lifecycle notifications;
- external storage change awareness;
- future ability to swap file storage for other providers.

The current file-backed implementation is not yet a finished enterprise database. Indexing, true storage-level bulk writes, and additional providers are still roadmap work. But the current baseline is already close enough to common database write paths to justify further optimization.
