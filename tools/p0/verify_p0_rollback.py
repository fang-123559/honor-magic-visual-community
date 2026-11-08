"""Verify recorded rollback boundaries and check recorded process birth identities."""
import importlib.util
import json
from pathlib import Path
from statistics import mean

ROOT = Path(__file__).resolve().parents[2]
FOLDER = ROOT / "evidence" / "p0" / "2026-10-14" / "seelen-runtime"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    before = read(FOLDER / "before/system-state.json")
    after = read(FOLDER / "after/system-state.json")
    old = read(ROOT / "evidence/p0/2026-10-14/environment-session.json")
    new = read(FOLDER / "after-rollback-environment.json")
    spec = importlib.util.spec_from_file_location("p0group", ROOT / "tools/p0/measure_process_group.py")
    group = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(group)
    identities = {}
    for path in FOLDER.glob("*.json"):
        data = read(path)
        if not isinstance(data, dict) or not isinstance(data.get("samples"), list):
            continue
        for sample in data["samples"]:
            for row in sample["processes"]:
                if row.get("status") == "observed":
                    identities[(row["pid"], row["creation_filetime"])] = row["name"]
    remaining = []
    for (pid, birth), name in identities.items():
        state = group.read_process(pid)
        if state.get("creation_filetime") == birth and state["status"] == "observed":
            remaining.append({"pid": pid, "name": name})
    def monitor_keys(environment):
        return [(row["device"], row["bounds_physical"], row["work_area_physical"], row["dpi"])
                for row in environment["monitors"]["data"]["items"]]
    def desktop_chain(environment):
        rows = environment["desktop_windows"]["data"]
        progman = next(row for row in rows if row["class"] == "Progman" and row["visible"])
        view = next(row for row in rows if row["class"] == "SHELLDLL_DefView" and row["parent_hwnd"] == progman["hwnd"])
        icons = next(row for row in rows if row["class"] == "SysListView32" and row["parent_hwnd"] == view["hwnd"])
        return [progman, view, icons]
    checks = {
        "packages_absent": not after["installed_packages"],
        "tasks_absent": not after["tasks"],
        "root_processes_absent": not after["processes"],
        "tracked_data_dirs_absent": all(not row["existed"] for row in after["directories"]),
        "user_path_unchanged": before["user_path"] == after["user_path"],
        "monitor_workareas_match": monitor_keys(old) == monitor_keys(new),
        "native_desktop_icon_chain_matches": desktop_chain(old) == desktop_chain(new),
        "recorded_process_identities_absent": not remaining,
    }
    report = {"checks": checks, "all_pass": all(checks.values()),
              "recorded_unique_process_identities": len(identities), "remaining": remaining,
              "scope": "Tracked package/task/process/directories/user PATH/display workareas/desktop HWNDs; not byte-for-byte whole OS restoration",
              "empty_task_folder": "Retained if present; preexistence not captured; no experiment task remains"}
    (FOLDER / "rollback-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    data = read(FOLDER / "restarted-baseline.json")
    samples = data["samples"]
    restarted = {
        "scope": "Restarted host, Dock/toolbar/wallpaper disabled; Apps Menu/Flyouts/Task Switcher still default enabled",
        "samples": len(samples), "all_complete": all(row["complete"] for row in samples),
        "private_ws_mib": mean(row["observed_totals"]["private_working_set_bytes"] for row in samples) / 1048576,
        "private_commit_mib": mean(row["observed_totals"]["private_bytes"] for row in samples) / 1048576,
        "total_ws_sum_mib": mean(row["observed_totals"]["working_set_total_bytes"] for row in samples) / 1048576,
        "cpu_machine_percent": sum(row["observed_cpu_delta_seconds"] for row in samples)
        / sum(row["interval_seconds"] for row in samples) / data["logical_processors"] * 100,
    }
    (FOLDER / "restarted-summary.json").write_text(json.dumps(restarted, indent=2), encoding="utf-8")
    print(json.dumps(report))
    print(json.dumps(restarted))
    if not report["all_pass"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
