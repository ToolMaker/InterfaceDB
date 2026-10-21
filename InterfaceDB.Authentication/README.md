# InterfaceDB.Authentication

## Preview.12 baseline

File and SegmentCore reads return independent committed copies, including strong/weak caches. Unsaved edits stay private until a successful save. Correctness fixes cover indexed predicates, key identity, index failure visibility, transactions and compaction-safe scans. Serializer-aware context handling removes unused work without changing encryption or durability defaults.

Cached selective searches improve, while copied large results and scans cost more. See the [preview.12 upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.12/documentation/preview12-release-notes.md) and [measured trade-offs](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.12/documentation/performance-proposal-review-20261020.md). Update all InterfaceDB packages together and rebuild.

Optional MIT JWT access-token validation and OAuth service-token providers for .NET 10.
The embedded InterfaceDB core has no dependency on this package.

- `JwtAccessTokenValidator`: exact configured issuer/audience, signature, type, lifetime and claims.
- `FixedAccessTokenProvider`: an access token supplied by the application.
- `ClientCredentialsAccessTokenProvider`: token acquisition for trusted services.

Native clients use their application's OAuth/OIDC login and token refresh. Service secrets
belong in trusted services. Authentication must be combined with application authorization.

Install `InterfaceDB.Authentication` version `1.0.0-preview.12`. Keep all InterfaceDB package versions aligned.
See [JWT and Keycloak configuration](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.12/documentation/jwt-authentication.md).
