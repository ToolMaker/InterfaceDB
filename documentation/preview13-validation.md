# Preview.13 release validation — 28 October 2026

Status: candidate preparation in progress. NuGet publication is not yet confirmed.

The feature snapshot passed 874 public test cases with 12 platform-specific skips on Windows.
The 24 production benchmark rounds passed exact payload, partition, ordering and reopen checks.
See [feature validation](access-policy-production-20261028.md). Those results do not replace the
release workflow checks below.

The release will verify package versions, source and package boundaries, full Windows/Linux CI,
analyzers, repository/integration smoke checks, and isolated package consumers. The tag workflow
uses NuGet trusted publishing with a temporary credential. Public packages must contain only
their intended MIT assemblies and allowed dependencies. Commercial verification remains private.

Release commit, workflow outcomes, package hashes and public-feed consumption will be recorded
here after verification. Live Keycloak, real mobile devices and physical power-loss qualification
are not claimed by these package checks.

## Local candidate checks

- Release solution build passed, including analyzer compatibility enforcement.
- Core 393, analyzers 31, SegmentCore 216, TCP 204 and authentication 61 passed:
  **905 total**, with 12 platform-specific skips on this Windows machine.
- The integration harness and benchmark materialization contract passed.
- All four candidate packages passed MIT, assembly and dependency boundary inspection.
- An isolated candidate-package consumer with no project references passed **199 transport,
  authentication and policy tests**. Five host-process tests remain in the ordinary source suite.
- The initial dependency audit found two PKCS advisories only in SQL benchmark dependencies;
  those projects now pin patched 9.0.21. Public runtime packages do not include that dependency.

Local evidence is under `.tmp/preview13/`, `.tmp/preview13-*.log`, and
`artifacts/preview13/packages/`. Candidate package metadata predates the release commit;
the tag workflow rebuilds the published artifacts from its immutable commit.

The final local dependency audit reports no known vulnerable dependencies across the solution.
The six-provider/cache smoke combinations and three SegmentCore review smoke cases also passed.
