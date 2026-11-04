# Preview.17 validation and publication

Status: **in progress; not yet published**.

The candidate integrates the active formerly commercial implementations into the public
solution and makes nine coordinated packages MIT licensed. No private source checkout
or private feed is required to build them.

The local .NET 10 Release build, integration harness, object-materialization checks and
all nine package content checks pass. NuGet's transitive dependency audit reports no
known vulnerabilities against the configured feed. Isolated package consumers and hosted
Windows/Linux checks are underway; publication remains gated on their final results.

Initial validation found two timing-sensitive integration-test outcomes; they are retained
here rather than discarded:

- Windows: managed File/LocalFirst notification feedback timed out once. All seven focused
  cases subsequently passed, followed by a complete 321/321 transport-composition rerun
  without runtime changes. The cause is not established; a passing rerun is not proof of a fix.
- Linux: the group-approval test encountered the supported `Busy` response when two
  startup paths briefly contended for one subject's setup lease. The test now retries only
  `Busy` within its existing deadline. Authorization failures still propagate; runtime
  admission, locking, retry policy and isolation are unchanged.

Earlier successful suites are not substituted for the final release/consumer checks.

The version-label migration correction has dedicated tests. Earlier private-build
evidence is not substituted for tests of this release. Live Keycloak/Docker acceptance,
real Android/iOS devices and physical power-cut testing have not been run here.
