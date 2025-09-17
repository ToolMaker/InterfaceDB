# Changes in v1.5
- Writers now stream directly into the target file with exclusive access instead of using temp files; this is documented along with the need for future cross-process coordination.
- Spec captures the planned hash/migration workflow and notes strategies for handling version mismatches between concurrently running applications.

# Interface Storage Specification (v1.5 Draft)

## 1. Scope Discovery & Topology
- [StorageScope] decorates interfaces intended for persistence and may declare a VersionTag string (default "1.0").
- ScopeTreeBuilder scans configured assemblies, records version tags and implementation lists, caches metadata per assembly set, and provisions <Root>/<Interface> directories.
- ScopeDescriptor stores: ContractType, Directory, Implementations, and VersionTag.

## 2. Versioned Folder & Envelope Format
- Data lives under <Interface>/<VersionTag>/<Id><ext>.
- ObjectEnvelope.VersionTag mirrors the tag that authored the file; serializer implementations remain responsible for byte-level format.
- Changing the tag in code and redeploying writes to a new folder; existing folders remain untouched until migrations run.

## 3. Serialization Pipeline
- IObjectSerializer is runtime-aware via Serialize(object, Type) / Deserialize(byte[], Type).
- Built-in JSON/MessagePack/Encrypted implementations continue unchanged.

## 4. Persistence Services
- IObjectWriter.WriteAsync<TContract>:
  - Validates contracts, writes directly to <Interface>/<VersionTag>/<Id><ext> using a single FileStream opened with exclusive write access.
  - The stream is created with FileShare.None so other writers/readers block until the write completes.
  - No temp files are produced; data durability relies on the underlying filesystem’s semantics.
- IObjectReader.ReadAsync<TContract>:
  - When no tag is supplied, checks the default tag folder first, then falls back to the highest available tag (lexicographic order).
  - Respects caller-specified tags; will throw if the folder or file is missing.
- ExistsAsync<TContract> mirrors the lookup logic and still uses the exclusive access hint to detect incomplete writes gracefully.

## 5. Repository Facade
- SaveAsync<TContract> runs tag-based writes; read/exist keep optional tag parameters.

## 6. Dependency Injection
- AddScopeStorage produces cached descriptors; writer/reader/repository rely exclusively on descriptor metadata for folder resolution.

## 7. Operational Notes
- Concurrent writers must coordinate via shared configuration; the default exclusive file handle blocks competing writes but does not eliminate last-writer-wins if the same id is reused.
- Version-tag mismatches across running binaries should be detected in the forthcoming migration phase via stored hashes; until then, older binaries encountering unknown tags can treat them as read-only or prompt for migration.
- Avoid editing version tags at runtime; change code and redeploy.

# Migration Strategy (Future Work)
- Record a hash per <Interface>/<VersionTag> representing contract + serializer shape.
- On startup, compare stored hashes with the current code.
  - If mismatch and a migration script exists under Migrations/<Interface>/<FromTag>_to_<ToTag>, run it.
  - If mismatch and no script exists, fail fast.
  - If hash missing, treat as first run and record the hash.
- Old binaries detecting unknown tags should fall back to read-only mode or instruct the operator to migrate.

# Roadmap
- **Implementation Registry Options**: Explicit registration of permitted concrete types.
- **Schema Migration Plan**: Implement hash storage, migration scripts, and safe upgrade flow.
- **Serializer Overrides per Interface**: Revisit after migration tooling lands.
- **Deduplicated Multi-Interface Persistence**: Investigate linking to avoid redundant payloads.
- **Tooling**: Diagnostics for missing implementations, tag mismatches, or concurrency hazards.
- **Version Tag Conventions**: Enforce folder-safe tagging and provide friendly errors.
