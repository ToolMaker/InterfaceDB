# Preview.11 release validation — 12 October 2026

Status: release approved; local release gates passed. GitHub release and public-feed verification pending.
All four source package projects are now version `1.0.0-preview.11`.

## Approved release candidate

- Public Release build passed with analyzers enabled: zero errors and eight existing MSTEST0032 warnings.
- Public tests: **751 passed, four existing platform skips, zero failed**.
- Standalone integration harness: **50 passed**.
- Materialization self-test: all three providers passed.
- Complete-repository smoke: both storage providers and all three cache modes passed.
- Four freshly rebuilt MIT packages passed assembly/dependency/README boundaries.
- A new package-only consumer with an isolated NuGet cache passed JWT/TLS save/query/update,
  pub/sub and server reopen, plus File and SegmentCore save/query/notification-readiness/reopen.
  Its first embedded reopen check omitted the required initialization call; correcting the harness
  made both embedded providers pass. No library change was needed.
- NuGet vulnerability audit reported no vulnerable packages in the configured public solution sources.
- Public source boundary: all 27 projects stay within this repository.

The preparation runs below are retained as historical evidence; their earlier local candidate
packages predate the final release rebuild.

## Public product boundary

`InterfaceDB.Transport` accesses one remote database with TLS/JWT and disposable pub/sub.
The client has no persistent local database, offline fallback or queued writes. MessagePack
remains the default public serializer. `InterfaceDB.Authentication` is an optional separate
package; the embedded core has no JWT dependency. No gRPC or Protobuf implementation is included.

## Public TCP move baseline

| Check | Result |
| --- | --- |
| Public Release solution build | Passed |
| Core repository tests | 324 passed, 2 platform skips |
| SegmentCore tests | 198 passed, 2 platform skips |
| Analyzer tests | 5 passed |
| JWT/token-provider tests | 61 passed |
| TCP tests | 152 passed |
| Public source boundary | 27 projects checked; all project references stay within this repository |
| Package boundary | Four MIT packages, one expected assembly each and approved public dependencies |
| Clean package consumer | Passed TLS/JWT save, query, update, pub/sub and durable server reopen |
| Optional Docker/Keycloak projects | Compiled; live container suites not rerun in this move |

Public total: **740 passed, 4 skipped**. The four existing Windows skips require case-sensitive
paths or directory-symlink capabilities unavailable in this environment. An initial new online-only
test surfaced the expected disconnected-subscription exception again during async cleanup; its
cleanup was corrected and the complete TCP suite then passed. No production behavior was weakened.

The online-only regression verifies that a disconnected client cannot read cached state or queue
a write, creates no client database directory, and can explicitly register a new subscription after
the host returns. Existing tests cover revisions, isolation, limits, authentication, serializer
selection and trusted server migrations.

The consumer used a fresh NuGet package directory, only a package reference to `InterfaceDB.Transport`,
locally generated credentials, loopback TLS and its own server files. It loaded no private assembly.
CI/release workflows now build and test the new packages and enforce the public dependency boundary.
This Windows run does not claim new Linux, live Keycloak, cloud, device or physical power-loss evidence.

## Serialization-pass follow-up

The TCP metadata acknowledgement path previously deserialized an envelope twice in the same
operation. Three new call-count cases reproduced that extra decode with the default MessagePack,
custom test and encrypted custom serializers. They passed after switching to the already
materialized current record. A separate core regression verifies that preparing an acknowledgement
leaves the borrowed committed envelope unchanged if the prepared write is abandoned.

Ten TCP call-count cases now cover CRUD, indexed reads, stale writes, explicit conflict refresh,
batches, deltas, metadata preconditions and pub/sub. Writes encode each changed envelope once;
reads decode each received snapshot record once per operation. The test also compares stored
unencrypted payload bytes with the serializer output. Server contract validation can independently
decode incoming bytes without re-encoding them; see [serialization passes](serializer-configuration.md#tcp-serialization-passes).

The complete public solution was rebuilt and tested in Release after this follow-up:

| Suite | Result |
| --- | --- |
| Core | 325 passed, 2 existing platform skips |
| SegmentCore | 198 passed, 2 existing platform skips |
| Analyzers | 5 passed |
| Authentication | 61 passed |
| TCP | 162 passed |
| Total | **751 passed, 4 skipped, 0 failed** |

The package and clean-consumer checks above predate this follow-up. Candidate packages must be
rebuilt before publication; this follow-up has not published or updated any NuGet package.

See [release preparation](releasing.md), [online integration](tcp-repository.md),
[hosting](tcp-transport.md) and [authentication](jwt-authentication.md).
