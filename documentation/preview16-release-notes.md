# InterfaceDB 1.0.0-preview.16

Published to NuGet.org on 4 November 2026; see the [validation and publication record](preview16-validation.md).
Update `InterfaceDB`, `InterfaceDB.Storage.SegmentCore`, `InterfaceDB.Authentication`
and `InterfaceDB.Transport` together and rebuild dependent applications and hosts.

## Correctness and infrastructure

- File queries, physical lookups and batched reads tolerate a payload deleted after
  catalog lookup but before its file is opened. Materialized results remain stable
  committed copies. Missing directories/dependencies and other I/O errors still fail.
- File writer and migration leases validate exclusive-handle enforcement before
  relying on it. Supported abandoned leases recover promptly; age alone is not proof
  that a live owner has exited. Disabled Unix .NET locking or unsupported filesystem
  exclusion rejects writes/migration instead of risking concurrent access.
- `IEnvelopeImportRepository<T>` adds explicit committed imports without application
  notification echoes. Suppression is per operation; index maintenance, revision
  checks, durable commits and notification readiness/baseline invalidation remain active.
  Ordinary saves and existing metadata/synchronization imports retain their events.
- `ICanonicalObjectSerializer<T>` provides deterministic logical integrity bytes across
  application builds. The supplied binary serializer omits assembly versions in this
  auxiliary representation, retaining runtime type identity and all logical data.
  Normal storage and TCP serialization are unchanged.
- `ICommittedEnvelopeInventory<T>` exposes fresh committed File identities independently
  of cache state. Callers must hold provider exclusion while collecting snapshots.
- `RepositoryCompatibilityAlias` and `IRepositoryDataAvailability` let composers route
  only their own provider services and gate authoritative export/synchronization while
  an inventory is incomplete. Independent databases and user DI replacements are preserved.

These are public provider capabilities. Commercial migration, synchronization and
Atlas implementations remain separately delivered; no private implementation is packaged.

## Upgrade and supported boundaries

There is no new envelope format, TCP protocol, durability default or cache-isolation
change. Existing preview.15 stores do not need a data-format conversion for this release.
Applications upgrading from earlier previews must still follow the
[preview.15 field-encryption and remote-revision rollout](preview15-release-notes.md).

Custom composers must opt into and uphold the new capabilities they use. An incomplete
inventory cannot prove deletion. `IFileLeaseValidation` can be supplied through DI only
when it establishes equivalent exclusion. Read-only File access does not run the writer
probe. A successful check is cached per coordinator; failed checks are retryable.

On Unix, do not disable .NET file locking. The runtime/filesystem self-test is necessary
but does not qualify cross-host network filesystems, mobile devices or unaware older
writers that ignore the protocol. Keep incompatible writers stopped during cutover.

Physical-device/power-loss qualification and representative long-running application
soak remain outstanding. This preview does not claim a stable production qualification
or a new performance improvement. See [serializer capabilities](serializer-configuration.md),
[notifications](repository-notifications.md) and [storage guarantees](segment-core-storage-provider.md).
