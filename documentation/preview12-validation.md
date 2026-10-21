# Preview.12 baseline validation — 21 October 2026

Status: **preparation in progress; not published**.

## Frozen runtime

Public commit `4e63ddb786d7d89c4151e62535e623090b33cba3` is the committed
correctness/A1 baseline. Package version and release documentation changes do
not include subsequent performance prototypes.

The preceding verification passed 791 public tests with 12 platform skips:
core 391, SegmentCore 216 and public transport 184. Its 18-process Windows
comparison passed 117 engine/cache configurations and 1,413 measurement rows.
The source fingerprints still matched the tested runtime when it was committed.

## Candidate gates

Fresh versioned Release build, release suites, integration/smoke gates, package
boundaries, dependency audit and isolated package-consumer checks are pending.
Candidate packages are not evidence of NuGet.org publication.

The NuGet feed checked before preparation advertised preview.11 for all four
public packages and no preview.12. No release tag has been pushed.

The [release procedure](releasing.md) describes publication and subsequent
public-feed verification. Real-device and physical power-loss qualification
are outside these local checks.
