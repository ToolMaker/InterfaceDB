# InterfaceDB Documentation

Start with these documents:

1. [Why InterfaceDB](why-interfacedb.md) explains the architectural purpose and when the model is a good fit.
2. [Getting Started](getting-started.md) builds the smallest working repository.
3. [Feature Guide](features.md) describes the implemented product surface.
4. [Architecture](architecture.md) explains the repository, index, envelope, and storage layers.

Use the focused guides when needed:

- [Security And Encryption](security.md): threat boundaries, AES-GCM, encrypted fields, key storage, and rotation.
- [Configuration](configuration.md): repository options, storage policies, coordination, and multiple registrations.
- [Contract Versioning And Migrations](migrations.md): baselines, migration code, runtime strategies, and deployment checks.
- [Storage, Indexing, And Performance](storage-indexing-performance.md): file layout, caching, indexes, and tuning.
- [Query Planning](query-planning.md): expression routing, cardinality estimates, intersections, unions, and residual predicates.
- [Search Index Strategies](search-index-strategies.md): exact, range, prefix, suffix, and contains structures.
- [Current Performance Results](performance-results.md): reproducible measurements and honest interpretation.
- [Production Benchmark And Stress Gate](production-benchmark-gate.md): disk-to-materialized-object methodology and commands.
- [Performance Profiling](performance-profiling.md): collecting CPU and allocation evidence.
- [Product Positioning](product-positioning.md): comparison with ORMs, serializers, embedded databases, and repositories.
- [Roadmap](roadmaps.md): remaining product work and provider research.

The executable reference application is `InterfaceDb.ConsoleApp`. The broad storage behavior suite is `InterfaceDb.IntegrationTests`.
