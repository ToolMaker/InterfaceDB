# InterfaceDB Protobuf serialization

This optional MIT package implements `IRevisionObjectSerializer<T>` using
[protobuf-net](https://github.com/protobuf-net/protobuf-net) under Apache-2.0.
It preserves InterfaceDB identity, contract version, revisions, encrypted fields
and extension metadata in a Protobuf envelope. Register it by database key for
File, SegmentCore, Atlas or the TLS TCP connector.

```powershell
dotnet add package InterfaceDB.Serialization.Protobuf --version 1.0.0-preview.18
```

Use matching InterfaceDB package versions. The package ships
`schemas/envelope.proto`. HTTP/2 gRPC hosting is separate work.
See the [registration and migration guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.18/documentation/protobuf-serialization.md)
and [preview.18 release notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.18/documentation/preview18-release-notes.md).
