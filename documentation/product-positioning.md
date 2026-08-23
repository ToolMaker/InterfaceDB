# Product Positioning

InterfaceDB is an interface-first object repository with a file-backed storage provider. Its differentiator is the dependency boundary and object lifecycle—not a claim that every operation is faster than a mature embedded database.

## What It Is

InterfaceDB provides:

- `IRepository<TContract>` as the application-facing persistence boundary;
- versioned interface contracts;
- polymorphic concrete implementations;
- storage envelopes and identities;
- file persistence, serialization, caching, and change reconciliation;
- secondary indexes and expression planning;
- migrations and contract-baseline analysis;
- optional encryption and key rotation.

The intended dependency direction is from application services to contracts and repository abstractions. Infrastructure selects and configures the concrete provider at the composition root.

## What It Is Not

InterfaceDB is not:

- a relational ORM;
- a SQL query provider;
- a distributed database;
- a remote database server;
- a document collaboration engine;
- a persistent full-text or vector search engine;
- a transactional multi-record store.

An ORM maps objects to relational tables. InterfaceDB owns an object repository model directly and currently persists one envelope-backed object per canonical file.

## Comparison

| Concern | InterfaceDB | Serializer + files | SQLite/embedded SQL | ORM |
|---|---|---|---|---|
| Application API | Interface repository | Custom file calls | SQL/provider API | Object context/repository |
| Polymorphism behind one contract | Built in | Application-owned | Discriminator/mapping design | Mapping-dependent |
| Storage version/migration chain | Built in | Application-owned | Schema migration | Framework/tooling |
| Exact/range/prefix/suffix/contains indexes | In-memory, attribute-driven | Application-owned | Persistent B-tree/SQL indexes; text extensions vary | Database-backed |
| Per-object inspectable files | Yes | Usually | No | No |
| External file change reconciliation | Yes | Application-owned | Not object-file based | No |
| Multi-record transactions | No | Usually no | Yes | Yes |
| Joins/aggregation | No | No | Yes | Yes |
| Encryption | Optional envelope/field AES-GCM | Application-owned | Provider/platform-dependent | Database/provider-dependent |
| Server/connection string required | No | No | No for embedded SQLite | Depends on provider |

## Strongest Use Cases

- desktop and local-first applications;
- embedded application-owned state;
- offline tools;
- plugin/configuration/catalog data with several concrete types;
- projects enforcing dependency inversion around persistence;
- object stores that benefit from independent files and external file awareness;
- domains that need explicit contract baselines and runtime object migration.

## Choose Another Store When

- multiple records must commit atomically;
- joins, grouping, or relational constraints drive the workload;
- many remote users write concurrently;
- replication, failover, or server-side authorization is required;
- persistent full-text, geospatial, fuzzy, or vector search is central;
- startup must reopen millions of indexed records immediately;
- maximum independently durable write throughput matters more than per-object files.

## Performance Position

The file provider has two profiles:

- `Performance` writes canonical object files directly for low local latency.
- `Durable` uses proposal publication, cross-process locks, and restart recovery.

Current measurements show competitive hot cached range/broad-query behavior, but SQLite remains much faster for selective exact/suffix queries, reopen, and independently durable writes. The comparison gate materializes complete objects for every engine; scalar SQL selects are not used as a substitute.

See [Current Performance Results](performance-results.md) for numbers and [Production Benchmark And Stress Gate](production-benchmark-gate.md) for the contract.

## Product Message

The strongest concise description is:

> InterfaceDB keeps application code on versioned interfaces and repository abstractions while a local object database handles concrete types, files, indexes, migrations, caching, and encryption below that boundary.

Avoid unsupported superlatives such as “the first” or “the fastest.” The public value proposition is specific, testable, and useful without them.
