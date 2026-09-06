# Envelope revisions and conditional writes

This working-tree change introduces free, provider-neutral optimistic concurrency. It has not been packaged or assigned a new preview version.

## Application contract

Every `IObjectEnvelope<TContract>` carries `long RevisionTimestampUtcMicroseconds`. This timestamp is the revision token itself; there is no second public revision counter. Save a returned entity for subsequent edits:

```csharp
await repository.InitializeAsync();
person = await repository.SaveAsync(person);
var loadedRevision = person.Envelope!.RevisionTimestampUtcMicroseconds;
person.Name = "Updated";

try
{
    person = await repository.SaveAsync(person);
}
catch (WriteConflictException<IPerson> conflict)
{
    // A fresh, isolated read, requested only when needed.
    var current = await conflict.ReadCurrentAsync();
    // Decide explicitly whether to discard or reconstruct the edit.
}
```

A new entity without an envelope inserts only when its physical identity is absent. An existing entity saves or deletes only when its expected revision exactly equals the current committed revision. Both older and newer mismatches reject. Missing is distinct from an existing legacy revision of zero. An old copy cannot recreate a deleted object. Explicit recreation starts from a new envelope-less entity.

A successful save advances the timestamp and stores it atomically with that save's payload. The returned entity represents this operation's committed values. The input receives its accepted envelope after commit as well, but a strategy may have resolved different values, so prefer the return value. Rejected, failed, or pre-commit cancelled saves do not advance the caller's revision. A cancellation arriving after physical commit cannot undo the save.

The implementation stages a separate envelope and a shallow CLR object shell. Committed revision metadata is independent of editable cached envelopes. This prevents an edit to a cached timestamp from changing the storage precondition. It does not make shared mutable entities or nested object graphs safe for concurrent application mutation. Separate editors should use separate object copies; editing-baseline capture and merge are not implemented.

`WriteConflictException<T>` exposes the key and expected/current revisions at rejection. `ReadCurrentAsync` materializes a fresh detached committed value, or null after deletion. Because it executes later, that value may be newer than the rejection revision. It is neither an editing baseline nor a historical snapshot.

## Coordination and ownership

| Path | Validation through publication | Readers | Bulk behavior |
| --- | --- | --- | --- |
| File | Migration writer lease plus the existing cross-process per-payload sidecar lock spans revision read, staging, atomic replacement/deletion, and cache publication | Open committed payloads with delete sharing; never take the writer guard | Independent commits; partial success is explicit |
| Segment Core | Ordered acquisition of 256 per-provider writer stripes spans revision validation, transaction commit, and cache publication | Existing committed-record reads; no revision writer locks | Existing transaction commits the accepted batch atomically |

Unrelated File paths proceed independently. Segment stripes can collide, but ordinary writes do not acquire every stripe. Maintenance backup acquires all revision stripes before taking a consistent backup. Store ownership still permits only one embedded writer process per live root.

The revision guard exists below `IRepository<T>`, so direct envelope-provider writes, bulk writes, and deletes use the same policy. Search indexes publish committed results after storage succeeds. Runtime publication order prevents a delayed local index update from replacing a newer write or deletion. This order is internal and is not a persisted revision token.

Repository notification scopes retain the encoded result of their actual commit; they do not reread a later writer's result and label it as this save. Rejected writes publish no event. A stale-write rejection does not fault existing subscriptions. Disabled notifications and enabled notification support with no subscribers remain distinct paths.

For File partial failures, `StorageBulkOperationException` implements `IStorageBulkOperationResult`. Cancellation uses `StorageBulkOperationCanceledException : OperationCanceledException` with the same result contract. `SucceededIndexes` and `CommittedItems` identify completed writes. Indexes and notifications publish those items before the exception escapes. Do not blindly retry the whole input batch with its old revisions. Duplicate input references or existing physical keys reject up front.

## Strategy extension

`IWriteConflictStrategy<T>` is registered keyed by the repository's envelope DI cache key, or unkeyed for a default. The built-in `StrictTimestampMatchStrategy<T>` performs exact equality. Constructor injection is also available for provider integrations.

A strategy receives the staged candidate, expected/current revisions, insert status, and lazy isolated current-object retrieval. It returns rejection, acceptance of the candidate, or a resolved replacement envelope. Replacement candidates are restaged before timestamp assignment, must preserve physical key and location, and determine the committed and indexed values. The strategy executes under the entity writer guard: keep it bounded and do not recursively write the same entity.

Infrastructure metadata operations deliberately use strict destination matching even when an application resolver is registered. No automatic merge, last-writer-wins resolver, editing snapshot, or conflict UI is supplied.

## Serialization and legacy stores

The current writer emits `IDB6`: four magic bytes, an eight-byte little-endian revision, then the former IDB5 envelope fields and MessagePack object body. IDB2–IDB5 remain readable with revision zero. Cache flags, encrypted-field metadata, opaque extension bytes, runtime type, key, and version tag remain preserved. Unknown/truncated revision headers fail rather than silently assuming a valid token.

Legacy zero records upgrade on the first matching save. Other copies still carrying zero then conflict. Migration preserves a positive source revision; zero-revision source data receives a revision on its target commit.

Old readers cannot read IDB6. Do not mix old writers with revised writers on a live store. Stop old processes, retain a complete pre-upgrade backup, rebuild custom providers/envelopes/serializers, then open the upgraded store. A new required envelope property and serializer capability are source/API compatibility changes; optional constructor parameters also require rebuilding existing binaries. These changes ship in preview.7; see the matching release notes.

Custom serializers must implement `IRevisionObjectSerializer<T>`, persist the revision, and correctly implement header reads. Unsupported serializers fail explicitly. File coordinators must implement `IConditionalFileWriteCoordinator`; disabling sidecar locks also fails explicitly for envelope writes, including Performance mode. Performance mode uses atomic replacement with its weaker flush policy.

Plaintext File revision checks read only the 12-byte prefix; they do not deserialize the old entity. Segment keeps compact independent revision metadata populated from committed payloads and writes. A cold embedded entry may require reading its encoded record once. Whole-envelope encryption keeps its existing authenticated outer framing: reading a revision requires reading and decrypting the complete encoded payload, but not deserializing its entity. This remains a cost limitation for encrypted File saves. Lazy conflict retrieval, metadata-only acknowledgement, and ordinary materialized reads do deserialize when requested.

## Clock, backup, and restore

Generated values are UTC microseconds with a monotonic logical floor. Equal wall-clock ticks and wall-clock rollback advance past the observed committed value. They are not a guarantee of accurate event wall time or global uniqueness across devices.

Each store root reserves a durable range in `.idb-revision-clock` before using it. The sidecar contains a high-water mark plus its complement; corrupt/truncated state fails closed. Range reservation amortizes durable clock I/O across ordinary saves. Failed writes may consume timestamps, and reopening skips the unused remainder of a range. Import reserves past its source revision before committing it, including future-dated source values. This keeps deletion/recreation after reopen from reusing an earlier local revision.

Segment provider backups include the clock sidecar under the maintenance guard. File backups must capture the whole quiescent data root, including the sidecar. Never remove/reset it on an active store. Restoring an older whole-store backup creates an older history; discard outstanding application copies and coordinate synchronization before resuming. Copying identical stores to independent devices does not provide globally unique timestamp allocation.

## Metadata, import, and raw storage

The metadata overload without a condition supports inserts only. Detached updates must supply `EnvelopeWriteCondition(ExpectedRevision)`; they cannot infer the expected revision from a fresh destination lookup.

`EnvelopeRevisionAction.Import` preserves a positive source revision while checking the explicitly supplied destination revision. Use `ExpectedMissing: true` for an absent destination. It is a replication/maintenance capability, not an ordinary save. Authoritative import can move to an older source revision deliberately; the store clock still retains its durable high-water mark.

`MetadataOnly` requires both the existing revision and exact expected opaque metadata bytes. It retains the committed entity and revision while replacing extension metadata. Candidate entity changes are ignored. This prevents an acknowledgement from overwriting a newer pending delivery state with the same logical revision.

Raw byte stores, raw file coordinators, and administrative encoding replacement are outside the logical entity contract. Embedded envelope providers privately own their raw store and its exclusive process lease; a second raw writer cannot open that live root. Raw File publish/delete entry points reject roots once the revision clock exists. Encoding-only `ReplaceIfUnchangedAsync` remains a trusted whole-byte compare-and-replace operation for key rotation; it must preserve the envelope revision. Direct filesystem edits or a custom raw implementation can bypass the contract and are unsupported concurrent mutation paths. Metadata APIs remain coordinated; opaque metadata is not an unchecked logical-write back door.

See [revision qualification](envelope-revisions-qualification.md) for tests and public-provider measurements. Earlier performance reports describe their original builds and have not been rewritten.
