# Authenticated TCP client sample

This .NET 10 console client obtains a token, opens the commercial TLS TCP store and runs an authenticated Ping. The host must enable `JwtBearer` (or `MutualTlsAndJwtBearer` for combined authentication), validate the identity provider and authorize the token for the selected store. A valid token is required in both client modes.

Copy `clientsettings.example.json` into a local configuration file. Set the host, port, alias and **the exact compatibility manifest from the host configuration**. The example matches `../InterfaceDB.TcpStore.Host/hostsettings.jwt.example.json`; its identity service URL is a placeholder.

## Service account

Configure a confidential service client in your identity provider with client credentials enabled. For the Keycloak example, issue a JWT access token with audience `interfacedb-storage` and scope `interfacedb.storage`; the host also checks the Keycloak access-token `typ` claim. Supply the client secret through the environment variable named by `clientCredentials.clientSecretEnvironmentVariable` using your shell or secret manager. Keep credential values out of JSON, command arguments and source control.

From the public repository root:

```powershell
dotnet run --project samples/InterfaceDB.TcpStore.Client -- ./clientsettings.local.json
```

The provider validates HTTPS discovery against the exact configured `authority`, defaults to `client_secret_basic`, caches tokens in memory and refreshes through client credentials. `authenticationMethod: "Post"` is available only when discovery advertises `client_secret_post`. If a provider uses a token endpoint on a different HTTPS origin, list that exact origin in `clientCredentials.additionalTrustedTokenOrigins`. This allowlist controls where the client secret can be sent.

## An existing application token

Remove `clientCredentials` and set `"accessTokenEnvironmentVariable": "IDB_ACCESS_TOKEN"`. Set that variable using the identity SDK or login flow already used by your application. Exactly one token source is allowed. The host validates the supplied token, including its signature, audience and expiry. This sample does not implement browser login or a password grant.

## TLS and local private CAs

Normal certificate-chain and hostname validation applies by default. `targetHost` sets the expected certificate name when `host` is an IP address. For an isolated private CA, set `serverCaCertificatePath` to its public PEM/DER certificate. Use `identityProviderCaCertificatePath` separately for a private HTTPS identity provider. Relative paths resolve beside the configuration file; certificates are never installed in the machine trust store.

Custom roots still require the expected hostname, a valid chain, certificate validity and server-authentication usage. `privateCaRevocationMode` defaults to `Online`. A local development CA without revocation endpoints needs the explicit setting `"privateCaRevocationMode": "NoCheck"`; this changes revocation checks only for the configured private CA callbacks. The full certificate chain must be presented by the server because automatic certificate downloads are disabled.

For combined authentication, use `"authenticationMode": "MutualTlsAndJwtBearer"`, `clientCertificatePath` (a PFX file) and `clientCertificatePasswordEnvironmentVariable`. The host must authorize both the certificate and token.

## Optional storage probe

```powershell
dotnet run --project samples/InterfaceDB.TcpStore.Client -- ./clientsettings.local.json --probe
```

The probe checks that a random key is absent, prints its identifier, writes 64 opaque bytes, reads them, deletes the key and verifies it is missing. Use a dedicated raw test store: these bytes are not a domain envelope. The client requests only the read/write/delete capabilities needed by this probe. Ping alone requests no data-access capabilities.

No operation is automatically replayed. If a request fails after the probe key is printed, that key may remain and needs inspection. Exit code `3` specifically means a mutation may have committed without its acknowledgement. Reconcile the reported key before repeating; the sample does not reconnect to retry or delete after an uncertain result.

Exit codes: `0` success, `1` configuration/authentication/connection failure, `2` invalid command arguments, `3` unknown mutation outcome. Tokens, client secrets and response bodies are never printed. Configuration is limited to 64 KiB, unknown/duplicate properties are rejected, and environment-variable names are accepted in place of credential literals.

Run `pwsh -File samples/InterfaceDB.TcpStore.Client/verify-config.ps1` for ten local CLI rejection/redaction checks. These checks require no running host or identity service.
