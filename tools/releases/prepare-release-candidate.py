"""Inventory a frozen local acceptance build; never install, package or publish."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest().upper()


def within(root, value):
    path = Path(value)
    path = (root / path).resolve() if not path.is_absolute() else path.resolve()
    path.relative_to(root)
    return path


def prepare(root, candidate_path, build_path, package_path, output):
    root = root.resolve()
    output = within(root, output)
    relative_output = output.relative_to(root)
    if relative_output.parts[0] not in ("artifacts", "evidence") or output.exists():
        raise ValueError("Use a new output directory under this checkout's artifacts/evidence")
    candidate = read(within(root, candidate_path))
    build = read(within(root, build_path))
    package = read(within(root, package_path))
    if candidate.get("schemaVersion") != 1 or candidate["version"] != build["version"] or candidate["packageVersion"] != package["version"]:
        raise ValueError("Candidate/build/package version mismatch")
    source = subprocess.check_output(["git", "rev-parse", "--verify", "refs/tags/" + candidate["sourceTag"] + "^{commit}"], cwd=root, text=True).strip()
    if source != build["sourceCommit"] or source != package["sourceCommit"]:
        raise ValueError("Tag and frozen build source differ")
    for entry in build["files"]:
        if not entry["path"].startswith("versions/" + candidate["version"] + "/") or digest(within(root, entry["path"])) != entry["sha256"].upper():
            raise ValueError("Frozen build file differs: " + entry["path"])
    msix = within(root, package["package"])
    if digest(msix) != package["sha256"].upper():
        raise ValueError("Local MSIX digest differs")
    payload = within(root, str(Path(package["output"]) / "payload"))
    inventory = []
    blockers = [
        {"id": "manual-acceptance", "reason": "Explicit user acceptance is pending; automatic checks cannot approve distribution"},
        {"id": "distribution-signing-and-install", "reason": "Local validation signing and fixed-root launcher are not a public installer"},
        {"id": "clean-machine", "reason": "Clean Windows install/upgrade/uninstall and dependency/resource notices remain release gates"},
    ]
    excluded = []
    forbidden = {"data", "runtime", "evidence", "logs", ".git", "feedback", "cache"}
    local_documents = {"native-launch.json", "native-backend-build.json", "composition-renderer.json"}
    for path in sorted(payload.rglob("*")):
        if not path.is_file():
            continue
        resolved = path.resolve()
        resolved.relative_to(payload)
        relative = path.relative_to(payload).as_posix()
        parts = {part.lower() for part in path.relative_to(payload).parts}
        if parts & forbidden or path.suffix.lower() in {".pfx", ".p12", ".key"}:
            blockers.append({"id": "forbidden-payload", "path": relative, "reason": "User state or key material must not ship"})
        reason = None
        if path.suffix.lower() in {".obj", ".pdb"}:
            reason = "build intermediate or debug symbols"
        elif path.name in local_documents:
            reason = "local launcher or build metadata; regenerate an audited portable contract"
        if reason:
            excluded.append({"path": relative, "reason": reason})
        inventory.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest(path), "proposedDistribution": reason is None and not (parts & forbidden) and path.suffix.lower() not in {".pfx", ".p12", ".key"}})
    if (payload / "native-launch.json").exists():
        launch = read(payload / "native-launch.json")
        if launch.get("root"):
            blockers.append({"id": "machine-root", "path": "native-launch.json", "reason": "Contains a local project-root setting; require first-run per-user data location"})
    output.mkdir(parents=True)
    report = {"schemaVersion": 1, "version": candidate["version"], "sourceTag": candidate["sourceTag"], "sourceCommit": source, "acceptance": candidate["acceptance"], "localPackageSha256": package["sha256"], "localPackageFiles": len(inventory), "distributionPackageBuilt": False, "published": False, "publicBundleReady": False, "blockers": blockers, "proposedExclusions": excluded}
    for name, data in (("preparation.json", report), ("payload-inventory.json", inventory)):
        (output / name).write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--build-receipt", required=True)
    parser.add_argument("--package-receipt", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    result = prepare(Path(__file__).resolve().parents[2], args.candidate, args.build_receipt, args.package_receipt, args.output)
    print(json.dumps({key: result[key] for key in ("version", "sourceCommit", "localPackageFiles", "distributionPackageBuilt", "publicBundleReady")}))
