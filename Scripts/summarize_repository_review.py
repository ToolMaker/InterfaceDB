"""Summarize Measure-RepositoryReview.ps1 output; no timing acceptance thresholds."""
import argparse
import hashlib
import json
import statistics
from collections import defaultdict
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", type=Path)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
groups = defaultdict(list)
reports = []
fingerprints = {}
harness_hash = None
case_options = {}
for path in sorted(args.directory.glob("*-round*.json")):
    report = json.loads(path.read_text(encoding="utf-8"))
    if not report["TimingsRecorded"]:
        raise ValueError(f"Smoke report cannot supply timings: {path}")
    build, _, round_id = report["Label"].split("-")
    if build not in ("baseline", "current"):
        raise ValueError(f"Unknown build label: {path}")
    signatures = {a["Name"]: a["Sha256"] for a in report["Assemblies"]}
    if build in fingerprints and fingerprints[build] != signatures:
        raise ValueError(f"Assemblies changed within {build} runs.")
    fingerprints[build] = signatures
    current_harness = signatures["StorageBenchmarks.Repository"]
    if harness_hash is not None and current_harness != harness_hash:
        raise ValueError("Baseline/current must use the same harness.")
    harness_hash = current_harness
    options = {k: v for k, v in report["Options"].items() if k not in ("Label", "Output", "DataRoot")}
    options["UpdateLimit"] = report["UpdateLimit"]
    count = options["Records"]
    if count in case_options and case_options[count] != options:
        raise ValueError(f"Mismatched options at {count} records.")
    case_options[count] = options
    reports.append({
        "file": path.name,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "assemblies": report["Assemblies"],
        "startedUtc": report["StartedUtc"],
        "finishedUtc": report["FinishedUtc"],
    })
    for summary in report["Summary"]:
        key = (report["Options"]["Records"], summary["Engine"], summary["CacheMode"], summary["Operation"])
        groups[key].append((build, round_id, summary))

def median(values):
    return statistics.median(values)

def describe(rows):
    return {
        "processRuns": len(rows),
        "operations": sum(r["Samples"] for r in rows),
        "medianRunMeanMs": median(r["MeanMilliseconds"] for r in rows),
        "medianRunP50Ms": median(r["P50Milliseconds"] for r in rows),
        "medianRunP95Ms": median(r["P95Milliseconds"] for r in rows),
        "medianAllocatedBytesPerOperation": median(r["MeanAllocatedBytes"] for r in rows),
        "revisionReads": sum(r["RevisionReads"] for r in rows),
        "deserializations": sum(r["Deserializations"] for r in rows),
    }

comparisons = []
for (records, engine, cache, operation), entries in sorted(groups.items()):
    by_build = {}
    for build in ("baseline", "current"):
        rows = [s for b, _, s in entries if b == build]
        rounds = {r for b, r, _ in entries if b == build}
        if len(rows) < 3 or len(rows) != len(rounds):
            raise ValueError(f"Need three process runs: {records}/{cache}/{operation}/{build}")
        by_build[build] = describe(rows)
    paired = []
    for round_id in sorted({r for _, r, _ in entries}):
        pair = {b: s for b, r, s in entries if r == round_id}
        if set(pair) != {"baseline", "current"}:
            raise ValueError(f"Unpaired run: {records}/{cache}/{operation}/{round_id}")
        if pair["baseline"]["Samples"] != pair["current"]["Samples"]:
            raise ValueError(f"Unequal operation counts: {records}/{cache}/{operation}/{round_id}")
        paired.append(pair["current"]["MeanMilliseconds"] / pair["baseline"]["MeanMilliseconds"])
    comparisons.append({
        "records": records, "engine": engine, "cache": cache, "operation": operation,
        **by_build,
        "currentToBaselineMeanRatios": paired,
        "medianRatio": median(paired),
    })
if not comparisons:
    raise ValueError("No complete timed reports found.")
output = {
    "notes": [
        "P50/P95 columns are medians of within-run percentiles, not pooled percentiles.",
        "Ratios pair process runs with matching round IDs; below one means faster.",
        "Three processes provide diagnostic evidence, not a statistical confidence interval.",
        "Inspect individual ratios and allocations; no automatic pass/fail threshold.",
    ],
    "reports": reports,
    "comparisons": comparisons,
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8", newline="\n")
print(args.output)
