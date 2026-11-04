# InterfaceDB 1.0.0-preview.18

Preview.18 adds authenticated TCP contract discovery and the optional
`InterfaceDB.Serialization.Protobuf` package. The ten MIT packages are versioned
together. Install only the modules your application uses.

- **TCP contract discovery.** Register an approved schema per store alias with
  `IContractDescriptorProvider`, or set `contractDescription` in the sample host.
  Use `DescribeContractAsync` or the sample client's `--describe-contract` flag
  to retrieve the active schema after the host authorizes access. Discovery
  reads no stored records. [Configuration and protocol](tcp-transport.md#optional-contract-descriptions)
- **Optional Protobuf serialization.** Register the revision-aware serializer
  for File, SegmentCore, Atlas or a compatible TCP store. The package includes
  the envelope schema at `schemas/envelope.proto` and depends on protobuf-net
  under Apache-2.0. [Configuration](protobuf-serialization.md)
- **Measured format tradeoffs.** Protobuf produced smaller payloads in the
  benchmark. MessagePack remains the default and encoded faster. The results
  cover the existing TLS TCP transport, SQLite and SQL Server.
  [Benchmark results and reproduction steps](performance-protobuf-sql-20261104.md)

## Install

```powershell
dotnet add package InterfaceDB --version 1.0.0-preview.18
dotnet add package InterfaceDB.Transport --version 1.0.0-preview.18
# Optional envelope serializer
dotnet add package InterfaceDB.Serialization.Protobuf --version 1.0.0-preview.18
```

See the [preview.17 package map](preview17-release-notes.md#packages) for the
other coordinated modules. Use preview.18 for every InterfaceDB package in the
application. The Protobuf package is the tenth package.

## Upgrade from preview.17

Rebuild clients and hosts with matching package versions. Existing databases
can continue using their registered MessagePack serializer. For schema
discovery, update the TCP host and client, then configure the schema on each
alias that should publish it. The existing TCP handshake and compatibility
discovery are preserved. Use the existing compatibility discovery method when
connecting to a host that predates the schema command.

Choose Protobuf for a new database or perform an explicit, verified format
conversion before switching an existing store. The built-in contract upgrade
engines for File, SegmentCore and Atlas expect MessagePack. Refer to the
[format and migration boundaries](protobuf-serialization.md#compatibility-and-migration-boundaries)
before selecting Protobuf for persisted data. HTTP/2 gRPC hosting remains
a separate roadmap item.

Treat this as the final feature preview candidate. Stable 1.0 qualification
includes physical power-loss testing, the remaining security checks and a
representative application soak on the supported platforms.
[Roadmap](roadmaps.md#final-preview-candidate)
