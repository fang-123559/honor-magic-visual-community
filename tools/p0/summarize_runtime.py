"""Recompute actual Seelen group and visible WPF resource comparisons."""
import hashlib
import json
from pathlib import Path
from statistics import mean

ROOT = Path(__file__).resolve().parents[2]
FOLDER = ROOT / "evidence" / "p0" / "2026-10-14" / "seelen-runtime"
MIB = 1024 * 1024


def main():
    rows = []
    for phase in ["without-widget", "empty-widget", "100-items-widget", "after-unload"]:
        path = FOLDER / (phase + ".json")
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        samples = data["samples"]
        if len(samples) != 120:
            raise ValueError("Unexpected runtime sample count")
        row = {"phase": phase, "evidence": str(path.relative_to(ROOT)),
               "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
               "sample_count": len(samples),
               "all_complete": all(sample["complete"] for sample in samples),
               "incomplete_samples": sum(not sample["complete"] for sample in samples),
               "process_count_min": min(len(sample["processes"]) for sample in samples),
               "process_count_max": max(len(sample["processes"]) for sample in samples),
               "process_names": sorted({process["name"] for sample in samples for process in sample["processes"]})}
        for field, label in [("private_working_set_bytes", "private_working_set_mib"),
                             ("private_bytes", "private_commit_mib"),
                             ("working_set_total_bytes", "summed_working_set_mib")]:
            values = [sample["observed_totals"][field] / MIB for sample in samples]
            row[label] = {"mean": mean(values), "min": min(values), "max": max(values)}
        row["cpu_machine_percent"] = (
            sum(sample["observed_cpu_delta_seconds"] for sample in samples)
            / sum(sample["interval_seconds"] for sample in samples)
            / data["logical_processors"] * 100
        )
        rows.append(row)
    base = rows[0]
    for row in rows:
        row["delta_from_warm_baseline_mib"] = {
            label: row[label]["mean"] - base[label]["mean"]
            for label in ["private_working_set_mib", "private_commit_mib", "summed_working_set_mib"]
        }
    report = {
        "scope": "One real Seelen 2.8.9 GUI plus elevated helper and descendants; two monitor Widgets; SDK 2.8.4 compatibility probe",
        "baseline_kind": "Warm runtime after first default startup, config API change and earlier probe loads; not a clean cold launch or all-widgets-disabled baseline",
        "conditions": "Dock, toolbar and wallpaper false verified through real SDK after user clicked ModifyConfiguration permission; Apps Menu/Flyouts/Task Switcher default enabled; hardware acceleration left enabled",
        "summary_method": "Mean of all 120 samples in each phase; no sample exclusion",
        "rows": rows,
        "wpf_visible_evidence": "evidence/p0/2026-10-14/wpf-visible/summary.json",
        "limitations": [
            "Summed total working sets double-count shared pages across processes; private resident and private commit are primary comparisons.",
            "Absolute Seelen group totals and adding-Widget increments are different quantities.",
            "Seelen internally simulates memory pressure every 30 seconds; the sampler does not force GC or trim.",
            "Desktop Widget visible flags and matched physical rectangles do not prove visual/occlusion, dragging, Win+D or mixed-DPI acceptance.",
            "WPF uses opaque ordinary windows; Seelen uses transparent bottom windows. This is preliminary host resource evidence, not final architecture acceptance.",
            "Missing/unreadable members are explicitly marked incomplete; incomplete observed sums cannot be accepted as complete totals.",
            "No GPU allocation, long-term drift or cold-start isolation measurement."
        ]
    }
    (FOLDER / "resource-summary.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for row in rows:
        print(json.dumps({"phase": row["phase"], "all_complete": row["all_complete"],
                          "private_ws_mib": row["private_working_set_mib"]["mean"],
                          "private_commit_mib": row["private_commit_mib"]["mean"],
                          "cpu_machine_percent": row["cpu_machine_percent"],
                          "delta_private_ws_mib": row["delta_from_warm_baseline_mib"]["private_working_set_mib"]}))


if __name__ == "__main__":
    main()
