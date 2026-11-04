# Preview.16 release validation — 4 November 2026

Status: prepared for hosted verification; not yet published.

The local pre-release working tree passed core (424), SegmentCore (222), analyzers
(31), transport (287) and authentication (84), with 12 platform/privilege skips.
Four additional separate-process lease checks passed on Windows. They verify ordinary
writes and migration admission with the runtime locking switch both enabled and disabled;
the Unix-only disable switch must reject unsafe operations on Linux.

Hosted Windows/Linux CI must pass on the release commit before the publication tag
is pushed. The tag workflow repeats Linux correctness, integration and package-boundary
checks before obtaining a temporary NuGet publishing credential.

Local WSL cannot start (`ERROR_NO_SYSTEM_RESOURCES`); this is not counted as a Linux
pass. Device, power-loss and external identity-provider qualification remain separate.
Final commit, workflow links and published-package consumer receipts will be recorded here.
