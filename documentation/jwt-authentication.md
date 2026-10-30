# JWT access for the TCP store

This guide covers JWT modes. Preview.14 also supports a separate
[native username/password provider](native-account-authentication.md), with no Keycloak dependency.
Selecting it is explicit on both client and host; JWT configuration remains supported unchanged.

The selected deployment uses **TLS plus a required JWT access token**, with local Keycloak as the
first identity provider. Every admitted storage session authenticates first. A client with no token
can use its configured service credentials to obtain one before opening the TCP connection; a
client with neither a token nor configured credentials receives no storage access.

The optional MIT `InterfaceDB.Authentication` package uses namespace `Idb.Authentication`.
It is introduced in preview.11; see the [release status](preview11-release-notes.md). InterfaceDB's public embedded core remains
authentication-agnostic. The [TCP guide](tcp-transport.md) describes storage and retry
semantics; this guide describes identity and access. Tenant/group selection and ownership-field
validation are documented in [data access policies](data-access-policies.md). That policy API is
a preview.13 change and is not included in the published preview.12 package.

## Host configuration

Start with [hostsettings.jwt.example.json](../samples/InterfaceDB.TcpStore.Host/hostsettings.jwt.example.json).
Set `authenticationMode` to `JwtBearer` on the host and client. Supply the server TLS certificate,
its environment-sourced password, persistent data path and exact compatibility manifest as usual.
JWT mode requires no client certificate; server certificate/hostname validation remains mandatory.

The sample's Keycloak settings are placeholders:

| Setting | Required deployment value |
| --- | --- |
| `issuer` | Exact HTTPS realm issuer, for example `https://identity.example.com/realms/interfacedb` |
| `metadataAddress` | That realm's `/.well-known/openid-configuration` URL |
| `jwksAddress` | Explicit trusted `/protocol/openid-connect/certs` URL; discovery must agree exactly |
| `audiences` | The storage resource audience, for example `interfacedb-storage` |
| `algorithms` | The configured asymmetric signature algorithms; sample `RS256` |
| `tokenTypes` | Sample `JWT`, matching Keycloak's default access-token header |
| `requiredClaimValues` | Sample `{ "typ": "Bearer" }`, rejecting tokens with a different payload discriminator |
| `requiredTokenScopes` | Sample `["interfacedb.storage"]` |

Configure the Keycloak client/service account, storage audience mapper and allowed scope to produce
these claims. Scope requests do not create permissions or automatically add the storage audience.
Use the exact externally reachable HTTPS issuer consistently for discovery, validation and clients.
Keycloak's [OIDC endpoint documentation](https://www.keycloak.org/securing-apps/oidc-layers)
describes discovery, token and certificate endpoints. Its
[token manager](https://github.com/keycloak/keycloak/blob/main/services/src/main/java/org/keycloak/jose/jws/DefaultTokenManager.java)
uses header `JWT` by default and supports `at+jwt` when the RFC 9068 header setting is enabled.
If that setting changes, change the host's accepted type deliberately. The validator's general
default is `at+jwt`; the Keycloak example explicitly selects its issuer-specific profile.

The sample rejects unknown JSON fields, unnamed/numeric authentication modes, invalid authorities,
empty required scopes and JWT settings attached to certificate-only mode. It accepts HTTPS JWKS
settings; use the library API for offline `SigningKeys`. Credentials, tokens and private signing keys
do not belong in JSON configuration or source control. The host needs only public signing keys.

For local HTTPS Keycloak using a private CA, set `identityProviderCaCertificatePath` to its public
PEM/DER root certificate. Relative paths resolve beside the host configuration. The custom trust
policy still checks hostname, certificate validity, server-authentication usage and a chain rooted
in that CA; it installs no machine certificates and disables certificate downloads. Supply the
complete intermediate chain from the server. `privateCaRevocationMode` defaults to `Online`; an
isolated development CA without revocation endpoints requires explicit `"NoCheck"`. This option
affects only the configured private CA callback. The runnable client has matching private-CA options.

### Authentication modes

| Mode | Requirements |
| --- | --- |
| `JwtBearer` | TLS server identity and a validated, authorized access token; selected deployment path |
| `MutualTlsAndJwtBearer` | All JWT requirements plus an authorized TLS client certificate |
| `MutualTls` | Authorized TLS client certificate; explicit compatibility path for existing integrations |

The library and original sample configuration retain `MutualTls` as their API default for existing
callers. Always set `JwtBearer` explicitly for the token-required deployment. A JWT endpoint does
not fall back to certificate-only or anonymous access, and client/server mode mismatches reject.
Do not expose a separate `MutualTls` endpoint for a deployment that requires tokens on every path.
All modes use TLS 1.2 or 1.3; there is no plaintext TCP mode.

## Supplying a token

An application that already uses an identity SDK can supply its access token:

```csharp
using Idb.Authentication;
using Idb.Transport;

var tokens = new FixedAccessTokenProvider(new AccessToken(accessToken, expiresAt));
await using var store = await TcpEncodedEnvelopeStore.ConnectAsync(new()
{
    Host = "storage.internal.example", Port = 7443,
    StoreAlias = "catalogue", Manifest = manifest,
    AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
    TokenProvider = tokens
});
```

`FixedAccessTokenProvider` validates the bearer-string format and rejects a known expired token.
It does not refresh or persist the token. `AccessToken.ExpiresAt` is optional client metadata;
the host always verifies the signed JWT expiration independently. `AccessToken.ToString()` is
redacted, but `Value` intentionally exposes the credential to the transport: never log or serialize it.

## Acquiring a service token first

For a trusted service using OAuth client credentials:

```csharp
using var tokens = new ClientCredentialsAccessTokenProvider(new()
{
    Authority = "https://identity.example.com/realms/interfacedb",
    ClientId = "storage-service",
    ClientSecretEnvironmentVariable = "IDB_OIDC_CLIENT_SECRET",
    Scope = "interfacedb.storage"
});

await using var store = await TcpEncodedEnvelopeStore.ConnectAsync(new()
{
    Host = "storage.internal.example", Port = 7443,
    StoreAlias = "catalogue", Manifest = manifest,
    AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
    TokenProvider = tokens
});
```

`ConnectAsync` awaits `IAccessTokenProvider.GetAccessTokenAsync` before TCP connect. The service
provider discovers the token endpoint, sends `grant_type=client_credentials`, caches the token and
refreshes before expiration when another token is requested. Concurrent requests share one fetch.
The transport connect timeout includes acquisition; increase it deliberately if the identity
provider needs more time. A token acquisition failure opens no storage connection.

Supply a secret using `ClientSecretEnvironmentVariable` or `ClientSecretProvider`, not both.
HTTP Basic client authentication is the default. Explicit `ClientSecretAuthenticationMethod.Post`
is available when the discovery document advertises it. Discovery must return the exact issuer;
the token endpoint must be HTTPS and on that authority's origin unless the operator explicitly
adds another HTTPS origin to `AdditionalTrustedTokenOrigins`. Redirects are disabled. HTTP errors,
credentials and token response bodies are omitted from `TokenAcquisitionException`; `Failure`
provides a safe diagnostic category. This component does not store refresh tokens.

Client credentials identify the service account. They do not perform a human's interactive login.
For browser/native-app sign-in, use the application's supported OIDC SDK and supply the resulting
access token through `IAccessTokenProvider`; a built-in browser/PKCE/device-code UI is future work.
Web browsers cannot directly open this raw TLS TCP transport. Their application backend remains
responsible for user/resource authorization and the storage call.

## Host API and authorization

`JwtAccessTokenValidator` implements:

```csharp
ValueTask<AuthenticatedIdentity> ValidateAsync(string token, CancellationToken cancellationToken);
```

Construct it with `JwtAccessTokenValidatorOptions`, an optional owned HTTP handler, and an optional
`TimeProvider`. Each `JwtAuthorityOptions` sets a unique local `Id`, exact `Issuer`, accepted
`Audiences`, `Algorithms`, `TokenTypes`, required claims and fixed HTTPS endpoints. Alternatively,
set `SigningKeys` to trusted asymmetric `SecurityKey` instances with unique `KeyId` values and omit
network endpoints. Static keys are deployment configuration; changing them requires replacing the
validator. The validator uses pinned `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 for JWT
cryptographic validation; see [the package](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens/8.23.0)
and [Microsoft validation parameters](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters).

Set `TcpStoreHostOptions.TokenValidator` and `AuthorizeIdentity`. The latter receives the validated
`AuthenticatedIdentity` (`Principal`, actual `ExpiresAt`, `AuthorityId`), store alias and requested
capabilities. It runs during admission and before each operation. The host requires both in JWT
modes and refuses an unauthenticated or expired result. Dispose the validator only after the host
has stopped and drained work.

The sample policy requires its exact configured alias and **every configured scope word** in the
validated `scope` claim. This admits the configured store's operations, including writes; the
separately registered `IDataAccessPolicy` determines which data that caller may access. With no
data policy, admission grants the entire alias dataset. For tenant/family separation, register
`ClaimDataAccessPolicy("tenant_id")` or `ClaimDataAccessPolicy("groups")`. To enforce matching
ownership fields on saves, use `ContractDataAccessPolicy<T>` and interface declarations.
Additional domain invariants and per-record authorship rules remain application policy.
Client-provided aliases, keys, manifests and payloads never establish permission.
For atomic batches the operation authorization mask always includes `AtomicBatch`, plus `Write`
when any entry is an upsert and `Delete` when any entry is a deletion.

### Tenant and group claims

The following is an illustrative decoded access-token payload, not a credential:

```json
{
  "iss": "https://identity.example.com/realms/interfacedb",
  "sub": "member-123",
  "aud": "interfacedb-storage",
  "scope": "interfacedb.storage",
  "tenant_id": "tenant-a",
  "groups": ["family-a", "club-b"]
}
```

The real signed token must also satisfy the configured lifetime/type checks. Configure the
identity-provider integration to emit the chosen stable membership claim in its access token.
InterfaceDB consumes validated claims; it does not query group membership from Keycloak.
`ClaimDataAccessPolicy("tenant_id")` selects `tenant-a`; a `groups` policy would require an
authorized `RequestedScope` selection here because two groups are present. Pick the policy
appropriate to the dataset rather than treating all claims as simultaneous grants.

For `[StorageAccessClaim("tenant_id")] string TenantId`, the contract policy also rejects a save
whose field differs from `tenant-a`. A role can allow writing while the tenant policy still limits
where that write goes. Multiple criteria, inheritance, keyed DI and trusted composite memberships
are covered in the [policy integration guide](data-access-policies.md#declaring-ownership-on-the-stored-interface).

## Validation, key refresh and bounds

The JWT issuer is decoded only to find one exact preconfigured authority. Validation then requires
a permitted asymmetric signature, matching issuer/audience/header type, signed expiration, valid
lifetime and nonempty string `sub`, plus configured claims. Duplicate JSON members, unsigned tokens,
unknown authorities/keys and token-supplied `jku`, `x5u`, `jwk`, `x5c` or critical headers reject.
No URL from a token is fetched. Discovery cannot replace the configured JWKS endpoint.

| Default bound | Value |
| --- | --- |
| TCP/validator access token | 16 KiB |
| Validator metadata/JWKS document | 256 KiB |
| Signing keys per authority | 64 |
| Configured authorities | Maximum 32 |
| Refresh interval | 15 minutes |
| Minimum interval between refresh attempts | 30 seconds |
| Absolute age of last successful network configuration | 1 hour |
| Validator HTTP timeout | 10 seconds |
| Allowed future `nbf` clock skew | 30 seconds; already expired sessions still reject |
| Client discovery/token response | 64 KiB |
| Client HTTP timeout / refresh before expiry | 15 seconds / 30 seconds |

Network key loading is single-flight per authority. An unknown key identifier can request a refresh
within the same rate limit. Still-valid cached keys can bridge a temporary failure, but failures
never extend their absolute age; once that age is reached, validation fails until a successful load.
New successful key sets replace previous sets. Stream reads and key counts are bounded before
admission, and ordinary HTTP handlers must have redirects disabled. Custom handlers are trusted
integration code and are owned/disposed by the provider or validator.

The initial MessagePack hello has a fixed 64 KiB complete-frame ceiling that is enforced before its
body is allocated or a bearer token is validated. Authenticated sessions continue to use their
negotiated operational frame and payload limits.

## Expiration, reconnect and mutation outcomes

The host limits idle waiting to the token's remaining life and the storage-session admission lifetime,
checks expiration before starting each operation and closes expired sessions. Activity and ping do
not extend that lifetime. In-flight work is cancelled at the boundary and retains its lease until
the store observes cancellation or completes. Policy-partitioned stores use independent leases
for different resolved data scopes; a second session for the same scope receives `Busy`. There is no
in-session token replacement. Obtain a fresh
token and explicitly open a new session. Reusing the service token provider supplies a cached valid
token or obtains a fresh one as needed.

The preview.14 [connection-reliability changes](tcp-connection-reliability.md) let typed
pub/sub subscriptions reauthenticate immediately after a known token expiry. They do not
extend the old session's grant. The same guide distinguishes retryable token-endpoint
timeouts/network failures from TLS, credential and policy failures.

A mutation already admitted before expiration may complete durably. An expired token does not undo
that work. Lost acknowledgements, disconnection and post-dispatch cancellation retain the existing
`RemoteCommitOutcomeUnknownException` contract: reconcile durable state before retrying. The
transport never refreshes and replays a mutation automatically.

JWT signature verification is local validation, not online token introspection. An existing session
keeps its validated identity until expiration; key removal does not retroactively revalidate that
session. Authorization runs on every operation, allowing an application policy to deny later
requests. Immediate identity-provider revocation, introspection and a session revocation registry
are not implemented; choose access-token lifetimes to match the deployment's revocation needs.

## Verification

Run `dotnet test InterfaceDB.Authentication.Tests -c Release` and
`dotnet test InterfaceDB.Transport.Tests -c Release` for the ordinary regression suites.
The optional [Keycloak suite](../tests/InterfaceDB.Authentication.KeycloakTests/README.md)
uses isolated Docker resources and generated credentials. Browser/native login, online
revocation and application resource permissions remain application integration work.
