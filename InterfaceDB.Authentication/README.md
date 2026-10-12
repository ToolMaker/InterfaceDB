# InterfaceDB.Authentication

Optional MIT JWT access-token validation and OAuth service-token providers for .NET 10.
The embedded InterfaceDB core has no dependency on this package.

- `JwtAccessTokenValidator`: exact configured issuer/audience, signature, type, lifetime and claims.
- `FixedAccessTokenProvider`: an access token supplied by the application.
- `ClientCredentialsAccessTokenProvider`: token acquisition for trusted services.

Native clients use their application's OAuth/OIDC login and token refresh. Service secrets
belong in trusted services. Authentication must be combined with application authorization.

Install `InterfaceDB.Authentication` version `1.0.0-preview.11`. Keep all InterfaceDB package versions aligned.
See [JWT and Keycloak configuration](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.11/documentation/jwt-authentication.md).
