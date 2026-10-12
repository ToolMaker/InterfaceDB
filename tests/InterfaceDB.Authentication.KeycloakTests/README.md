# Local Keycloak qualification

This explicit suite starts a separate HTTPS Keycloak 26.7.1 Linux container using a
unique test label, temporary realm, random service secret and temporary certificate
authority. It uses the installed image by immutable image ID and never modifies the
existing local Keycloak instance or the machine certificate trust store.

From the public repository, with Docker Linux containers running:

```powershell
docker pull quay.io/keycloak/keycloak:26.7.1
pwsh -File tests/InterfaceDB.Authentication.KeycloakTests/run-typed-tests.ps1
```

The project is excluded from the normal solution so routine unit tests do not start
Docker or need an identity server. It builds the real host and client samples.

## What is checked

1. OAuth client credentials obtain a real Keycloak access token; validated TLS TCP
   writes survive closing and reopening the SegmentCore database. Reconnection with
   a supplied token also reads and deletes the stored bytes.
2. Missing tokens, wrong audiences and missing storage scopes are rejected, leaving
   the store empty and available to an authorized connection.
3. Incorrect service secrets cannot acquire a token and are absent from errors.
4. The runnable host and client load their JSON configurations, trust the temporary
   private CA, log in through Keycloak and complete authenticated Ping plus an
   opaque-byte write/read/delete probe in separate processes.

The realm import defines a confidential service client, an explicit
`interfacedb-storage` audience mapper and the `interfacedb.storage` client scope.
The negative clients omit the scope or target another audience. Standard browser
and direct password grants are disabled for these service clients.

Only the fixture's uniquely labelled container is removed. Generated private keys,
realm credentials and the sample PFX are deleted during cleanup; diagnostic test
results and temporary test database files remain under ignored `artifacts/jwt-keycloak`.
This fixture uses Keycloak development mode solely for local qualification.

See [the authentication guide](../../documentation/jwt-authentication.md) for deployment
configuration, the supported token flows and the distinction between scope/alias
authorization and application domain permissions.

The typed test additionally verifies two independent client processes, revision conflicts,
account isolation, and Linux host recreation with persisted data. The script builds its host
image before running the full suite. It provides no persistent client database.
