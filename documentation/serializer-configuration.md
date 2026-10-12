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

Registering another serializer does not convert existing files. Keep existing databases on
their configured format, or perform an explicit verified conversion.

This change provides serializer/encryption injection. A Protobuf serializer and a gRPC
transport are not included. Existing built-in migration engines and encrypted-field value
encoding still use MessagePack; supporting another format throughout those paths is separate
work. Do not invoke those migration paths against a different envelope format.
