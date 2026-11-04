# InterfaceDB.Authentication

## Preview.18

Use matching `1.0.0-preview.18` versions across InterfaceDB packages. See the
[release and upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.18/documentation/preview18-release-notes.md).

## Preview.17: all modules under MIT

Atlas, repository composition, storage migration, conflict resolution and managed TCP
extensions are now MIT licensed too. Install only the modules you use, with matching versions.
See the [package map and upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview17-release-notes.md).

## Preview.16

Preview.16 fixes File read/delete races and adds validated file leases and provider-composition capabilities. Existing data formats, durability, conflict checks and normal application notifications remain intact. Update the public package set together and rebuild. See the [release and upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview16-release-notes.md).

## Preview.15

Preview.15 fixes encrypted-field updates and migrations, concrete reads, checkpoint publication, delete-policy enforcement and authentication admission. See the [upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview15-release-notes.md). Update all four public packages together. Adds server-owned remote revisions, explicit import authorization and context-bound field encryption. Isolation, committed reads and durability are preserved. Follow the staged field-encryption and coordinated TCP upgrade instructions before deployment.

## Inherited preview.12 storage baseline

File and SegmentCore reads return independent committed copies, including strong/weak caches. Unsaved edits stay private until a successful save. Correctness fixes cover indexed predicates, key identity, index failure visibility, transactions and compaction-safe scans. Serializer-aware context handling removes unused work without changing encryption or durability defaults.

Cached selective searches improve, while copied large results and scans cost more. See the [preview.12 upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview12-release-notes.md) and [measured trade-offs](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/performance-proposal-review-20261020.md). Update all InterfaceDB packages together and rebuild.

Optional MIT JWT access-token validation and OAuth service-token providers for .NET 10.
The embedded InterfaceDB core has no dependency on this package.

Authentication establishes identity; tenant/group data access is a separate responsibility in
`InterfaceDB.Transport`. Its preview.13 `IDataAccessPolicy` API uses validated claims such as
`tenant_id` and `groups`, optionally checking matching interface ownership fields before saves.
See [the source data-policy guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/data-access-policies.md). Use matching preview.15 transport; installing the authentication package alone does
not enable tenant isolation. Embedded local use can continue without either optional package.

- `JwtAccessTokenValidator`: exact configured issuer/audience, signature, type, lifetime and claims.
- `FixedAccessTokenProvider`: an access token supplied by the application.
- `ClientCredentialsAccessTokenProvider`: token acquisition for trusted services.

Native clients use their application's OAuth/OIDC login and token refresh. Service secrets
belong in trusted services. Authentication must be combined with application authorization.

Install `InterfaceDB.Authentication` version `1.0.0-preview.18`. Keep all InterfaceDB package versions aligned.
See [JWT and Keycloak configuration](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/jwt-authentication.md).

## MIT and voluntary support

All implemented InterfaceDB modules are now open source under MIT.
If InterfaceDB is useful to you, please consider [sponsoring maintenance](https://github.com/ToolMaker/InterfaceDB/blob/main/SPONSORING.md).
Sponsorship is optional and adds no license or feature conditions.
See the [preview.17 upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview17-release-notes.md).
