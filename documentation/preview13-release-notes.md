# InterfaceDB 1.0.0-preview.13

Status: published to NuGet.org on 28 October 2026. Windows/Linux CI, package inspection and
298 tests against the published packages passed; see [the validation record](preview13-validation.md).

The completed designer-review follow-up makes unrestricted
TCP sessions consistently address unpartitioned data, preserves trusted all-partition migration,
and distinguishes proven pre-commit rejections from uncertain mutation outcomes. Predictable
rejections and safe cancellation no longer discard other tenants' valid partition indexes.
The subsequent measured follow-up skips hidden tenant payloads before shared-page selection
and classifies a proven empty close during TLS setup as downtime, preserving explicit denial
and malformed-response handling. See [paging measurements](policy-paging-review-20261028.md)
and the validation record for the exact tested snapshot and remaining qualification limits.

All four public MIT packages move together: InterfaceDB, InterfaceDB.Storage.SegmentCore,
InterfaceDB.Authentication and InterfaceDB.Transport. The release contains public code only.

## What changes

- `IDataAccessPolicy` separates data authorization from TLS/JWT authentication and alias/operation
  admission. Select private-user, claim-based tenant/group, unrestricted or custom boundaries per
  server registration, using DI. Local embedded repositories still require no token.
- `[StorageAccessClaim]` adds an exact index to a declared ownership field.
  `ContractDataAccessPolicy<T>` checks that field against validated claims before an entire batch
  commits. The attribute alone does not enable authorization. The server uses the configured
  serializer and commits the original bytes without reserialization.
- A bounded partition-key index supports lazy, eager and disabled modes through
  `IPartitionKeyIndexFactory`. The default is lazy, with LRU eviction and retained budgets of
  10,000 keys, 16 partitions and 8 MiB estimated metadata per store. Errors discard derived state;
  reads rebuild from committed keys. No permission grants are cached.
- Keys, enumeration, batch operations, change cursors, subscriptions and client cache identity
  use the same approved scope. Clients cannot grant themselves membership through a selector.
- The guides now cover security boundaries, claim mapping, DI, ownership, offline limits,
  configuration, troubleshooting and performance choices.
- Two SQL benchmark projects pin their transitive PKCS dependency to patched version 9.0.21.
  That dependency is not included in the four public runtime packages. This addresses the
  [PKCS signature parsing advisory](https://github.com/advisories/GHSA-846f-f764-76mx) and
  [PKCS envelope parsing advisory](https://github.com/advisories/GHSA-2fmv-rv2c-59x7).

## Breaking upgrade from preview.12

1. Back up existing data and retain the previous runtime and encryption keys. Update all four
   packages together and rebuild custom adapters and clients.
2. Upgrade TCP clients and hosts together: the wire protocol is now **version 3**, with 14 message
   fields. Older peers are rejected. The stored IDB6/IDBE envelope formats are unchanged.
3. Replace `IsolateBySubject`, `TcpStoreAccessMode` and subject/shared capability flags with a
   server `IDataAccessPolicy`. Use `SubjectDataAccessPolicy` to retain the previous subject
   namespace, or `UnrestrictedDataAccessPolicy.Instance` for deliberate shared, unpartitioned access.
   A null registration inherits the host default; if neither exists, data is unrestricted.
4. Choosing a new claim/ownership policy changes the namespace. Perform an explicit trusted
   ownership migration and revalidation. Existing records are not automatically assigned to
   the first connecting user. Removing a policy is not a safe migration shortcut.
5. Rebuild disposable client caches or explicitly migrate pending writes. Old cache identities
   are incompatible. The client pins the server-approved scope and rejects a different scope
   on reconnect. Offline scope metadata is not a new remote authorization grant.
6. If the stored ownership field is mandatory, register the contract policy and its configured
   serializer on the server. A simple claim policy selects a partition but does not inspect fields.
   Custom decorators must preserve `IDataAccessWritePolicy` when they wrap a write policy.

Token validation, lifetime and membership-revocation strategy remain in the authentication/identity
provider integration. Direct local file access and downloaded offline data remain application/device
security concerns. These policies are not an arbitrary per-record predicate authorization engine.

## Performance and unchanged guarantees

In the recorded Windows loopback TLS/JWT benchmark, 100,000 records across 100 tenants gave a warm
1,000-record tenant enumeration of 81.505 ms with scanning and 6.653 ms with the bounded index.
Cold access still scans. Writes did not show a meaningful improvement, and ownership checks add
write overhead. These are encoded transport operations, not SQL, typed-query, WAN or concurrency
claims. See [the complete results](access-policy-production-20261028.md) and
[policy selection recommendations](data-access-policies.md#choosing-a-policy-for-performance).

Storage durability defaults, revision checks, transaction boundaries, configured encryption and
independent committed read copies are preserved. Real-device, physical power-loss, application
soak and other stable-release qualification limits remain; this is another preview, not stable 1.0.

## Integration documentation

- [Data policies, tenant/group selection and DI](data-access-policies.md)
- [Authentication and Keycloak configuration](jwt-authentication.md)
- [Security boundaries](security.md)
- [Online repository](tcp-repository.md) and [hosting/protocol](tcp-transport.md)
- [Configuration](configuration.md)
