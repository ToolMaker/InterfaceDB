# InterfaceDB 1.0.0-preview.7

This preview contains the MIT InterfaceDB package, including file storage, and the separate MIT InterfaceDB.Storage.SegmentCore package.

## Changes

- Revision-checked saves and deletes reject stale objects instead of silently replacing newer committed data. Revision allocation and payload publication share the provider's write guard.
- Optional Created/Changed/Deleted repository subscriptions provide filters, changed-property information and bounded lifetime/queue behavior. They are local observations, not a durable event log.
- Optional retained editing keeps selected original versions available through updates and compaction. Leases are bounded and process/session scoped; close or restart invalidates them.
- Compaction scheduling, admission budgets and maintenance summaries are separate from entity notifications. SegmentCore optionally reclaims independent files while preserving retained editing baselines.
- Complete-object scans reuse unchanged metadata safely, cache immutable type inspection and eliminate redundant serializer allocations. Results remain workload-dependent.

## Upgrade and rollback

New envelope writes use **IDB6**. Readers accept IDB2 through IDB6, but preview.6 and earlier cannot read new IDB6 writes. Stop old writers before opening a store with preview.7. Keep a complete verified pre-upgrade backup; returning to an older reader requires restoring that backup.

Rebuild applications and custom providers, envelopes, serializers and write coordinators. The required envelope revision property and revision-aware serializer/coordinator capabilities are source and binary compatibility changes. Existing-key metadata writes require an explicit condition; the overload without one is insert-only. Ordinary saves use exact revision equality by default.

The File provider requires sidecar write locks, including Performance mode. File backups must capture the entire quiescent root including the revision-clock sidecar. SegmentCore maintenance backup captures its clock consistently. Discard outstanding application objects and editing handles after restore.

Default SegmentCore stores remain engine format 1.0. Explicitly enabling independent-file compaction upgrades the durable engine manifest to **1.1**, even if subsequent work is cancelled or deferred. Older runtimes reject it. Returning to the default compaction option does not downgrade the store. Verify a backup before opting in.

## Production readiness and supported limits

The implementation includes recovery, integrity checks, backup/restore, migration, cleanup and conflict protection. Their presence does not establish every hardware/platform durability claim. Actual acknowledged-write physical power-cut evidence remains outstanding before stable 1.0.0; process kills and injected I/O failures test different failure modes. Apple-device runtime qualification also remains outstanding. Earlier Android emulator evidence is not a fresh qualification of every preview.7 feature.

SegmentCore supports one owning process per live store root and one-contract-store transactions. These packages do not provide distributed transactions, relational joins, authentication of remote users, durable notification replay or restart-persistent editing leases. Follow the documented ownership, backup and lifecycle rules.

## Release qualification

On 6 September 2026, a public-only source snapshot built Release with warnings treated as errors. Windows qualification passed 481 tests (299 core, 177 SegmentCore and five analyzer tests), with four platform-specific skips. All 50 integration scenarios, three object-materialization controls and 56 repository smoke cases passed. A clean-cache consumer using only the two packed preview.7 packages passed the same 56 smoke cases; the package inventory and MIT/public dependency boundary passed.

A raw SegmentCore store created with the published preview.6 package passed current-reader upgrade, checkpoint, backup and restore validation. The quick repository production pilot also passed mutation cycles, indexed reads, bounded compaction, online backup, reopen and verified restore. These are software-level checks, not physical power-cut qualification or an extended real-application soak.

Linux release CI must pass before package publication. A local package build alone does not establish NuGet availability. The tag-triggered release workflow is the publication authority.

See [envelope revisions](envelope-revisions.md), [retained editing](retained-editing.md), [maintenance controls](compaction-maintenance.md) and [repository notifications](repository-notifications.md).

## Install

```powershell
dotnet add package InterfaceDB --version 1.0.0-preview.7
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.7
```
