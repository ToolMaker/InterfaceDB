# Envelope serializer registration

## Preview.11 serializer registration

The public providers resolve `IObjectSerializer<TContract>` by database key. Register the
selected serializer before registering the provider, using the same
`IDbContractConfigDto<TContract>.EnvelopeDICacheKey`. Each key selects one serializer;
different keys can select different implementations.

```csharp
IRevisionObjectSerializer<IMyContract> serializer = configuredSerializer;
services.AddKeyedSingleton<IObjectSerializer<IMyContract>>(
    contractConfig.EnvelopeDICacheKey, serializer);
services.AddSegmentCoreRepository<IMyContract>(
    contractConfig, runtimeTypeAllowlist: allowedTypes);
```

The file provider uses the same keyed serializer pattern. A custom serializer controls
envelope payload bytes; provider-owned segment headers, journals and catalogs retain their
own formats. Existing MessagePack defaults and stored formats are unchanged.

## Required behavior

A writable repository requires `IRevisionObjectSerializer<TContract>`, which extends
`IObjectSerializer<TContract>`. Implement serialization, deserialization and revision reads
while preserving identity, version, revision, encrypted fields and extension metadata.
The repository supplies committed revisions; the serializer must round-trip them exactly.

Custom serializers are trusted application services. Their implementation must enforce
appropriate decoding bounds and allowed runtime types. If a singleton serializer serves
several repositories, it must support concurrent callers.

## Encryption and authenticated context

`EncryptingObjectSerializer<TContract>` now accepts any revision-aware serializer:

```csharp
var encrypted = new EncryptingObjectSerializer<IMyContract>(
    serializer, encryptor, maximumPayloadBytes: 4 * 1024 * 1024);

services.AddKeyedSingleton<IObjectSerializer<IMyContract>>(
    contractConfig.EnvelopeDICacheKey, encrypted);
services.AddSegmentCoreRepository<IMyContract>(
    contractConfig, encryptor: encryptor, runtimeTypeAllowlist: allowedTypes);
```

Register the fully configured wrapper when overriding the serializer registration.
Use the same encryptor instance for the serializer and envelope lifecycle. Providers do
not automatically add encryption around a custom serializer registration.

`IContextualObjectSerializer<TContract>` allows wrappers to receive authenticated storage
context for writes, reads, revision checks and rotation-aware reads. Provider helpers now
dispatch through this interface instead of checking for one concrete encryption wrapper.
Implementations expose their encryptor policy and preserve the supplied context.

The existing constructor that accepts `BinaryObjectSerializer<TContract>` remains available
and inherits that serializer's size limit. The new general constructor requires an explicit
positive limit. Its stream methods also enforce the wrapper's configured limit.

## Sharing with the TCP connector

`AddTcpRepository(options, databaseKey: "remote", serializerKey: "format")` selects an existing
keyed `IObjectSerializer<T>` registration. Direct construction accepts `options.Serializer`.
The same instance can serve storage and TCP. This shares serialization, not database state:
the connector does not create a local database. See [TCP serializer configuration](tcp-repository.md#reusing-the-configured-envelope-serializer--next-preview-preparation).

## TCP serialization passes

For an ordinary TCP repository write, the client prepares the envelope and calls its selected
serializer once. The TCP frame carries those bytes as a binary payload; the host writes that
payload to SegmentCore unchanged. It does not deserialize the object and serialize it again
just to store it.

On reads, the client deserializes each received record once within the operation. Indexes,
queries, revision checks and metadata acknowledgements reuse that materialized snapshot.
Saving an edited object performs one new serialization for the new stored representation.
The online repository currently loads a fresh bounded snapshot for each operation, so a new
operation can deserialize records again to observe current remote state.

Transport frame encoding is separate from envelope serialization: it encodes commands, keys,
limits and binary payload fields without interpreting domain objects. Encryption and buffer
copies also do not imply another envelope serialization pass.

A versioned host can independently deserialize a payload to validate its contract before
accepting a write; it still stores the validated bytes without re-encoding them. A migration
that changes a stored representation, or a write to another database with its own authenticated
encryption context, has its own necessary serialization work.

Call-count tests cover the default MessagePack format, a custom test format and that custom
format wrapped in encryption. They check ordinary writes/reads, batches, indexed queries,
conflicts, deletes, metadata acknowledgements, deltas and pub/sub hints.

## Format changes and migrations

### Unreleased infrastructure capabilities

`ICanonicalObjectSerializer<T>` supplies deterministic logical bytes for integrity
comparisons across application builds. `BinaryObjectSerializer<T>` implements it:
the canonical runtime type identity omits assembly versions, while the serialized
values, keys, contract version, revision and extension metadata remain covered.
This auxiliary encoding is not a replacement storage format. Ordinary `Serialize`,
deserialization and TCP payloads are unchanged. Custom serializers can implement
the capability using their own format and value semantics.

`ICommittedEnvelopeInventory<T>` exposes a fresh physical File inventory independently
of the application cache. It does not initialize watchers, hydrate objects, rotate
encryption keys or publish events. Its caller must hold provider exclusion while
enumerating and capturing committed snapshots; it is not an independent transaction.

File coordinator leases now use exclusive handles with delete-on-close. Abandoned
leases can be reclaimed after an exclusive open proves that their holder is gone;
age alone is never proof. Existing live exclusive leases remain protected. This
changes recovery after process termination, not commit durability or conflict checks.

Before acquiring File writer or migration leases, the coordinator validates exclusive
handle enforcement through `IFileLeaseValidation`. The default
`PlatformFileLeaseValidation` tests a uniquely owned file in the actual storage directory
and accepts only the platform's sharing-conflict error. Disabled Unix .NET file locking
or a successful competing exclusive open fails closed. A successful check is cached per
coordinator, so normal writes do not repeat this probe; failed checks remain retryable.
Read-only use does not run the writer probe. DI can supply another implementation that
proves equivalent exclusion. Network filesystems across hosts and mobile devices still
require qualification; this cannot exclude an unaware process that ignores locks.

File key lookup and scans also tolerate a payload deleted after catalog lookup but before
the file is opened. Already materialized results remain stable committed copies. Missing
directories, missing serializer dependencies, corruption and other I/O errors still fail.

Provider registrations also expose `RepositoryCompatibilityAlias` ownership metadata
so repository composers can redirect their own aliases without removing application
replacements or aliases belonging to another configured database.

Repository composers can expose the optional `IRepositoryDataAvailability` capability.
After initialization, consumers that reconcile whole datasets or perform authoritative
exports must require `IsDataComplete` (or call `RepositoryDataAvailability.RequireComplete`).
An incomplete query result cannot prove that an absent record was deleted. This is a
provider-neutral contract; it adds no migration policy or background work to ordinary
repositories. Ordinary providers that do not expose the capability retain their
existing behavior.

### Changing the stored format

Registering another serializer does not convert existing files. Keep existing databases on
their configured format, or perform an explicit verified conversion.

This change provides serializer/encryption injection. A Protobuf serializer and a gRPC
transport are not included. Existing built-in migration engines and encrypted-field value
encoding still use MessagePack; supporting another format throughout those paths is separate
work. Do not invoke those migration paths against a different envelope format.
