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

Completed release foundations are the MIT/package identity decision, semantic-versioning and storage/
envelope/API compatibility policies, Windows and Linux correctness gates, mixed/forced-termination/
disk-capacity qualification, backup/restore and integrity tooling, clean-package consumption, and
old-package-to-current-source upgrade validation. The repository also contains Windows and Linux CI,
Dependabot configuration, contribution guidance, security reporting guidance, and a complete
documentation index.

The next public preview is `1.0.0-preview.7`; its [release notes](preview7-release-notes.md) record qualification and compatibility. Virtualized Linux correctness remains
mandatory, while a 15% throughput lead over SQLite is an advisory optimization target rather than
a release blocker. The remaining evidence strengthens platform coverage and the future stable
durability claim:

1. Run the documented 10k/100k production gate on at least one additional Windows machine.
2. Add macOS build/storage coverage; Windows Credential Manager tests remain Windows-only.
3. Execute and retain an actual acknowledged-write physical power cut before stable `1.0.0`.

## Priority 1: SegmentCore Storage Provider

The source-backed design, failure invariants, phased implementation, and admission gates are specified in [Object-Native Storage Engine Research And Plan](object-native-storage-engine-plan.md).

Phase 0 is complete and produced a **no-go** for a custom isolated explicit-`FlushToDisk` engine. The measured checksummed append log was only 2.9% faster than SQLite direct `FULL` at p50 on Windows and 1.5% faster in the virtualized Linux run, below the required 15%; Linux p95 was also slower. See [Object-Native Storage Phase 0 Results](object-log-phase-zero-results.md).

The approved NVMe follow-up produced a **conditional go** for a minimal production-engine phase. Native Windows write-through stripes reached roughly 34,000 durable saves/s for 512-byte objects; virtualized Linux instead favored one explicit-flush group-commit log at roughly 3,600 saves/s under concurrency 32. Deterministic tests and representative forced-process-termination cycles passed. See [Object-Native Storage NVMe Prototype Results](object-log-nvme-prototype-results.md).

The checkpointed primary-index/direct-read follow-up is now named **SegmentCore** and produced a **go for an isolated production-provider implementation**. It retained concurrent append performance while adding materialized exact reads, updates, tombstones, checkpoints, checkpoint-tail recovery, and mixed workloads. The hardened 100,000-object run reached roughly 34,000 durable saves/s, 359,000 post-reopen materialized reads/s, and 147,000 mixed operations/s. Streaming reduced checkpoint allocation from 23.1 MB to about 102 KB and reopen allocation from 32.9 MB to 22.4 MB. See [SegmentCore Prototype Results](segment-core-prototype-results.md).

Store-local atomic multi-object transaction scoping is implemented with two immutable per-store protocols. Participant commit remains the correctness/control path. The mutation-bearing WAL prepares derived frames, uses concurrent group commit to durably acknowledge complete payloads and original locators, rebuilds missing frames during recovery, and leaves implicit one-object autocommit unchanged. Checkpoint-aware recycling rebases through the oldest retained recovery root and now uses reclaim/maximum-size thresholds to avoid replacing a small WAL after every checkpoint. The final explicit-flush run was 1.01x, 1.00x, 1.05x, and 1.21x faster than SQLite at batches 1, 10, 100, and 1,000 before maintenance. See [SegmentCore Transactions And Performance](segment-core-transactions.md).

The native-Windows write-through profile benefits from contiguous batched frame writes: mutation WAL was 8.67x, 5.48x, 4.13x, and 2.22x faster than SQLite before maintenance at batches 1, 10, 100, and 1,000. Independent autocommit was 14.54x faster. Named `Portable`, `LowMemory`, `HighThroughput`, `WindowsNvme`, and `Custom` hardware profiles resolve through `StoragePolicy` into complete SegmentCore options. Durability strategy remains immutable per store; a safe adaptive dual-path design remains research work.

The current file-per-object Durable path measured 5.574 ms median in the 2026-09-02 local quick gate, down from 15.299 ms without weakening tested recovery behavior. The remaining difference from SQLite is dominated by creating, locking, and publishing an independently recoverable object file. File discovery no longer schedules one thread-pool hop per object, and valid default file identities now avoid a redundant sanitized-key allocation.

The provider implementation is now packaged as `InterfaceDB.Storage.SegmentCore` and integrated behind `IEnvelopeRepository<TContract>`. It preserves the public repository/query layer, MessagePack envelopes, polymorphism, encryption hooks, notifications, and the existing exact/range/RadixForge prefix/suffix/contains index manager. Checksummed secondary-index catalogs now avoid payload hydration on a valid warm reopen and safely rebuild when stale, corrupt, or schema-incompatible. It remains opt-in and does not replace the file provider.

1. Append checksummed mutation records to a reused journal handle.
2. Select a qualified durability policy: parallel write-through stripes on supported Windows storage, grouped explicit flush where that wins, and a conservative fallback elsewhere.
3. Load the checkpointed primary locator catalog and replay only committed tail records.
4. Ignore or truncate incomplete tails.
5. Use bounded queues, explicit wait/reject overload policy, full/delta checkpoints, bounded retention, dirty-stripe flush accounting, background checkpoint publication, integrity verification, free-space admission, and bounded copy-forward compaction.
6. Preserve object-at-a-time acknowledgement; do not depend on an unfair multi-object transaction comparison.
7. Benchmark disk-to-materialized-object behavior against the file provider and SQLite.

This provider complements rather than replaces the per-object file provider. The current provider remains valuable when independent, inspectable files are the requirement.

### Completed SegmentCore Production-Candidate Work

The production pass completed:

1. A production-named, packable .NET 10 provider project and public `Idb.Storage.SegmentCore` API.
2. Full `IRepository<TContract>`/DI integration with atomic bulk saves, polymorphism, encryption hooks, cache policy, notifications, and the existing query planner/index manager.
3. Dirty-stripe flush accounting, no redundant write-through checkpoint flushes, checkpoint capture under a short barrier, and durable publication while new writes continue.
4. Parallel stripe reopen and validated checkpoint-tail recovery.
5. Automatic checkpoint/compaction maintenance with surfaced errors and mutation watermarks.
6. Interruption-safe copy-forward compaction with bounded record copies, bounded per-stripe preparation, two independently published recovery roots, free-space admission, and detailed pause telemetry. In the final two-update run it reclaimed 1,292.5 KiB; the longest observed exclusive slices were 15.6 ms under explicit flush and 9.9 ms under write-through.
7. Public integrity verification and store/provider health statistics.
8. End-to-end provider tests for bulk write, indexed query, delete, checkpoint, integrity, reopen, bounded compaction, hardware-profile resolution, precedence, validation, and every named profile's write/checkpoint/reopen path. The maintained suite includes lifetime-owner, symlink-alias, concurrent-initialization, and shutdown-admission regressions on both Windows and Linux.
9. Provider-neutral hardware profiles, explicit provider-option precedence, observable effective options, and public physical read/write coalescing controls.
10. True concurrent WAL group commit, allocation-reusing striped batch queues, no-op checkpoint generation reuse, lazy delta composition, and reclaim/maximum-size WAL recycle thresholds.
11. Checksummed, schema-bound, primary-locator-bound secondary-index catalogs for exact, range, ordinal prefix/suffix, and ordinal contains indexes. Warm indexed startup requires no payload deserialization; stale and corrupt catalogs rebuild from authoritative objects.
12. Bounded streaming enumeration/integrity verification, allocation-light small raw-payload point reads, passing write-through storage-core gates at 100,000 and 1,000,000 records, and a two-boot physical power-loss writer/validator harness.
13. A checksummed retained-WAL seek boundary that reduced the prior 51-53 ms small-transaction reopen cases to 9.77-14.60 ms without making the sidecar recovery authority.
14. Resident, paged, and automatic primary-locator modes. At one million raw records the latest paged mode reopened the raw store in 42.6 ms versus 927.2 ms resident and reduced reopen allocation by 82.0%, with documented checkpoint and raw-payload random-read trade-offs.
15. Provider-native, explicit, source-preserving, resumable contract migration with strict/tolerant modes, target checkpointing, and integrity verification.
16. Point-consistent online backup and non-overwriting restore with immutable-prefix capture, SHA-256 inventory, staged reopen/integrity validation, and atomic directory publication.
17. A checksummed storage-format/router manifest, explicit `IDB4` serialization identity, public compatibility policy, and a test-enforced exported-API baseline.
18. Automated long mixed-load and ENOSPC-equivalent durability-failure qualification plus passing .NET 10 Linux container execution.
19. A genuine raw-payload data-larger-than-memory execution with 35.65 GB live payload against a 34.26 GB managed-memory budget, periodic WAL recycling, full integrity verification, 932.8 ms raw-store reopen with a fully verified 755 MB WAL prefix, and 502.5 one-MiB payload reads/s. It does not instantiate repository objects.
20. Fresh-process complete-`GateRecord` and concurrent multi-process read characterization against SQLite on Windows, plus historical Linux raw-payload characterization. The current Windows harness uses the same codec on both sides, parent-verifies every materialized object/digest, and explicitly confirms the one-owning-process-per-live-store boundary. It remains a storage-core-plus-codec result rather than an `IRepository<T>` secondary-query result.
21. A .NET 10 JIT/CIL audit of measured hot paths. Hardware CRC32C already compiles to direct 64/32/8-bit `crc32` instructions with no calls in the hot loop; slower fused-CRC and custom-radix-sort experiments were reverted rather than shipped.
22. Direct primitive paged-checkpoint arrays, streamed map validation, parallel authoritative/derived validation, parallel map publication, allocation-light key validation, and cached read handles/paths. The final three-run corrected fresh-process object median measured 67,442 complete `GateRecord` objects/s versus SQLite at 38,806/s using the same payload codec and all-field validation. The earlier 64,624-versus-31,182 raw-payload result remains historical storage-core evidence only.
23. A deterministic regression for fresh-segment combined-batch rotation, found by Linux certification, plus a corrected large-payload validator that no longer allocates a failure message for every valid byte.
24. Listener-disabled-by-default `System.Diagnostics.Metrics` telemetry for open stages, maintenance latency/outcomes, and storage sizes, with deterministic instrument tests.
25. Cancellation-safe online backup gate acquisition and staging cleanup, including a regression that cancels while backup waits behind checkpoint publication and then proves the compaction gate remains usable.
26. Local-package upgrade qualification from a retained `1.0.0-preview.2` baseline through current-source mutation, checkpoint, integrity, backup, restore, and reopen.
27. A 10,000-object, five-cycle production pilot through `IRepository<T>` and every persisted index family, plus symmetric pre/post-reclamation measurements against SQLite VACUUM.
28. Startup stage profiling with listener-disabled-by-default headline measurements and opt-in stage diagnostics.
29. Safe overlap of independent checkpoint/WAL/object-log activation, a full-lifetime alias-safe root-owner lease, single-flight repository/index initialization, and shared concurrent disposal. The 100,000-record raw-open regression moved from one 103.5161 ms baseline observation to an 83.0657 ms three-run median while retaining full recovery validation.
30. A fresh-process repository-lifecycle gate that separates raw activation, missing-catalog rebuild, valid-warm catalog installation, first complete object materialization, and same-lifetime repeat initialization. Runtime-type caching and one-shot contains-index compaction reduced a matched 100,000-record warm initialization by 39.78% and allocation by 60.17%; the final five-run warm median was 2,172.41 ms with zero payload deserializations, while repeat initialization was 0.0087 ms p50 with zero allocation.

See [SegmentCore Storage Provider](segment-core-storage-provider.md) for the usable API and final maintained measurements.

### Stable 1.0 Release Gate

Execute and retain an actual physical power cut. Docker, abrupt process-kill windows, fault
injection, and the graceful two-boot rehearsal pass but are not substitutes for controller-level
evidence. This does not block `1.0.0-preview.5`; it blocks the strongest durability claim and stable
`1.0.0`.

### Deferred Product Decisions

1. Concurrent multi-process live readers remain a documented `1.0` boundary. Reconsider a separate read-only snapshot/lease design only when a concrete workload requires it; the current same-store probe fails by design while SQLite succeeds.
2. Extend lazy metadata beyond the raw paged primary locator only if repository startup memory remains material: the current InterfaceDB adapter still creates one envelope skeleton per physical key.
3. Keep complete retained-WAL validation. Do not adopt the O(1) checkpoint-boundary shortcut unless a future integrity-policy decision explicitly accepts the weaker recovery guarantee.
4. Consider an explicit detached-upsert API separately. Multiple-cardinality detached instances are inserts by design; tracked-object updates are enforced by the production pilot.
5. Treat the 15% virtualized-Linux lead as an advisory optimization target. Correctness remains mandatory; the maintained full run led SQLite by 5.4% p50 and the latest quick run by 11.1%.
6. Consider a compact catalog format with a runtime-type table and primitive binary values only as an explicitly versioned migration. The present format remains compatible; streaming catalog installation would mainly reduce peak memory and must preserve all-or-nothing footer validation.

### Lower-Priority External Characterization

Bare-metal Linux/NVMe certification and true post-reboot cold-media measurements are useful for
publishing hardware-profile-specific numbers, but they are no longer on the critical path for the
provider API. Run them when another physical machine/controller profile is available. They do not
replace the higher-value physical power-cut evidence, cross-machine Windows gate, or explicit
product-boundary decisions above.

### Future: Hardware Profile Detection Manager

Add an opt-in hardware detection manager that selects and registers a complete `StoragePolicy`
before the storage provider is constructed. The proposed `IStorageHardwareProfileDetector` should
inspect only stable, relevant capabilities such as operating system, processor count, available
memory, filesystem, volume/device type, and known flush/write-through support. It should resolve
those observations to `Portable`, `LowMemory`, `HighThroughput`, or a qualified platform/device
profile and then allow SegmentCore's existing profile resolver to produce the physical options.

Required design rules:

1. Fall back to `Portable` when detection is incomplete or ambiguous.
2. Never silently weaken the requested durability contract.
3. Let an explicitly registered `StoragePolicy` or `SegmentCoreRepositoryOptions` override detection.
4. Expose the detected evidence, selected profile, and effective options through diagnostics.
5. Cache the decision for a store and report hardware/profile drift on later startup.
6. Keep active disk microbenchmarks opt-in; normal startup detection must be bounded and non-destructive.
7. Require qualification evidence before mapping a device to `WindowsNvme` or any future optimized profile.

## Priority 2: Reopen And Query Fixed Costs

- Extend lazy metadata beyond the completed paged raw primary locator; the repository adapter still creates one envelope skeleton per physical key.
- Reduce valid-warm repository startup beyond the completed runtime-type cache and one-shot contains-index compaction. Profiled remaining work is dominated by installing in-memory index structures; do not merely defer it into the first query without an explicit readiness-semantics decision.
- Evaluate a persisted catalog for the file-per-object provider so indexed startup need not deserialize every object, but only after reviewing the interface-layer responsibility with the project owner.
- Retain full retained-WAL prefix verification unless the product explicitly approves the weaker O(1) checkpoint-boundary trust model.
- Extend the current per-expression compiled-delegate cache to normalized, safely reusable query-plan shapes.
- Batch/coalesce candidate hydration for medium and large indexed result sets; small point reads already use an allocation-light random-access path.
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
- Add application-level traces/log correlation around the completed low-cardinality SegmentCore metrics without leaking secrets.
- Add integrity scanning, repair reporting, and backup consistency points.
- Define key-loss, rotation-failure, and partial-store recovery runbooks.

## Deferred Research

- Git-backed data history should remain an explicit maintenance mode, not a write-path dependency or a substitute for backup.
- Native code should be considered only after a managed trace identifies a stable hot path that justifies interop complexity.
- Remote providers, replication, and distributed transactions would require separate product contracts and are not implied by the local repository API.
