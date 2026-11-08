"""Recompute P0 memory summaries from measured JSON, without running a host."""
import hashlib
import json
from pathlib import Path
from statistics import mean

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "evidence" / "p0" / "2026-10-14"
MIB = 1024 * 1024
NAMES = [
    "diagnostic-native-0host", "diagnostic-native-1host",
    "diagnostic-legacy-1host", "diagnostic-native-2host",
    "render-native-1host", "render-software-1host", "render-software-2host",
]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    rows = []
    for name in NAMES:
        path = EVIDENCE / (name + ".json")
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        samples = data["samples"]
        if len(samples) != 20 or data["counters_size"] != 96:
            raise ValueError(f"Unexpected sample count or counter layout: {name}")
        row = {
            "evidence": path.relative_to(ROOT).as_posix(), "sha256": digest(path),
            "hosts": data["hosts"], "method": data["method"],
            "process_render_mode": data["process_render_mode"],
            "pid": data["pid"], "captured_at": data["captured_at"],
            "sample_count": len(samples),
            "sample_duration_seconds": data["sample_duration_seconds"],
            "forced_gc": data["forced_gc"],
            "trimmed_working_set": data["trimmed_working_set"],
        }
        for field in ["working_set_total_bytes", "private_bytes", "private_working_set_bytes"]:
            values = [sample[field] / MIB for sample in samples]
            row[field.removesuffix("_bytes") + "_mib"] = {
                "mean": mean(values), "min": min(values), "max": max(values)
            }
        row["cpu_machine_percent"] = (
            data["sample_cpu_delta_seconds"] / data["sample_duration_seconds"]
            / data["logical_processors"] * 100
        )
        row["mean_query_managed_allocated_bytes"] = mean(
            sample["query_managed_allocated_bytes"] for sample in samples
        )
        row["managed_live_estimate_mib_at_end"] = data["stages"][-1]["managed_live_estimate_bytes"] / MIB
        rows.append(row)
    hashes = []
    inputs = [
        "experiments/p0/wpf-diagnostics/Program.cs",
        "experiments/p0/wpf-diagnostics/WpfDiagnostics.csproj",
        "experiments/p0/wpf-diagnostics/bin/Release/net8.0-windows/WpfDiagnostics.exe",
        "experiments/p0/wpf-diagnostics/bin/Release/net8.0-windows/WpfDiagnostics.dll",
        "experiments/p0/seelen-widget/package.json",
        "experiments/p0/seelen-widget/package-lock.json",
        "experiments/p0/seelen-widget/src/index.js",
        "experiments/p0/seelen-widget/build.mjs",
        "experiments/p0/seelen-widget/metadata.yml",
        "experiments/p0/seelen-widget/bundles/empty.js",
        "experiments/p0/seelen-widget/bundles/100-items.js",
        "tools/p0/summarize_diagnostics.py",
    ]
    for relative in inputs:
        path = ROOT / relative
        if not path.is_file():
            raise FileNotFoundError(path)
        hashes.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest(path)})
    report = {
        "schema_version": 1, "unit": "MiB = 1048576 bytes",
        "measurement_scope": "Hidden empty HWNDs; no visible or Seelen runtime acceptance",
        "summary_method": "Arithmetic mean of all 20 recorded samples; no outlier removal",
        "rows": rows, "current_artifact_hashes": hashes,
        "hash_limitation": "Current source/binary hashes captured after runs; earlier diagnostic runs preceded the render-mode experiment revision."
    }
    (EVIDENCE / "wpf-diagnostic-summary.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    for row in rows:
        print(row["evidence"], {key: round(row[key]["mean"], 2) for key in
              ["private_working_set_mib", "working_set_total_mib", "private_mib"]})


if __name__ == "__main__":
    main()
