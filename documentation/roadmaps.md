# InterfaceDB Roadmap

## Current Baseline

The file-backed provider currently includes:

- interface-first repositories and polymorphic implementation recovery;
- single/multiple cardinality and custom identity factories;
- metadata, weak, and strong cache modes;
- exact, range, ordinal prefix/suffix, and ordinal contains indexes;
- cost-aware query planning and plan explanations;
- cross-process write/delete coordination and restart recovery;
- file watcher reconciliation and in-process notifications;
- eager/lazy migrations and contract baseline analysis;
- AES-GCM envelope/field encryption and key rotation;
- fair SQLite/Dapper materialization, stress, and crash-recovery gates.

## Priority 0: Public Release Readiness

Before calling a release stable:

1. Select the public license and package identity.
2. Establish semantic-versioning and compatibility policies for contracts, serialized envelopes, and public APIs.
3. Run the documented 10k/100k production gate on more than one Windows machine.
4. Add Linux/macOS build and non-Windows storage coverage; keep Windows Credential Manager tests Windows-only.
5. Run long mixed-workload, forced-termination, and disk-capacity soaks.
6. Add backup/restore and store-integrity verification guidance or tooling.
7. Validate packaged consumer installation from a clean sample project.

The repository already contains Windows CI, Dependabot configuration, contribution guidance, security reporting guidance, and a complete documentation index.

## Priority 1: Append-Only Storage Provider

The current file-per-object Durable path has been reduced from 15.299 ms to 5.626 ms median in the local quick gate without weakening tested recovery behavior. The remaining difference from SQLite is dominated by creating, locking, and publishing an independently recoverable object file.

The next provider experiment should sit behind `IEnvelopeRepository<TContract>` and preserve the public repository/index layers:

1. Append checksummed mutation records to a reused journal handle.
2. Mark each object mutation committed and issue one persistence barrier.
3. Rebuild the envelope catalog by replaying committed records.
4. Ignore or truncate incomplete tails.
5. Add tombstones, checkpoints, compaction, and integrity verification.
6. Preserve object-at-a-time acknowledgement; do not depend on an unfair multi-object transaction comparison.
7. Benchmark disk-to-materialized-object behavior against the file provider and SQLite.

This provider complements rather than replaces the per-object file provider. The current provider remains valuable when independent, inspectable files are the requirement.

## Priority 2: Reopen And Query Fixed Costs

- Persist or checkpoint secondary index/catalog metadata for faster reopen.
- Cache normalized query plans and safely reusable compiled expression shapes.
- Specialize exact and tiny candidate sets to reduce allocation.
- Skip residual evaluation only when semantics are proven fully covered.
- Add distribution/correlation statistics beyond live cardinality.
- Expose a supported custom index-provider contract.

## Priority 3: Search Families

Literal ordinal contains is implemented. Separate structures are still required for:

- ordinal-ignore-case and culture-aware search;
- tokenized/full-text search and ranking;
- fuzzy/edit-distance search;
- semantic vector similarity.

A vector index is not a replacement for literal substring search. Each family needs explicit query semantics, mutation rules, memory limits, persistence/rebuild behavior, and benchmarks.

## Priority 4: Security And Operations

- Resolve named encryption profiles to provider-specific key policies.
- Add cross-platform keychain and external vault providers.
- Add structured operational diagnostics without leaking secrets.
- Add integrity scanning, repair reporting, and backup consistency points.
- Define key-loss, rotation-failure, and partial-store recovery runbooks.

## Deferred Research

- Git-backed data history should remain an explicit maintenance mode, not a write-path dependency or a substitute for backup.
- Native code should be considered only after a managed trace identifies a stable hot path that justifies interop complexity.
- Remote providers, replication, and distributed transactions would require separate product contracts and are not implied by the local repository API.
