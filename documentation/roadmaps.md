# InterfaceDB Roadmap

This document separates known roadmap work from smaller correctness or repository-hygiene gaps.

## Roadmap Items

### Enterprise Envelope Storage Backends

The current file-system implementation is a simple local object database baseline. Future enterprise storage should move the envelope persistence boundary behind provider implementations such as:

- SQL-backed envelope storage;
- document database envelope storage;
- object database storage;
- distributed cache or queue-backed write coordination where appropriate.

The application-facing contract should remain `IRepository<TContract>` and envelope-aware storage contracts. Storage backends should plug in below that boundary.

### Secondary Indexing

Full secondary indexing is planned future work. The current `IIndexManager<TContract>` implementation is a pass-through manager that scans envelopes and delegates persistence to the envelope repository.

Planned indexing work should include:

- A clearer index contract for registering indexed fields or expressions.
- Index update behavior for save, delete, and migration flows.
- Rebuild behavior on repository initialization.
- Query planner behavior for deciding when an index can satisfy a predicate.
- Persistence or deterministic rebuild strategy for large stores.
- Tests covering indexed and non-indexed query paths.

## Near-Term Gaps

### Identity-Based Existence Checks

`ExistsAsync` currently depends on the envelope cache key carried by the saved instance. A new object with the same logical identity may not be recognized unless it has the same envelope metadata.

Potential fix:

- Define explicit storage identity, either through an interface such as `IIdentifiableContract<TKey>` or an attribute such as `[StorageIdentity]`.
- Use that identity for stable existence checks.
- Add tests for saved-instance checks and new-instance same-identity checks.

### Stable Serialized Type Names

Serialized payloads currently include the concrete .NET runtime type name. This is fragile if classes move, namespaces change, or assemblies are renamed.

Potential fix:

- Add a type alias registry per contract/version.
- Persist stable aliases instead of assembly-qualified type names for new records.
- Keep backward-compatible reading for existing payloads that contain assembly-qualified names.

### Runtime Migration Hardening

Migration scaffolding exists, but runtime migration behavior needs stronger validation before it should be treated as production-ready.

Potential fix:

- Make migration registration explicit and discoverable.
- Validate complete adjacent-version migration chains at startup.
- Ensure migrations read and write envelope-aware payloads correctly.
- Add tests using real legacy payload files.

## Completed Cleanup

### Package Vulnerabilities

NuGet packages were updated and the `MessagePack` vulnerability warning was removed.

### Build Warnings

The solution was retargeted to .NET 10 and compiler warnings were cleaned up. A forced rebuild currently completes with zero warnings and zero errors.

### Demo Storage Tracking

The broad `IdbDemoStorage/*` ignore rule was removed so required demo storage source files can be tracked while `bin` and `obj` outputs remain ignored.
