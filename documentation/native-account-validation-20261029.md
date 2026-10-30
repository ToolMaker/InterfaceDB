# Native account provider validation — 29 October 2026

This is an unreleased source feature based on the published preview.13 code. No package version,
release tag, deployment or existing account database was changed by this work.
See the [configuration and integration guide](native-account-authentication.md).

## Scope and invariants

Native username/password authentication is opt-in through `IAuthenticationProvider`. Account
storage, password hashing, creation authorization and client credentials have separate injected
contracts. Existing JWT validation is adapted behind the authentication boundary; certificate
modes, data-policy rules and embedded local repositories retain their existing behavior.

The new credential adapter wraps durable opaque storage and authenticated encryption. It does
not change the application envelope format, serializer, revision checks, commit durability,
query implementation, committed-copy caching or migration semantics. No authentication cost
is added to embedded repositories that do not use this provider.

Multiple-account mode requires an explicit partitioning policy and denies unrestricted policy
results. Account IDs are server-generated and stable across password changes. A registration
request cannot set roles, membership claims, identity or a data partition. Bootstrap remains a
trusted local operation before listening. Registration never bypasses login for storage access.

## Windows results

Release validation included the complete public solution, followed by full authentication and
transport reruns after the final native-account checks were added:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core | 393 | 10 |
| SegmentCore | 216 | 2 |
| Analyzers | 31 | 0 |
| Authentication, final source | 74 | 0 |
| TCP transport and host process, final source | 265 | 0 |
| Total | 979 | 12 |

The existing platform-conditional skips were retained. The executable integration harness also
passed all 50 checks. Public source-boundary validation passed for all 29 projects. Existing
MSTEST0032 warnings in SegmentCore compatibility tests are unrelated to this change.

There are 36 new account-related cases across authentication, native TCP, and host-process tests.
They cover:

- Single/multiple accounts, duplicate/case-normalized names, atomic account limits and one-time bootstrap.
- Administrator/allowlisted-identity creation, anonymous registration when enabled, disabled registration,
  and rejection of client-supplied privilege fields.
- Salted hashes, wrong passwords, bounded password work, expiration, disabling accounts, and password
  changes preserving identity while invalidating old sessions.
- Per-account keys, get-many, batches, enumeration, deletion, group membership, cache identity binding
  and independent committed entity copies.
- TLS rejection before password retrieval, no downgrade to JWT, policy-denial behavior and failure of
  credential revalidation on already-open connections.
- Scoped pub/sub and denial of notifications after revocation.
- Encrypted storage/reopen, wrong keys, wrong authority, ciphertext tampering, missing encryption,
  no-op/context-insensitive encryptors, and missing secret configuration before listening.
- Capacity rejection before writing, continued use after that rejection, and conservative unknown-outcome
  handling after a credential write or a lost registration acknowledgement.
- Restart persistence without resetting an existing password from bootstrap configuration.

During test development, the notification-revocation test initially observed the expected disconnect
twice: once through `Completion` and again during asynchronous disposal. Its cleanup now observes
the expected failure once. No notification delivery or revocation behavior was weakened.

## Linux results

The final authentication suite passed **74/74** and final transport/host suite passed **265/265**
in the .NET SDK 10.0.401 Linux container, running as UID/GID 1000 with networking disabled
(loopback tests still operate). Source/output mounts were read-only; temporary databases used
the container filesystem. The suites ran the compiled portable .NET assemblies; this was Linux
runtime validation, not a second source build. No authentication/transport cases were skipped.

The earlier Linux pass caught the same test-cleanup issue described above. The final run used
the corrected fixture and includes the additional encryption and capacity cases. The result
file is `.tmp/native-auth-validation/linux/linux-native-auth-final.trx`.

## Reproduction and limits

```powershell
dotnet test InterfaceDB.sln -c Release
dotnet run --project InterfaceDb.IntegrationTests -c Release
./Scripts/Test-PublicSourceBoundary.ps1
```

Raw local results are under `.tmp/native-auth-validation/` (ignored, not a package input).
The full-solution command initially used one TRX filename, so that file contains the last suite;
the final authentication and transport runs have separate result files. The table above combines
the complete solution's unchanged core/engine/analyzer results with those final feature-suite runs.

This is correctness validation, not a login-throughput benchmark. Password checks are intentionally
expensive and run per connection; the guide documents the typed repository's connection behavior
and configurable global admission budget. No speed-up is claimed. Device/power-loss qualification,
live Keycloak end-to-end testing and broader native-account management features are not added by
this change. Existing command-line client samples retain JWT settings; native clients use the API
shown in the integration guide.
