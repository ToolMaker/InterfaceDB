# TCP connection reliability

Included in preview.14, 30 October 2026; see [publication/validation status](preview14-validation.md).
These changes are not included in preview.13 packages.

## Temporary failures and authentication failures

`ClientCredentialsAccessTokenProvider` classifies failures at the HTTP boundary before
removing exception details. `TokenAcquisitionFailure.Network` identifies proven DNS,
connection and socket failures. `Tls` identifies secure-connection failures. Existing enum
values retain their numbers. Original exceptions, response bodies and credentials stay redacted.

`TcpRepositoryAvailability.IsUnavailable` accepts `Network` and actual acquisition `Timeout`
as temporary failures. Composed repositories use their existing permitted offline behavior
and client-owned retry. Caller cancellation remains cancellation; disposal and unrelated
cancellation are not timeouts. Secret-source errors remain `Credentials`, including socket
errors inside a custom secret source. Generic `Unavailable`, TLS, bad metadata/responses and
rejected credentials remain fail-closed. Custom providers must supply meaningful categories;
arbitrary exceptions are not broadly reclassified as network failures.

The earlier TLS clean-close fix remains narrow: a peer closing before sending any TLS bytes
is an availability failure. Certificate rejection, TLS alerts and malformed/partial handshakes
are not silently changed into retryable outages.

## Subscription renewal

After the host accepts authentication, the client retains an expiry scheduling hint from
`AccessToken.ExpiresAt` and/or JWT `exp`. This hint grants no authorization. When an idle
subscription closes at token expiry, `TcpRepository.SubscribeChanges` opens a fresh authenticated
subscription immediately, binds the same account and approved data scope, then publishes the
usual catch-up hint. The disposable registration remains active through successful renewal.

The provider must return a usable token. A fixed expired token, account/scope change or denial
still ends access. A close within one second before expiry waits until expiry to accommodate
timer precision without repeatedly reusing a cached token. Other disconnects use the existing
availability path. Cancellation stops renewal. An opaque token without an expiry hint cannot
use this fast path; policies closing sessions earlier retain their existing classification.

No mutation is replayed and no data polling is introduced. Existing cursor reconciliation
catches up across re-registration gaps. No new wire message is sent; existing peers retain
their protocol. Internal subscription consumers can distinguish `AuthenticationExpired`
and request authenticated rediscovery.

## Busy admission

`TcpRepositoryOptions<T>.AdmissionRetryPolicy` accepts a borrowed `ITcpAdmissionRetryPolicy`.
The default is exponential backoff with equal jitter, capped at one second (or the initial
delay when larger). `BusyRetryDelay` supplies the initial upper bound; `AdmissionTimeout`
and caller cancellation bound the whole attempt. Supply another implementation through DI
at the composition root when required.

Only rejected admission is retried, before dispatching a storage request. Unknown commit
outcomes, conflicts and rejected writes retain their handling. Connection/subscription
budgets, scope leases and durability defaults are unchanged. Pooling stays a separate
[measured roadmap proposal](roadmaps.md#proposed-tcp-connection-reuse-and-optional-pooling).

## Diagnostics and bridges

Telemetry retains the broad error category plus a closed `DatabaseFailureReason`, including
`Busy`, `AuthenticationExpired`, `LimitExceeded`, `TokenTimeout`, `TokenNetwork` and `TokenTls`.
Structured logs carry `ReasonCode` and local `ConnectionId`; traces use `error.reason` and
`connection.id`. Reasons are bounded metric labels; connection IDs never become metric labels.
Credentials, record values and raw exception text are excluded. Renewal adds no information log.

Client and host connection IDs are local, not a wire correlation or an identity. Intermediaries
should retain their own safe correlation ID and admission/status reason at both bridge endpoints.
A quiet subscription is healthy: distinguish application-data silence from a dead peer.
For .NET WebSocket bridges, configure a finite ping/pong deadline and keep-alive interval with
a continuously pending receive. Keep bounded admission and absolute lifetimes. Do not insert
heartbeat bytes into InterfaceDB frames without negotiation. See Microsoft's
[WebSocket options](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions?view=net-10.0).

Connection reuse and snapshot/query optimizations are not part of this change. No throughput
gain is claimed. Future comparisons must preserve authorization, scope isolation, snapshot
consistency, revision checks and durable acknowledgements.

## Validation on 30 October 2026

Windows, .NET SDK 10.0.401 / runtime 10.0.12, Release:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core | 393 | 10 |
| Authentication | 80 | 0 |
| Public transport | 276 | 0 |

The skipped core cases require Unix permissions or a case-sensitive volume. Tests cover
real TLS/JWT subscriptions, renewed/rejected/expired credentials, account-switch rejection,
stale expiry hints, cancellation during Busy admission, redaction, bounded reason tags and
actual host Busy logging. Existing isolation, conflict and uncertain-commit tests also pass.
This run did not validate Linux or an Android device; Docker's Linux engine was unavailable.
Passing local tests is separate from deploying these sources or publishing a package.
