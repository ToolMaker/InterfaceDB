"""Validate frozen three-build reports and summarize process medians without dropping outliers."""
import csv
import json
import math
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8-sig"))
matrix = root / "matrix"
groups = defaultdict(lambda: defaultdict(list))
receipts = []
configurations = 0
reports = sorted(p for p in matrix.glob("*.json") if not p.name.endswith(".receipt.json"))
assert len(reports) == 18, f"Expected 18 reports, found {len(reports)}"
for path in reports:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    receipt = json.loads(path.with_suffix(".receipt.json").read_text(encoding="utf-8-sig"))
    assert receipt["exitCode"] == 0 and receipt["initialBootUtc"] == receipt["completedBootUtc"], path.name
    assert data["Passed"] and not data["Failures"], path.name
    assert data["SegmentDurability"] == "ExplicitFlush", path.name
    assert data["CacheModes"] == ["MetadataOnly", "StrongObject", "WeakObject"], path.name
    assert data["ReadProbes"], path.name
    for assembly, expected_hash in manifest["assemblies"][data["Label"]].items():
        assert data["Assemblies"][assembly] == expected_hash, (path.name, assembly)
    workload = "warm-10000" if data["RetainCacheObjects"] else "mixed-1000"
    assert data["Records"] == (10000 if workload == "warm-10000" else 1000), path.name
    assert data["Iterations"] == (400 if workload == "warm-10000" else 100), path.name
    assert data["QueriesOnly"] == (workload == "warm-10000"), path.name
    assert data["SkipFile"] == (workload == "warm-10000"), path.name
    assert data["MaterializationContract"] == "GateRecord/FNV1a32-v2/all-six-fields", path.name
    if data["SqlServerVersion"] is not None:
        assert data["SqlServerDelayedDurability"] == "DISABLED", path.name
    configurations += len({(metric["Engine"], metric["Cache"]) for metric in data["Metrics"]})
    receipts.append(receipt)
    for metric in data["Metrics"]:
        samples = sorted(metric["SamplesMs"])
        assert samples and all(math.isfinite(value) and value >= 0 for value in samples), path.name
        assert metric["P50Ms"] == samples[math.ceil(len(samples) * .50) - 1], path.name
        assert metric["P95Ms"] == samples[math.ceil(len(samples) * .95) - 1], path.name
        key = (workload, metric["Engine"], metric["Cache"], metric["Operation"])
        groups[key][data["Label"]].append(metric)
receipts.sort(key=lambda item: item["startedUtc"])
assert len({item["initialBootUtc"] for item in receipts}) == 1, "Mixed boot sessions"
for previous, current in zip(receipts, receipts[1:]):
    assert previous["completedUtc"] <= current["startedUtc"], "Overlapping benchmark processes"

rows = []
for key, variants in sorted(groups.items()):
    row = dict(zip(("workload", "engine", "cache_mode", "operation"), key))
    for label, metrics in variants.items():
        assert len(metrics) == 3, (key, label, len(metrics))
        row[label] = {
            "p50_ms": statistics.median(m["P50Ms"] for m in metrics),
            "p95_ms": statistics.median(m["P95Ms"] for m in metrics),
            "process_p50_min_ms": min(m["P50Ms"] for m in metrics),
            "process_p50_max_ms": max(m["P50Ms"] for m in metrics),
            "allocated_bytes": statistics.median(m["AllocatedBytesPerOperation"] for m in metrics),
            "result_counts": sorted({m["ResultCount"] for m in metrics}),
        }
    if all(label in row for label in ("baseline", "cache", "cache_a1")):
        row["cache_change_percent"] = 100 * (row["cache"]["p50_ms"] / row["baseline"]["p50_ms"] - 1)
        row["a1_change_percent"] = 100 * (row["cache_a1"]["p50_ms"] / row["cache"]["p50_ms"] - 1)
        row["combined_change_percent"] = 100 * (row["cache_a1"]["p50_ms"] / row["baseline"]["p50_ms"] - 1)
    rows.append(row)
summary = {"processes": len(reports), "metric_rows": sum(len(v) for variants in groups.values() for v in variants.values()),
           "configurations": configurations,
           "boot_utc": receipts[0]["initialBootUtc"], "started_utc": receipts[0]["startedUtc"],
           "completed_utc": receipts[-1]["completedUtc"], "rows": rows}
(root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
with (root / "summary.csv").open("w", newline="", encoding="utf-8") as stream:
    fields = ["workload", "engine", "cache_mode", "operation", "variant", "p50_ms", "p95_ms",
              "process_p50_min_ms", "process_p50_max_ms", "allocated_bytes", "result_counts"]
    writer = csv.DictWriter(stream, fieldnames=fields)
    writer.writeheader()
    for row in rows:
        identity = {field: row[field] for field in fields[:4]}
        for label in ("baseline", "cache", "cache_a1"):
            if label in row:
                writer.writerow({**identity, "variant": label, **row[label]})
print(f"Verified {summary['processes']} sequential processes, {summary['metric_rows']} metrics; all checks passed.")
for row in rows:
    if row["workload"] == "warm-10000" and row["engine"] == "InterfaceDB SegmentCore" and row["operation"] in (
            "read-known-key", "query-exact", "query-range", "query-broadconjunction", "query-prefix", "query-suffix"):
        values = " -> ".join(f"{row[label]['p50_ms']:.4f}" for label in ("baseline", "cache", "cache_a1"))
        print(row["cache_mode"], row["operation"], values, "ms")
