# InterfaceDB 1.0.0-preview.12

Status: **baseline candidate under local validation; not published**.
All four public package projects use `1.0.0-preview.12`: InterfaceDB,
InterfaceDB.Storage.SegmentCore, InterfaceDB.Authentication and InterfaceDB.Transport.

## Baseline and scope

This candidate freezes public runtime commit
`4e63ddb786d7d89c4151e62535e623090b33cba3`, before further cache-read optimization.
Versioning and release documentation are separate changes. It includes the
reviewed storage corrections and A1 serializer-context optimization accumulated
since preview.11. The packages remain MIT and contain only public implementations.

### Committed reads and caching

- File and SegmentCore return independent committed objects, including nested
  mutable data, from ordinary reads and StrongObject/WeakObject caches.
- Editing a returned object does not change another reader or search result.
  Only a successful save publishes its replacement. Abandoned or rejected edits
  leave the previously committed value visible.
- Indexed cached searches no longer recheck every live cached object when the
  provider declares isolated reads. Custom providers keep their fallback unless
  they implement that contract.
- Copies use the configured serializer, lifecycle and encryption context.
  Cache hits do not serialize the cached object again. Weak entries can be
  collected after callers release their copies.
- Serializer paths skip constructing context only when the serializer does not
  consume it. Contextual encryption and native migration retain their full context.

This intentionally changes shared-reference and unsaved-edit visibility from
preview.11. Applications must call `SaveAsync` to publish edits and must not rely
on reference equality between independent reads.

### Inherited storage and query corrections

- Correct numeric conversion handling in indexed predicates, including safe
  integral promotion, and accurate scan-fallback explanations.
- Exact logical/physical key handling, including logical keys ending in digits.
- Index validation before writes, committed index publication, and visible
  fallback after maintenance/subscription failures.
- Compaction-safe streaming, transaction admission/fault handling and bounded
  revision tracking without dropping live-key revisions.
- Durable File publication and revision-clock handling that respects POSIX
  directory permissions and publication boundaries.
- Bounded logging, metrics and tracing support. Source analyzer `IDB005` flags
  supported non-ordinal indexed-string searches without changing semantics;
  it remains source tooling, not a new public NuGet package.

The [storage correctness upgrade guide](storage-correctness-upgrade.md) describes
each compatibility boundary and recovery rule.

## Performance is workload dependent

The [three-build Windows/SQL comparison](performance-proposal-review-20261020.md)
separates the cache change from A1. At 10,000 records, A1 reduces uncached
SegmentCore broad-read time by about 28–54% against the cache-only build.
Selective cached searches improve substantially. Copying increases direct-read
allocations and makes large cached results and scans slower than shared-object
reads. File does not show the same uncached speed gains. Single durable writes
retain their prior flush-policy costs.

No durability defaults, revision checks, encryption protections or transaction
guarantees were relaxed. These results do not qualify devices or physical
power-loss behavior, and are not a claim that every query is faster than SQL.

## Upgrade from preview.11

1. Stop writers and back up the complete store and encryption keys. Update all
   InterfaceDB packages together and rebuild applications and custom adapters.
2. Check the committed-copy behavior above. Save edits explicitly; use retained
   edit sessions when an original version must survive subsequent saves.
3. Review `QueryAsync` nullability and custom implementations: generic explicit
   implementations require `ValueTask<TImplementation?>` with
   `where TImplementation : class`.
4. Audit existing logical keys if the prior suffix bug could have collapsed
   identities. The fix cannot reconstruct an already damaged identity. Migrate
   affected records through repository APIs into a fresh store; do not rename
   encrypted files or rewrite raw physical keys.
5. Upgrade all writers sharing a store together. The revision clock uses a
   stable lease and atomic publication; old 16-byte clock values remain readable.
   Do not run old and new writers concurrently.
6. Streaming scans capture membership and resolve values in bounded batches.
   They permit saves while compaction runs, but do not provide a scan-wide
   transaction snapshot. Concurrent updates/deletes may affect later batches.
7. On uncertain I/O outcomes, quiesce and reopen the complete repository scope,
   inspect committed state, and reconcile before retrying. Never clear fault
   flags in place or blindly retry uncertain batches.

Default `IDB6`, `IDBE` v3 and TCP control-frame formats are unchanged by this
baseline. Earlier preview upgrades still require the documented ciphertext and
contract migrations. There is no gRPC/Protobuf format addition.

## Rollback and qualification

Keep the prior application and a verified quiescent backup. Roll back the
application and store together where a contract or identity migration occurred;
do not mix writer versions. Preview.11 also restores its old shared-cache
behavior and lacks these correctness fixes.

This remains a preview. Android/other target-device qualification, physical
power-cut evidence and representative application soak remain separate gates.
See [candidate validation](preview12-validation.md) for exactly what was run.
