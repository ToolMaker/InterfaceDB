# Protobuf serializer validation — 4 November 2026

Implemented on public base `a1c6f26`, Windows x64, .NET SDK 10.0.403.
This record covers source validation. See the release notes for NuGet availability.

## Full regression run

`dotnet build InterfaceDB.sln -c Release -warnaserror:CS9057` passed.
`Scripts/Test-PublicSourceBoundary.ps1` verified 44 projects.
`Scripts/Test-AllSuites.ps1` completed successfully:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core | 428 | 10 |
| Analyzers | 31 | 0 |
| SegmentCore | 222 | 2 |
| Authentication | 84 | 0 |
| Public transport | 297 | 0 |
| Repository composition | 48 | 0 |
| Conflict resolution | 29 | 0 |
| Storage migrations | 95 | 1 |
| Atlas | 142 | 0 |
| Composed transport | 324 | 0 |
| Protobuf serializer (at full-run snapshot) | 25 | 0 |
| **Total** | **1,725** | **13** |

The skips concern Unix permissions/case sensitivity and unavailable symlink cases on
this Windows run. There were no failed tests.

## Final targeted checks

After the broad run, final checks added explicit TCP read/page byte equality and
DateTime kind preservation. protobuf-net's default date mapping dropped UTC/local
kind; the serializer now enables `IncludeDateTimeKind` before compiling the model.
No existing MessagePack or repository runtime implementation changed.

A final solution build passed with zero warnings/errors in its output. Against that
build, all affected serializer checks passed:

| Suite/filter | Passed | Failed |
| --- | ---: | ---: |
| Protobuf serializer, including three DateTime kinds | 28 | 0 |
| Public transport, serializer filter | 33 | 0 |
| Composed transport, serializer filter | 31 | 0 |

The targeted counts overlap the broad run; do not add them to the full-suite total.

## Requirements checked

- Existing serializer abstraction and keyed DI; no storage-provider or TCP runtime rewrite.
- Same repository save/update/query/reopen/conflict/delete scenarios for MessagePack and
  Protobuf across File, SegmentCore and Atlas, in strong and weak cache modes.
- Committed-copy isolation: unsaved edits do not change subsequent read results.
- Envelope identity, contract version, revisions, concrete type, encrypted fields and
  null-versus-empty extension metadata survive serialization.
- Whole-record encryption, field encryption and key rotation with authenticated context.
- Bounded streams/arrays, nesting depth, explicit type enrollment, malformed data,
  concurrent use, and frozen models.
- A forged 16 MiB payload length is rejected without allocating the advertised length.
- TCP direct reads and pages return stored bytes exactly, including ciphertext.
  Forwarding invokes no object serializer. A typed client deserializes each received
  record once per operation. Writes invoke one envelope serialization; pub/sub sends hints.
- TLS/authentication, data scope selection, revision checks, conflict handling and
  durability defaults remain in place.

Test data used the task-owned `C:\Temp\InterfaceDBProtobufBuild` and
`C:\Temp\InterfaceDBProtobufTests` directories. TRX evidence is retained under
the former directory's `full-results` and `results` folders.

## Boundaries

See [configuration](protobuf-serialization.md). This is Protobuf serialization for
the existing storage/TCP abstractions, not an HTTP/2 gRPC service. Existing
MessagePack-specific contract-upgrade engines cannot yet upgrade Protobuf stores.
There is no automatic format conversion or canonical fingerprint claim.

No throughput claim, Linux run, mobile/AOT qualification, power-loss qualification
or package publication is implied by this validation.
