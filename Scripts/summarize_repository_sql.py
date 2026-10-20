"""Summarize three paired runs of the complete-repository SQL comparison."""
import argparse
import json
import statistics
from pathlib import Path


def summarize(root: Path) -> dict:
    reports = {}
    harnesses = set()
    contracts = set()
    dependencies = set()
    runtime = set()
    index_profiles = set()
    fingerprints = {"before": set(), "current": set()}
    for size in (1000, 10000):
        for round_number in range(1, 4):
            for label in ("before", "current"):
                path = root / f"{label}-{size}-r{round_number}.json"
                report = json.loads(path.read_text(encoding="utf-8"))
                assert report["Passed"] and not report["Failures"], str(path)
                assert report["Label"] == label and report["Records"] == size
                assert report["Iterations"] == 50 and report["MutationLimit"] == 1000
                assert report["Reverse"] == (round_number % 2 == 0)
                assert report["CacheModes"] == (
                    ["MetadataOnly", "StrongObject", "WeakObject"] if size == 1000 else ["MetadataOnly"]
                )
                include_sql_server = report["SqlServerVersion"] is not None
                assert report["SqlServerDelayedDurability"] == ("DISABLED" if include_sql_server else None)
                index_profiles.add(report["SqlIndexProfile"])
                assert len(report["QueryPlans"]) == 2
                for plans in report["QueryPlans"]:
                    assert any("IX_Records_Age" in line for line in plans["Plans"]["Range"])
                    assert any("IX_Records_Name" in line for line in plans["Plans"]["Prefix"])
                assemblies = report["Assemblies"]
                harnesses.add(assemblies["StorageBenchmarks.Transactions"])
                contracts.add(report["MaterializationContract"])
                dependencies.add(tuple(sorted((name, digest) for name, digest in assemblies.items()
                    if name not in ("Idb.Libraries", "InterfaceDB.Storage.SegmentCore"))))
                runtime.add((report["Runtime"], report["OS"], report["SqliteVersion"], report["SqlServerVersion"]))
                fingerprints[label].add((assemblies["Idb.Libraries"], assemblies["InterfaceDB.Storage.SegmentCore"]))
                rows = {}
                for row in report["Metrics"]:
                    key = (row["Engine"], row["Cache"], row["Operation"])
                    assert key not in rows, (path, key)
                    assert row["Records"] == size and row["SamplesMs"]
                    rows[key] = row
                expected_engines = {
                    "InterfaceDB", "InterfaceDB SegmentCore", "SQLite Microsoft.Data.Sqlite",
                    "SQLite Dapper"
                }
                if include_sql_server:
                    expected_engines.add("SQL Server")
                assert {key[0] for key in rows} == expected_engines
                assert len(rows) == ((6 if size == 1000 else 2) + 2 + int(include_sql_server)) * 12
                reports[(label, size, round_number)] = (report, rows)
    assert len(harnesses) == len(contracts) == len(dependencies) == len(runtime) == len(index_profiles) == 1
    assert all(len(values) == 1 for values in fingerprints.values())
    output = []
    for size in (1000, 10000):
        keys = set(reports[("before", size, 1)][1])
        for label in ("before", "current"):
            for round_number in range(1, 4):
                assert set(reports[(label, size, round_number)][1]) == keys
        for key in sorted(keys):
            before = [reports[("before", size, r)][1][key] for r in range(1, 4)]
            current = [reports[("current", size, r)][1][key] for r in range(1, 4)]
            assert len({row["ResultCount"] for row in before + current}) == 1
            previous = statistics.median(row["P50Ms"] for row in before)
            now = statistics.median(row["P50Ms"] for row in current)
            ratios = [c["P50Ms"] / b["P50Ms"] for b, c in zip(before, current)]
            delta = (now / previous - 1) * 100
            if key[2] in ("initialize-empty", "provider-specific-startup"):
                status = "lifecycle-only; no cross-engine ranking"
            elif abs(delta) <= 10:
                status = "similar (within 10%; not proof of equivalence)"
            elif delta < -10 and all(value < 1 for value in ratios):
                status = "lower in all three pairs"
            elif delta > 10 and all(value > 1 for value in ratios):
                status = "higher in all three pairs"
            else:
                status = "mixed across pairs"
            output.append(dict(
                records=size, engine=key[0], cache=key[1], operation=key[2],
                result_count=before[0]["ResultCount"],
                before_p50_ms=previous, current_p50_ms=now, change_percent=delta,
                paired_ratios=ratios, status=status,
                before_p50_runs=[row["P50Ms"] for row in before],
                current_p50_runs=[row["P50Ms"] for row in current],
                current_p95_ms=statistics.median(row["P95Ms"] for row in current),
                current_max_ms=max(max(row["SamplesMs"]) for row in current),
                before_allocated_bytes=statistics.median(row["AllocatedBytesPerOperation"] for row in before),
                current_allocated_bytes=statistics.median(row["AllocatedBytesPerOperation"] for row in current),
            ))
    return dict(
        run_count=len(reports), metadata=list(runtime)[0], harness_sha256=list(harnesses)[0],
        build_fingerprints={label: list(values)[0] for label, values in fingerprints.items()},
        comparison_scope="before/current isolates live-revision retention and metadata-only existence; both already contain correctness fixes",
        classification_rule="10% descriptive band plus direction agreement across all three pairs; not statistical significance",
        metrics=output,
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("results", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    summary = summarize(arguments.results)
    arguments.output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Validated {summary['run_count']} reports and summarized {len(summary['metrics'])} cases.")
