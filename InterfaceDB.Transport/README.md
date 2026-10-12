# InterfaceDB.Transport

Optional MIT TLS TCP connector and host for InterfaceDB on .NET 10.

The client uses `TcpRepository<TContract>` (`IRepository<TContract>`) to access one remote
database. Database files stay on the host. The client uses bounded in-memory query snapshots;
it creates no local database, queues no offline writes, and fails requests when disconnected.

Includes JWT-authenticated access, strict revisions, atomic batches, disposable account-scoped
pub/sub hints, explicit compatibility manifests, and a runnable SegmentCore host. The default
envelope serializer remains MessagePack; a configured revision-aware serializer can be shared
with storage. TCP control frames retain their MessagePack protocol.

Install `InterfaceDB.Transport` version `1.0.0-preview.11`. Keep all InterfaceDB package versions aligned.

```shell
dotnet add package InterfaceDB.Transport --version 1.0.0-preview.11
```

See the [online TCP guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.11/documentation/tcp-repository.md),
[hosting guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.11/documentation/tcp-transport.md),
and [typed client sample](https://github.com/ToolMaker/InterfaceDB/tree/v1.0.0-preview.11/samples/InterfaceDB.TcpRepository.Client).

Configure TLS, JWT issuer/audience and application authorization before exposure. One process
owns each physical database. A lost write acknowledgement requires reconciliation before retry.
