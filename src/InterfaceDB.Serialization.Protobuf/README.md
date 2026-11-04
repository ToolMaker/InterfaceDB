# InterfaceDB Protobuf serialization

Optional MIT source module implementing `IRevisionObjectSerializer<T>` with
[protobuf-net](https://github.com/protobuf-net/protobuf-net) (Apache-2.0). It writes a Protobuf envelope and Protobuf entity payload, preserving
InterfaceDB identity, contract version, revisions, encrypted fields and extension
metadata. Storage providers and the existing TCP connector consume the same abstraction.

See [configuration and compatibility](../../documentation/protobuf-serialization.md).
The envelope schema is [envelope.proto](envelope.proto).

This module is not in the published preview.17 packages. Reference the project to use
it from source. HTTP/2 gRPC services are separate roadmap work.
