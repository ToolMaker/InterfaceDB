# Changes in v1.4
- Schema versions are now referred to as code-defined **version tags** declared on storage interfaces via [StorageScope(VersionTag = "...")].
- Version tags are treated as opaque strings (suitable for folder names) and cannot be overridden at runtime.
- Writer and reader services operate solely on the declared tag; numeric parsing logic was removed.
- Documented the forthcoming hash-based migration approach while leaving implementation for later.

# Interface Storage Specification (v1.4 Draft)

## 1. Scope Discovery & Topology
- [StorageScope] decorates interfaces intended for persistence and may declare a VersionTag string (defaults to "1.0").
- ScopeTreeBuilder scans configured assemblies, records each interface’s version tag and implementation list, and provisions <Root>/<Interface> directories.
- ScopeDescriptor stores:
  - ContractType
  - Directory
  - Implementations
  - VersionTag (string)

## 2. Versioned Folder & Envelope Format
- Data lives under <Interface>/<VersionTag>/<Id><ext>.
- ObjectEnvelope.VersionTag mirrors the tag that authored the file.
- Changing the version tag in code and redeploying produces a new folder; existing folders remain until migrations run.

## 3. Serialization Pipeline
- IObjectSerializer remains runtime-aware via Serialize(object, Type) / Deserialize(byte[], Type); built-in JSON/MessagePack/Encrypted implementations continue unchanged.

## 4. Persistence Services
- IObjectWriter.WriteAsync<TContract>:
  - Validates that TContract is a registered storage interface and the instance implements it.
  - Writes into <VersionTag> derived from the descriptor; callers cannot supply a different tag.
  - Persists the runtime type inside the envelope for round-tripping.
- IObjectReader.ReadAsync<TContract>:
  - If the caller omits a tag, checks the default tag folder first, then falls back to the highest available tag (lexicographically) when necessary.
  - Honors explicit tag requests by reading the corresponding folder.
- ExistsAsync<TContract> reuses the same lookup logic.

## 5. Repository Facade
- SaveAsync<TContract> excludes version arguments; read/exists methods optionally accept a tag for historical access.

## 6. Dependency Injection
- AddScopeStorage produces descriptors capturing tag metadata. Writer/reader/repository rely solely on those descriptors for folder resolution.

## 7. Operational Notes
- Version tags must be folder-safe strings (no path separators or invalid characters).
- To publish a new version: change the interface’s VersionTag, redeploy, and persist data again; do not change tags at runtime.
- Future migration tooling will reconcile differences between stored hashes and current code-tag pairs.

# Migration Strategy (Future Work)
- Store a hash alongside each version tag to detect contract changes.
- If a mismatch is detected on startup, locate a migration script under Migrations/<Interface>/<FromTag>_to_<ToTag> and execute it; otherwise fail fast.
- Missing hashes indicate first run for a tag; the system records the hash without migration.

# Roadmap
- **Implementation Registry Options**: Allow explicit registration of permitted concrete types.
- **Schema Migration Plan**: Flesh out hash storage, migration scripts, and transactional upgrades.
- **Serializer Overrides per Interface**: Revisit once migration/core workflow is solidified.
- **Deduplicated Multi-Interface Persistence**: Investigate linking to avoid redundant payloads.
- **Tooling**: Provide diagnostics for missing implementations, tag mismatches, or ambiguous mappings.
- **Version Tag Conventions**: Offer validation helpers and friendly errors for invalid folder names.
