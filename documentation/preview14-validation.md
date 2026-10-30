# Preview.14 release validation — 30 October 2026

Status: release candidate; publication has not yet been verified. All four public packages
are being prepared as `1.0.0-preview.14`. This record will be updated after the immutable
release checks and an isolated restore from the public NuGet feed.

The source snapshot passed 276 public transport tests, 80 authentication tests and 393 core
tests with 10 Windows platform-specific skips. The earlier native-account work also passed
Windows and Linux runtime tests; those historical results are documented separately in
[native account validation](native-account-validation-20261029.md).

The local release build passed with no errors or analyzer compatibility failures. A full
recompile retains eight existing MSTEST0032 warnings in SegmentCore compatibility tests. The analyzer suite passed 31 tests,
SegmentCore passed 216 with 2 Windows skips, the integration harness passed all 50 cases,
and the three object-materialization checks passed. Source and all four candidate-package
boundaries passed.

The release process also verifies the complete solution, analyzers, SegmentCore, integration and
repository smoke checks, public-source/package boundaries, Windows/Linux CI and the tag
publication workflow. Packages contain only the four MIT public assemblies and their allowed
dependencies. Local context, private implementations, credentials and build experiments are
excluded from source commits and packages.

No real phone, live Keycloak or physical power-cut result is claimed by these release checks.
See [release notes](preview14-release-notes.md) and [connection behavior](tcp-connection-reliability.md).
