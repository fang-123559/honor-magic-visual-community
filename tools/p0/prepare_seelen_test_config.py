"""Create project-local test configuration; does not write user/system directories."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "references" / "seelen-v2.8.9" / "package-extracted" / "static" / "widgets"
SYSTEM = {"@seelen/settings", "@seelen/dialog", "@seelen/context-menu", "@seelen/tooltip"}


def main():
    widgets = {}
    for path in SOURCE.glob("*/metadata.yml"):
        match = re.search(r"^id:\s*['\"]?([^'\"\s]+)", path.read_text(encoding="utf-8-sig"), re.M)
        if not match:
            raise ValueError(f"Missing widget ID in {path}")
        widget_id = match.group(1)
        widgets[widget_id] = {"enabled": widget_id in SYSTEM}
    if len(widgets) != 24 or not SYSTEM <= set(widgets):
        raise ValueError("Unexpected bundled widget inventory")
    settings = {"byWidget": widgets, "activeThemes": [], "drpc": False,
                "backupSyncEnabled": False, "hardwareAcceleration": True,
                "unstableOptimizations": False, "suspendOnGameMode": False}
    output = ROOT / "evidence" / "p0" / "2026-10-14" / "seelen-runtime" / "test-settings.json"
    output.write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(output), "bundled_widgets": len(widgets),
                      "enabled_system_widgets": sorted(SYSTEM), "system_changed": False}))


if __name__ == "__main__":
    main()
