"""Verify selected source bytes against the pinned GitHub Git-tree blob hashes."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "references" / "seelen-v2.8.9"
COMMIT = "507378aa01190617c18ee2c70ecc66b316169437"


def main():
    rows = []
    for name in ["source-manifest.json", "startup-source-manifest.json", "task-source-manifest.json", "runtime-preparation-manifest.json"]:
        manifest = json.loads((REF / name).read_text(encoding="utf-8-sig"))
        if manifest["commit"] != COMMIT:
            raise ValueError("Manifest commit mismatch")
        for entry in manifest["files"]:
            raw = (REF / "source" / entry["path"]).read_bytes()
            actual = hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
            matched = actual == entry["git_blob_sha"] and len(raw) == entry["size"]
            rows.append({"path": entry["path"], "bytes": len(raw),
                         "expected_git_blob_sha": entry["git_blob_sha"],
                         "actual_git_blob_sha": actual, "match": matched,
                         "sha256": hashlib.sha256(raw).hexdigest(), "manifest": name})
    report = {"commit": COMMIT, "selected_files": len(rows),
              "all_match": all(row["match"] for row in rows), "files": rows,
              "scope": "Selected source mirror only; not a complete checkout or built application"}
    (REF / "source-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ["commit", "selected_files", "all_match"]}))
    if not report["all_match"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
