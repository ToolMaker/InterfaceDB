# Preview.17: all implemented modules under MIT

Status: **release candidate; publication verification pending**. All nine packages use
`1.0.0-preview.17`. See the [validation record](preview17-validation.md) for actual results.

## Packages

| Package | Use it for | InterfaceDB dependencies |
| --- | --- | --- |
| `InterfaceDB` | Contracts, File storage, serializers, indexes and repository lifecycle | None |
| `InterfaceDB.Storage.SegmentCore` | Append-only SegmentCore storage | Core |
| `InterfaceDB.Authentication` | JWT/OIDC and native-account authentication | None |
| `InterfaceDB.Transport` | Basic online TLS TCP repository, host and pub/sub | Core, SegmentCore, Authentication |
| `InterfaceDB.Storage.Atlas` | Prefix-partitioned Atlas hybrid storage | Core, SegmentCore |
| `InterfaceDB.Repositories` | Local/remote composition, peer replication and provider migration | Core |
| `InterfaceDB.ConflictResolution` | Optional three-way merge for retained edits | Core |
| `InterfaceDB.Transport.Repositories` | Managed local/remote TCP adapters and peer relay | Repositories, Transport |
| `InterfaceDB.Transport.Atlas` | Atlas adapter for managed setups | Storage.Atlas, Transport.Repositories |

Install the modules the application uses; transitive dependencies restore normally.
There is no requirement to install all nine. Every implemented module is MIT licensed;
package boundaries express dependencies, not paid editions. Storage migration remains
in `InterfaceDB.Repositories` so its runtime types do not move to another assembly.
Embedded applications still need no TCP or authentication package.

```sh
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.17
# Optional provider migration and repository composition:
dotnet add package InterfaceDB.Repositories --version 1.0.0-preview.17
# Optional managed TCP composition:
dotnet add package InterfaceDB.Transport.Repositories --version 1.0.0-preview.17
```

## Licensing and support

The standard [MIT license](../LICENSE) applies to the formerly commercial implementations
now included here as well as the existing public code. Every runtime package includes it.
Third-party dependencies retain their own licenses.

If InterfaceDB helps you, please consider [sponsoring maintenance](../SPONSORING.md).
That request is voluntary and separate from the license. There are no paid feature gates,
activation requirements or added restrictions. GitHub Sponsors enrollment is not yet
verified; the support page will carry a payment link once it is available.

## Upgrade and compatibility

1. Update all InterfaceDB package references used by an application to the same preview.
   Coordinate TCP client and host upgrades as documented for earlier previews.
2. Remove corresponding old private project or direct DLL references when adopting the
   packages. Do not load both implementations into one process.
3. Preserve existing namespaces and configuration. Repository, merge and managed transport
   assemblies retain their historical `InterfaceDB.Commercial.*` identities; Atlas retains
   `InterfaceDB.Storage.AtlasCore`. These are compatibility names, not license restrictions.
4. No storage/encryption format transition, durability relaxation, pooling behavior or
   authorization bypass is introduced by this distribution change. Strict conflicts,
   committed-copy reads, server-owned revisions and scope checks remain.
5. Object serialization uses `IObjectSerializer<T>` through DI. Select the same registration
   and key for storage and TCP. Internal protocol framing is separate from object
   serialization. Sharing a serializer does not make native and hosted stores interchangeable.
6. Upgrading packages alone does not move File data to SegmentCore. Configure
   [storage migration](storage-migration.md), back up source/receipt/clock state and stop
   competing old writers. Physical ownership and rollback requirements still apply.

### Same-label storage migration correction

Explicit provider moves now support a nonnumeric label such as `v1_0_0` when running,
source and destination labels are identical, nonblank and have no version ceilings.
Labels are compared exactly, not normalized to numbers. Ordered/version-triggered
migrations still require numeric versions. Unsupported ordering fails before transfer.
Tests cover legacy IDB2 data, positive revisions, metadata, clock floors, retirement,
restart and writable partial recovery.

## Integration guides

- [Atlas storage](atlas-storage-provider.md)
- [Local-first and remote-first repositories](local-remote-repositories.md)
- [Server-managed setups and authorization](server-managed-setups.md)
- [Peer replication and history/resume choices](peer-replicated-repository.md)
- [Storage migration](storage-migration.md), [legacy repair](storage-migration-legacy-repair.md)
  and [retirement/cleanup](storage-migration-retirement.md)
- [Conflict resolution and retained editing](conflict-resolution.md)
- [Basic TCP](tcp-repository.md), [data policies](data-access-policies.md)

## Qualification and future work

This remains a preview. Opening source does not establish production qualification.
Physical power loss, representative application soak and intended Android/iOS hardware
remain qualification work. Optional live Keycloak tests require their external environment.

gRPC, adaptive Atlas tuning, TCP pooling and historical migration-stage reclamation
remain roadmap work. Peer replication still offers best-effort availability: records
cannot be recovered if every copy is lost. Server policy does not make offline device
caches tamper-proof. No new performance claim accompanies this packaging change.
