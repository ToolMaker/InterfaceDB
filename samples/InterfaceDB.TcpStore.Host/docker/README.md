# Local Docker host

This sample runs the TLS TCP host and SegmentCore in one Linux container. The client connects to
`localhost:7443` using mutual TLS. Docker publishes the port on host loopback only, while the host
process binds `0.0.0.0:7443` inside the container.

The setup uses a persistent named volume for database files and mounts the server PFX and JSON
configuration read-only. The image contains only the Linux publish output. The client private key,
certificate authority private key and PFX password are never added to the image.

The host accepts an optional server-side `partitionIndex` object:

```json
"partitionIndex": {
  "mode": "Lazy",
  "maximumKeys": 10000,
  "maximumPartitions": 16,
  "maximumEstimatedBytes": 8388608
}
```

These are the defaults. `Disabled` retains scanning; `Eager` warms a bounded index at open.
The budget is per store and applies to estimated retained key metadata, not total process memory.
This setting does not change authentication, policy or durability. The stock JSON host supports
`dataAccessPolicy` values `none`, `subject` and `claim`. Interface-field ownership validation
uses the typed `AddContractDataAccessPolicy<T>` composition API and a server-registered serializer;
see [data access policies](../../../documentation/data-access-policies.md).

## Prepare and start

Use Docker Desktop with Linux containers and the .NET 10 SDK. Run these commands from the public
`InterfaceDB` repository root:

```powershell
pwsh -File samples/InterfaceDB.TcpStore.Host/docker/prepare-local.ps1

docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml up --build -d

docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml logs storage
```

The preparation script publishes the host for Linux and creates local development certificates,
configuration and the environment file under Git-excluded `artifacts/`. It does not install a
certificate into a machine trust store or persist the development CA's private key. Rerunning
preparation reuses existing credentials and configuration. Use `-Port 7444` to select another local
port, or `-SkipPublish` to reuse an already prepared Linux build. Use the generated development CA
to validate the server explicitly in a test client, while retaining hostname validation. Client configuration must use
the same manifest and alias as `artifacts/tcp-local/config/hostsettings.json`.

| Local artifact | Purpose |
| --- | --- |
| `artifacts/tcp-host-linux/` | Docker build context containing published binaries only |
| `artifacts/tcp-local/config/hostsettings.json` | Listener, store, certificate pin and manifest configuration |
| `artifacts/tcp-local/certificates/server.pfx` | Host TLS identity mounted into the container |
| `artifacts/tcp-local/certificates/client.pfx` | Development client TLS identity, kept outside the container |
| `artifacts/tcp-local/certificates/development-ca.cer` | Public development CA for explicit client trust validation |
| `artifacts/tcp-local/.env` | Local port and server/client PFX passwords; Compose injects only the server password |

Only the server PFX is mounted from the certificate directory. Client credentials remain on the
client side. Keep the generated artifacts private and do not add them to source control. These
development credentials are for local testing; production credentials and certificate rotation
belong to the deployment's secret-management process.

The service logs `host listening` after it opens the persistent store and binds the port. This is
startup evidence; a successful authenticated storage write/read establishes the application path.
The host permits one storage session per non-isolated alias or per resolved data scope in a policy-partitioned store. Dispose test clients before reconnecting to the same lease. Storage sessions have a fixed admission lifetime (five minutes by default); Ping does not extend it. Passive push listeners hold no storage lease.
See the [transport guide](../../../documentation/tcp-transport.md) for client code,
unknown-commit recovery and the complete operating limits.

To choose a different host port, set `IDB_LOCAL_PORT` in the generated `.env` file or process
environment before starting Compose. The container still listens on port 7443. The loopback
binding remains in place.

## Stop and preserve data

```powershell
docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml stop storage

docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml start storage
```

Docker sends SIGTERM, and the host closes the listener and disposes SegmentCore before exiting.
Compose allows 30 seconds for shutdown before forcing termination. Data remains in the
`interfacedb-tcp-local_database` named volume across stop/start and image rebuilds. A client whose
write acknowledgement is interrupted still needs to reconcile its write outcome.

To remove the container and network while preserving the database volume:

```powershell
docker compose --env-file artifacts/tcp-local/.env -f samples/InterfaceDB.TcpStore.Host/docker/compose.yaml down
```

Do not add `--volumes` or `-v` when retaining the database. There is one storage container and one
owning process for this volume. Do not scale the service to multiple writers.

## Container boundaries

The process runs as the .NET image's nonroot `app` user with a read-only root filesystem, writable
database volume and temporary `/tmp`. Linux capabilities are dropped and new privileges disabled.
The Dockerfile creates the data directory with ownership for `app` before the volume is initialized.
No host source directory, client PFX or CA private key is mounted into the container.

The Dockerfile is outside the build context: Compose resolves its path relative to
`artifacts/tcp-host-linux`. This avoids sending the source repository or the sibling
`artifacts/tcp-local` credential directory to the image build. The base runtime image may be pulled
from Microsoft's public registry; this setup does not push the resulting image anywhere.

Local Docker validates a Linux process, TCP connectivity and volume persistence on this machine.
It does not by itself qualify a cloud volume, production failover or physical power-loss behavior.

## Repeatable storage and migration tests

From the public repository root:

```powershell
pwsh -NoProfile -File tests/InterfaceDB.Transport.DockerTests/run-tests.ps1
```

The script publishes the current Linux host, builds `interfacedb-tcp-local:dev`, and runs the separate
Docker integration test project. Docker must already be running with Linux containers. Each fixture
pins the image ID and owns a unique labelled container, volume and ephemeral loopback port; cleanup
removes only those test resources. The existing `localhost:7443` service and its data remain available.
Test certificates are generated per fixture and cleaned up afterward. TRX results, including fixture
image IDs, are retained under `artifacts/tcp-migration-tests/docker`.

This project is deliberately run explicitly, outside the ordinary solution, so the usual
unit/integration suite does not acquire a Docker dependency or silently skip missing-Docker cases.
The native migration tests export unchanged remote bytes into a temporary local SegmentCore store,
run the real public maintenance API into a separate target, and exercise uploading the result to a
new Docker host. This is an explicit test maintenance procedure. For automatic trusted migration at server startup, deploy a version module as described in the [server version guide](../../../documentation/tcp-repository.md#server-versions); clients cannot upload migration code.

The two original defect reproductions are now regression tests: version mismatches must reject
without target writes, and property-encrypted values must survive migration and target reopen. Read the
[qualification findings](../../../documentation/tcp-transport.md#storage-and-migration-qualification--11-october-2026)
before using native migrations for application data.
