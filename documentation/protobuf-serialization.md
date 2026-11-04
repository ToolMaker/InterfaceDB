# Protobuf envelope serialization

## Scope and availability

`src/InterfaceDB.Serialization.Protobuf` adds
`Idb.Serialization.Protobuf.ProtobufObjectSerializer<TContract>`, an implementation of
`IRevisionObjectSerializer<TContract>` using protobuf-net 3.4.40 on .NET 10. It is an
optional source module, not part of the already published preview.17 packages.

Protobuf is the normal serialization format used by gRPC. This module implements the
serialization piece; it does not introduce a gRPC service, HTTP/2 endpoint, generated
client, or replacement authentication path. The current TLS TCP connector can already
carry these envelope bytes.

Both the envelope and its entity payload use Protobuf. No intermediate MessagePack
entity conversion takes place. Provider journals/catalogs and TCP command frames keep
their existing formats. Encrypted-field plaintext retains the existing field codec
inside its authenticated ciphertext.

## Explicit schema and registration

Reference the serializer project and configure a model at the application's composition
root. Stable field numbers and explicit type IDs replace assembly-qualified names in
the persisted entity identity. Register all nested types needed by the model as well.

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Serialization.Protobuf;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Meta;

// MyRecord implements IMyContract / IStoredContract<IMyContract>.
var model = RuntimeTypeModel.Create();
model.Add(typeof(MyRecord), applyDefaultBehaviour: false)
    .Add(1, nameof(MyRecord.Name))
    .Add(2, nameof(MyRecord.Score));

var serializer = new ProtobufObjectSerializer<IMyContract>(
    model,
    new Dictionary<string, Type> { ["my-record-v1"] = typeof(MyRecord) },
    maximumPayloadBytes: 4 * 1024 * 1024,
    maximumDepth: 64);

services.AddKeyedSingleton<IObjectSerializer<IMyContract>>(
    contractConfig.EnvelopeDICacheKey, serializer);
services.AddSegmentCoreRepository<IMyContract>(
    contractConfig, runtimeTypeAllowlist: new(typeof(MyRecord)));
```

Register before the provider. File and Atlas resolve the same keyed abstraction.
Different database keys can select different serializers. The constructor compiles and
freezes the supplied model, enables DateTime-kind preservation and disables automatic type enrollment. Do not mutate or
reuse that model to configure another serializer. A configured serializer supports
concurrent calls.

Map stored values only. Do **not** map `IStoredContract<T>.Envelope`: the serializer
stores envelope metadata separately and the repository attaches the restored envelope.
Each concrete implementation needs a unique stable type ID. Unknown IDs and runtime
types fail closed; payloads cannot request arbitrary .NET type loading.

Use protobuf-net's explicit schema features for nullable values, collections, nested
messages and inheritance where required by your contract. Protobuf defaults/presence
are schema semantics; they are not a promise to preserve every possible CLR object's
null-versus-empty distinction automatically. The envelope's own null-versus-empty
extension metadata is preserved explicitly. Never renumber or reuse field tags.
Keep readers and writers on compatible contract versions. Decimal/DateTime mappings
can use protobuf-net extensions; cross-language services need matching entity schemas.

The stable envelope schema is [envelope.proto](../src/InterfaceDB.Serialization.Protobuf/envelope.proto).
The format identifier is `InterfaceDB.Protobuf.Envelope.v1`.

## Encryption and limits

Use the existing `EncryptingObjectSerializer<T>` decorator for whole-record encryption,
and supply that same encryptor to the provider's envelope lifecycle. Registration does
not automatically wrap a custom serializer. See [serializer configuration](serializer-configuration.md#encryption-and-authenticated-context).
The File provider accepts a keyed `IEncryptor` registration before `AddFileRepository`.

Context authentication, encrypted fields and committed revisions use the existing
repository behavior. Protobuf itself provides no encryption or authorization.

The default maximum is 4 MiB for the entire plaintext envelope; the entity payload
shares that bound. Streams are bounded even when they cannot seek. Extension metadata
is limited to the core's 64 KiB limit. Entity nesting defaults to 64. Align these
settings with the repository, encryption-wrapper and negotiated transport limits,
allowing for encryption framing overhead. Async methods honor cancellation and leave
caller-owned streams open. Revision inspection parses the bounded envelope without
constructing the entity; it is not the MessagePack format's fixed-header fast path.

## Reusing it over TCP

Use the same keyed serializer registration for TCP:

```csharp
var format = ProtobufObjectSerializer<IMyContract>.EnvelopeFormat;
var options = existingTcpOptions with
{
    SerializerEnvelopeFormat = format,
    Connection = existingTcpOptions.Connection with
    {
        Manifest = existingTcpOptions.Connection.Manifest with
        {
            EnvelopeFormat = format
        }
    }
};
services.AddTcpRepository(options, databaseKey: "remote",
    serializerKey: contractConfig.EnvelopeDICacheKey);
```

The host's store manifest must declare the same format. Every typed reader/writer,
and any versioned host that decodes envelopes for validation, needs the matching
model and serializer. Keep TLS, token validation, access policies and scope selection
configured as before.

For ordinary opaque-store TCP writes, the client serializes the envelope once and the
host stores those bytes unchanged. Reads decode each returned record once per operation.
Direct reads and pages return exactly the stored bytes, including ciphertext, without object deserialization on the host.
Pub/sub messages are change hints and do not encode/decode entities. A subsequent
explicit read retrieves the stored entity. Envelope framing, encryption and byte copies
are distinct from repeated entity serialization.

## Compatibility and migration boundaries

Selecting this serializer is opt-in. Existing MessagePack stores and defaults are
unchanged. Use a new database or an explicit verified conversion; changing DI alone
does not convert stored data, and there is no automatic format fallback.

The existing File, SegmentCore and Atlas **contract-upgrade migration engines parse
MessagePack**. Do not run those engines against Protobuf stores. Making those migration
codecs provider-neutral is separate work, required before enabling automatic contract
upgrades for a Protobuf database. Forward-compatible optional Protobuf fields do not
replace InterfaceDB's contract-version negotiation or migration rules.

The serializer deliberately does not claim `ICanonicalObjectSerializer<T>`.
General Protobuf output is not a canonical logical fingerprint (for example,
dictionary order can differ). Provider-to-provider storage migration must be given
an independently verified `IStorageMigrationFingerprint<T>` implementation or a
suitable canonical logical serializer. Do not assume raw Protobuf bytes are a stable
cross-build fingerprint.

## Validation

The shared repository tests exercise MessagePack and Protobuf across File, SegmentCore
and Atlas with StrongObject and WeakObject caching, whole-record and field encryption,
save/update, indexed query, reopen, conflicts, committed-copy isolation and deletion.

The [Windows validation record](protobuf-validation-20261104.md) contains the full-suite results and final targeted checks.

Dedicated tests cover independent wire bytes, multiple concrete types, Unicode, null
metadata, decimal/date values, malformed and oversized input, allowed types, frozen
models, concurrent use, bounded depth, forged lengths, DateTime kind and authenticated key rotation. Existing TCP tests
also run with Protobuf for encrypted/plain storage, DI reuse, reopen, batches, deltas,
metadata acknowledgements, pub/sub and exact serialization call counts.

Run the serializer suite with:

```text
dotnet test tests/InterfaceDB.Serialization.Protobuf.Tests -c Release
dotnet test InterfaceDB.Transport.Tests -c Release --filter FullyQualifiedName~Serializer
```

The [benchmark guide](../StorageBenchmarks.Transactions/ProductionGate/SQL_COMPARISON.md#comparing-envelope-serializers)
covers envelope size, codec cost, TCP reads and comparison with SQLite and SQL Server. See the [measured results](performance-protobuf-sql-20261104.md). Native AOT and mobile devices
have not been qualified by these desktop tests.

References: [gRPC introduction](https://grpc.io/docs/what-is-grpc/introduction/),
[protobuf-net](https://github.com/protobuf-net/protobuf-net),
[Protobuf encoding and noncanonical serialization](https://protobuf.dev/programming-guides/encoding/).
