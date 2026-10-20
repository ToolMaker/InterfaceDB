"""Validate repeated safety-fix/flush-mode runs and retain paired timings.

Usage: python Scripts/summarize_repository_followup.py EVIDENCE_DIRECTORY
"""
import json
import statistics
import sys
from datetime import datetime
from pathlib import Path

root = Path(sys.argv[1])
rows = []
reference = None
current_assemblies = None
timelines = []
for directory, labels, records, engines in [
    ("safety-performance", ("before", "current"), 1000, {"InterfaceDB", "InterfaceDB SegmentCore"}),
    ("flush-modes", ("ExplicitFlush", "WriteThrough"), 10000,
     {"InterfaceDB SegmentCore", "SQLite Microsoft.Data.Sqlite", "SQLite Dapper", "SQL Server"}),
]:
    reports = {}
    for label in labels:
        for round_number in range(1, 4):
            path = root / directory / f"{label}-r{round_number}.json"
            report = json.loads(path.read_text(encoding="utf-8-sig"))
            assert report["Passed"] and not report["Failures"], path
            assert report["Records"] == records and report["Iterations"] == 50, path
            assert report["CacheModes"] == ["MetadataOnly"], path
            assert not report["QueriesOnly"] and not report["CompareOrdinalStrings"], path
            assert report["MutationLimit"] == (256 if directory == "safety-performance" else 1000), path
            assert {m["Engine"] for m in report["Metrics"]} == engines, path
            assert all(m["Cache"] == "MetadataOnly" or m["Engine"] not in {"InterfaceDB", "InterfaceDB SegmentCore"}
                       for m in report["Metrics"]), path
            assert len(report["QueryKinds"]) == 6, path
            assert report["OnlyInterfaceDb"] == (directory == "safety-performance"), path
            assert report["SkipFile"] == (directory == "flush-modes"), path
            assert report["SegmentDurability"] == (label if directory == "flush-modes" else "ExplicitFlush"), path
            assert report["Reverse"] == (round_number == 2), path
            if directory == "flush-modes":
                assert report["SqlServerDelayedDurability"] == "DISABLED", path
                assert report["SqlServerVersion"], path
                assert len(report["QueryPlans"]) == 2, path
            invariant = {key: report[key] for key in ("Runtime", "OS", "MaterializationContract", "SqliteVersion", "SqlIndexProfile")}
            if reference is None:
                reference = invariant
            assert reference == invariant, path
            if label != "before":
                if current_assemblies is None:
                    current_assemblies = report["Assemblies"]
                assert current_assemblies == report["Assemblies"], path
            reports[label, round_number] = report
            timelines.append((datetime.fromisoformat(report["StartedUtc"]),
                              datetime.fromisoformat(report["CompletedUtc"]), str(path)))
    metric_maps = {key: {(m["Engine"], m["Operation"]): m for m in value["Metrics"]}
                   for key, value in reports.items()}
    cases = metric_maps[labels[0], 1].keys()
    assert all(m.keys() == cases for m in metric_maps.values()), directory
    for engine, operation in sorted(cases):
        measurements = [[metric_maps[label, n][engine, operation] for n in range(1, 4)] for label in labels]
        assert len({m["ResultCount"] for ms in measurements for m in ms}) == 1, (engine, operation)
        p50s = [[m["P50Ms"] for m in ms] for ms in measurements]
        medians = [statistics.median(values) for values in p50s]
        pairs = [100 * (b / a - 1) if a else None for a, b in zip(*p50s)]
        delta = 100 * (medians[1] / medians[0] - 1) if medians[0] else None
        direction = ("mixed" if any(p is None for p in pairs)
                     else "lower in every pair" if all(p < 0 for p in pairs)
                     else "higher in every pair" if all(p > 0 for p in pairs) else "mixed")
        rows.append({
            "Experiment": directory, "Engine": engine, "Operation": operation, "Labels": labels,
            "P50sMs": p50s, "MedianP50Ms": medians, "ChangePercent": delta,
            "PairedChangePercent": pairs, "PairedDirection": direction,
            "MedianP95Ms": [statistics.median(m["P95Ms"] for m in ms) for ms in measurements],
            "MedianAllocatedBytes": [statistics.median(m["AllocatedBytesPerOperation"] for m in ms) for ms in measurements],
        })

ordered = sorted(timelines)
for started, completed, name in ordered:
    assert started < completed, name
for previous, following in zip(ordered, ordered[1:]):
    assert previous[1] <= following[0], (previous[2], following[2])

for path in (root / "safety-performance").glob("before-r*.json"):
    before = json.loads(path.read_text(encoding="utf-8-sig"))["Assemblies"]
    assert {name for name in before if before[name] != current_assemblies[name]} == {
        "Idb.Libraries", "InterfaceDB.Storage.SegmentCore"
    }, path

summary = {
    "Method": "Median of three process p50s; paired directions retained. Descriptive timings, not significance tests.",
    "Environment": reference, "CurrentAssemblies": current_assemblies,
    "ValidatedProcesses": len(timelines), "Rows": rows,
}
(root / "measurement-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(f"Validated {len(timelines)} processes and {len(rows)} cases.")
for row in rows:
    if row["Operation"] in {"seed-object-per-operation", "update-indexed-fields", "delete-by-id", "query-range"}:
        a, b = row["MedianP50Ms"]
        print(f'{row["Experiment"]}: {row["Engine"]} {row["Operation"]}: {a:.4f} -> {b:.4f} ms; {row["ChangePercent"]:+.1f}%; {row["PairedDirection"]}')
