# InterfaceDB 1.0.0-preview.14

Preview.14 adds connection recovery improvements and optional native database accounts.
Update `InterfaceDB`, `InterfaceDB.Storage.SegmentCore`, `InterfaceDB.Authentication` and
`InterfaceDB.Transport` together, then rebuild the client and host. Publication and verification
status is recorded in [release validation](preview14-validation.md).

## Connection recovery

- Token acquisition distinguishes actual timeout/network outages from TLS, credential,
  discovery and response failures. Only proven temporary failures permit availability retry.
- Typed subscriptions reauthenticate promptly when the server closes at known token expiry,
  keeping the disposable registration and issuing a catch-up hint after re-registration.
  The token provider must supply a usable token. Expired fixed tokens, rejected credentials,
  account changes and scope changes still stop access.
- Busy admission uses bounded exponential backoff with jitter through `ITcpAdmissionRetryPolicy`.
  Cancellation and the total admission deadline remain enforced. No dispatched mutation is retried.
- Transport logs retain bounded failure reasons and local connection IDs without credentials,
  record values or raw exception text. IDs never become metric labels.

See [connection behavior and integration](tcp-connection-reliability.md). Bridges must use
their own liveness mechanism; upgrading InterfaceDB alone does not remove an intermediary's
application-idle timeout. No heartbeat bytes, pooling or snapshot optimization were added to
the InterfaceDB wire protocol.

## Optional native username/password provider

`IAuthenticationProvider` now supports JWT through an adapter or a native database account
provider. Native accounts are explicitly enabled with `Provider` transport mode and matching
host/client configuration. They support single/multiple accounts, encrypted durable credential
storage, salted password hashing, explicit creation policies, stable account identity,
revocation and partitioned data access. Raw passwords are not persisted.

Use the [native account guide](native-account-authentication.md) and the sample host's password
configuration. Existing JWT and certificate modes keep their configuration and behavior.
Embedded repositories remain usable without authentication. Switching an existing deployment
to native accounts requires an explicit account/data migration plan; it is never automatic.

## Compatibility and preserved behavior

The existing JWT/certificate protocol remains wire version 3. Existing preview.13 peers do not
gain the new client behavior automatically, and native Provider mode requires updated peers.
Upgrade from preview.12 or earlier using the [preview.13 migration guide](preview13-release-notes.md#breaking-upgrade-from-preview12).

File/envelope formats, durable acknowledgements, revision/conflict protection, committed-copy
cache semantics, data-policy isolation and migration ownership are unchanged. A temporary
identity-provider outage does not bypass account-bound cache access checks. Authentication
defaults and connection/subscription budgets are unchanged. No throughput gain is claimed.

Real-device connection testing follows deployment. This preview does not claim new Android,
Apple-device or physical power-loss qualification.
