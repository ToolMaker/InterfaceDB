# InterfaceDB Documentation

Start with these documents:

1. [Why InterfaceDB](why-interfacedb.md) explains the architectural purpose and when the model is a good fit.
2. [Getting Started](getting-started.md) builds the smallest working repository.
3. [Storing Multiple Concrete Types](polymorphic-storage.md) shows how one interface repository stores and restores different runtime classes.
4. [Feature Guide](features.md) describes the implemented product surface.
5. [Architecture](architecture.md) explains the repository, index, envelope, and storage layers.

Use the focused guides when needed:

- [Online TCP repository](tcp-repository.md): direct remote access and disposable pub/sub, without a local client database; added in preview.11.
- [TCP hosting](tcp-transport.md): TLS listener, persistent server storage, Docker and operating limits.
- [JWT authentication](jwt-authentication.md): supplied user tokens, service credentials and Keycloak configuration.
- [Preview.11 Release Notes](preview11-release-notes.md): public TCP/authentication packages, serializer reuse and upgrade guidance.

- [Public package overview](../Idb.Libraries/README.md): current preview.11 behavior, examples, defaults and upgrade requirements.
- [Preview.10 Release Notes](preview10-release-notes.md): subscription readiness, running versions, migration corrections, updated dependencies and combined upgrade guidance.
- [Preview.9 Release Notes](preview9-release-notes.md): context-bound encryption, runtime-type enrollment, input limits, encrypted-index isolation and hardened SegmentCore restore.
- [Preview.8 Release Notes](preview8-release-notes.md): expanded NuGet documentation; unchanged preview.7 runtime.
- [Preview.7 Release Notes](preview7-release-notes.md): IDB6 compatibility, revisions, retained editing, maintenance and qualification.
- [Retained editing](retained-editing.md): explicit editing scopes, history limits, compaction protection and free strategy extension points.
- [Optional compaction maintenance controls](compaction-maintenance.md): scheduling policies, candidate selection, shared I/O admission and separate maintenance summaries; current integration limits are explicit.
- [Envelope revisions](envelope-revisions.md): conditional writes, compatibility, metadata import, and storage ownership.
- [Security And Encryption](security.md): threat boundaries, AES-GCM, encrypted fields, key storage, and rotation.
- [Serializer registration](serializer-configuration.md): keyed envelope serializers, revision support and authenticated encryption wrappers; preview.11 support.
- [Configuration](configuration.md): repository options, storage policies, coordination, and multiple registrations.
- [Repository Notifications](repository-notifications.md): free Created/Changed/Deleted subscriptions, property changes, filters, loop prevention, lifetimes and limits.
- [Repository Notification Costs](repository-notifications-performance-20260905.md): disabled and subscribed measurements for File and Segment Core.
- [Notification Parallel-Write Follow-up](repository-notifications-parallel-performance-20260905.md): zero-subscriber fast path, background comparison and updated cost measurements.
- [Contract Versioning And Migrations](migrations.md): baselines, migration code, runtime strategies, explicit SegmentCore migration, and deployment checks.
- [Storage, Indexing, And Performance](storage-indexing-performance.md): file layout, caching, indexes, and tuning.
- [Query Planning](query-planning.md): expression routing, cardinality estimates, intersections, unions, and residual predicates.
- [Search Index Strategies](search-index-strategies.md): exact, range, prefix, suffix, and contains structures.
- [Current Performance Results](performance-results.md): reproducible measurements and honest interpretation.
- [Preview.6 Performance Snapshot](performance-preview6-20260905.md): latest complete-object SQLite comparison, repository timings, allocations and regressions.
- [Preview.6 Release Notes](preview6-release-notes.md): changes, IDB5 compatibility and release qualification.
- [SegmentCore Storage Provider](segment-core-storage-provider.md): installation, registration, atomic writes, paged locators, migration, online backup/restore, compatibility policy, qualification, performance, and supported boundaries.
- [Object-Native Storage Engine Research And Plan](object-native-storage-engine-plan.md): source-backed architecture decision, recovery design, benchmarks, and go/no-go gates for a journal provider.
- [Object-Native Storage Phase 0 Results](object-log-phase-zero-results.md): implemented admission probe, fault/recovery evidence, measured Windows/Linux results, and the no-go decision.
- [Object-Native Storage NVMe Prototype Results](object-log-nvme-prototype-results.md): hash-striped/write-through and group-commit follow-up, native Windows and virtualized Linux metrics, crash evidence, and the conditional go decision.
- [SegmentCore Prototype Results](segment-core-prototype-results.md): bounded striped writes, streaming primary-locator checkpoints, direct materialized reads, recovery evidence, Dapper confirmation, scale results, and the production-provider admission decision.
- [SegmentCore Adversarial Strength And Weakness Results](segment-core-adversarial-results.md): scan/query losses, reopen and full-recovery costs, update/delete space amplification, checkpoint stalls, unsupported capabilities, and the resulting engineering priorities.
- [SegmentCore Transactions And Performance](segment-core-transactions.md): explicit/implicit transaction API, participant and mutation-WAL protocols, recovery and failure semantics, and fair SQLite results.
- [Production Benchmark And Stress Gate](production-benchmark-gate.md): complete-object query, cache-profile, provider-open, and storage-core methodology and commands.
- [Performance Profiling](performance-profiling.md): collecting CPU and allocation evidence.
- [Product Positioning](product-positioning.md): comparison with ORMs, serializers, embedded databases, and repositories.
- [Roadmap](roadmaps.md): remaining product work and provider research.
- [Preparing a NuGet preview](releasing.md): versioning, documentation, package boundaries, validation and publication.

The executable reference application is `InterfaceDb.ConsoleApp`. The broad storage behavior suite is `InterfaceDb.IntegrationTests`.
