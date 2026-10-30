# InterfaceDB.Authentication

## Preview.14 release

Connection recovery, safe transport diagnostics and optional native accounts are documented in the [upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview14-release-notes.md). Update all four public packages together. Existing JWT settings, isolation, committed reads and durability remain unchanged.

## Inherited preview.12 storage baseline

File and SegmentCore reads return independent committed copies, including strong/weak caches. Unsaved edits stay private until a successful save. Correctness fixes cover indexed predicates, key identity, index failure visibility, transactions and compaction-safe scans. Serializer-aware context handling removes unused work without changing encryption or durability defaults.

Cached selective searches improve, while copied large results and scans cost more. See the [preview.12 upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.13/documentation/preview12-release-notes.md) and [measured trade-offs](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.13/documentation/performance-proposal-review-20261020.md). Update all InterfaceDB packages together and rebuild.

Optional MIT JWT access-token validation and OAuth service-token providers for .NET 10.
The embedded InterfaceDB core has no dependency on this package.

Authentication establishes identity; tenant/group data access is a separate responsibility in
`InterfaceDB.Transport`. Its preview.13 `IDataAccessPolicy` API uses validated claims such as
`tenant_id` and `groups`, optionally checking matching interface ownership fields before saves.
See [the source data-policy guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.13/documentation/data-access-policies.md). The API requires preview.13 transport; installing the authentication package alone does
not enable tenant isolation. Embedded local use can continue without either optional package.

- `JwtAccessTokenValidator`: exact configured issuer/audience, signature, type, lifetime and claims.
- `FixedAccessTokenProvider`: an access token supplied by the application.
- `ClientCredentialsAccessTokenProvider`: token acquisition for trusted services.

Native clients use their application's OAuth/OIDC login and token refresh. Service secrets
belong in trusted services. Authentication must be combined with application authorization.

Install `InterfaceDB.Authentication` version `1.0.0-preview.13`. Keep all InterfaceDB package versions aligned.
See [JWT and Keycloak configuration](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.13/documentation/jwt-authentication.md).
