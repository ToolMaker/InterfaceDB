# Typed TCP repository sample

This .NET 10 console client uses `IRepository<IExampleProfile>` over TLS TCP. Supply the signed-in
user's access token through `IDB_USER_ACCESS_TOKEN`. The application's identity SDK owns login.

1. Build this project and `../InterfaceDB.TcpStore.Host` from this public source checkout.
2. Copy the sample JSON files to a new deployment directory. Configure a valid server certificate,
   Keycloak issuer/JWKS, database audience and scope. Keep `isolateBySubject: true` and the exact
   `ExampleProfileConfig.Manifest`. Use a new data volume/directory and unused port.
3. Supply the PFX password through the host's configured environment variable and the access token
   through the client's variable. Never put credentials in committed JSON or process arguments.
4. Start the host with its config, then run:

```powershell
dotnet run --project samples/InterfaceDB.TcpRepository.Client -c Release -- /deployment/clientsettings.json create Example
dotnet run --project samples/InterfaceDB.TcpRepository.Client -c Release -- /deployment/clientsettings.json read
dotnet run --project samples/InterfaceDB.TcpRepository.Client -c Release -- /deployment/clientsettings.json update 1
dotnet run --project samples/InterfaceDB.TcpRepository.Client -c Release -- /deployment/clientsettings.json delete
```

There is one profile per principal. Two users can have separate records at the same key.
For multiple objects configure `ObjectCardinality.Multiple`, or supply an application
`IEnvelopeStorageIdentityFactory<TContract>`.

For a private CA set `serverCaCertificatePath`. Only for a development CA without revocation
infrastructure set `developmentCaWithoutRevocation: true`; hostname/EKU checks remain active.
Normal deployment uses platform trust/revocation.

Exit 0: success. Exit 2: revision conflict. Exit 3: unknown outcome requiring reconciliation.
Exit 1: other failure. Output is JSON without credentials. Optional update barrier files are
exclusively for the separate-process test.

See [integration and limits](../../documentation/tcp-repository.md) and run
`tests/InterfaceDB.Authentication.KeycloakTests/run-typed-tests.ps1` for isolated
Linux Docker/Keycloak verification.
