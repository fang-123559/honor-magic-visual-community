"""Download and inspect the pinned MSIX as data; never install or execute it."""
import hashlib
import json
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path, PurePosixPath
import stat
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "references" / "seelen-v2.8.9"
URL = "https://github.com/eythaann/Seelen-UI/releases/download/v2.8.9/Seelen.UI_2.8.9.0_x64.Msix"
SIZE = 43725362
SHA256 = "ea1b513e1c95af06a5765ef662fc664a9ea97c5e449a7d5e846ed83798747f4b"


def main():
    downloads = REF / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    package = downloads / "Seelen.UI_2.8.9.0_x64.Msix"
    if not package.exists():
        partial = package.with_suffix(".Msix.part")
        block_size = 256 * 1024
        blocks = list(range(0, SIZE, block_size))
        payload = bytearray(SIZE)

        def read_block(start):
            end = min(SIZE - 1, start + block_size - 1)
            request = urllib.request.Request(URL + f"?download=1&range={start}", headers={
                "User-Agent": "MagicCommunity-P0-readonly", "Accept-Encoding": "identity",
                "Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(request, timeout=20) as response:
                expected = f"bytes {start}-{end}/{SIZE}"
                if response.status != 206 or response.headers.get("Content-Range") != expected:
                    raise RuntimeError("Range response does not match requested block")
                data = response.read(end - start + 1)
            if len(data) != end - start + 1:
                raise RuntimeError("Range block length mismatch")
            return start, data

        received = 0
        print(f"Downloading {SIZE} bytes in validated ranges; no install/run", flush=True)
        with ThreadPoolExecutor(max_workers=4) as pool:
            futures = [pool.submit(read_block, start) for start in blocks]
            try:
                for future in as_completed(futures, timeout=360):
                    start, data = future.result()
                    payload[start:start + len(data)] = data
                    received += len(data)
                    if received // (4 * 1024 * 1024) != (received - len(data)) // (4 * 1024 * 1024):
                        print(f"Received {received}/{SIZE} bytes", flush=True)
            except Exception:
                for future in futures:
                    future.cancel()
                raise
        if received != SIZE or hashlib.sha256(payload).hexdigest() != SHA256:
            raise RuntimeError("Downloaded package size or SHA-256 mismatch; package not used")
        partial.write_bytes(payload)
        partial.replace(package)
    if package.stat().st_size != SIZE or hashlib.sha256(package.read_bytes()).hexdigest() != SHA256:
        raise RuntimeError("Existing package does not match pinned release asset")
    destination = (REF / "package-extracted").resolve()
    destination.relative_to(ROOT)
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(package) as archive:
        for info in archive.infolist():
            path = PurePosixPath(info.filename)
            if path.is_absolute() or ".." in path.parts or "\\" in info.filename or ":" in info.filename:
                raise RuntimeError(f"Unsafe package entry: {info.filename}")
            if stat.S_ISLNK(info.external_attr >> 16):
                raise RuntimeError("Symlink entry refused")
            target = (destination / info.filename).resolve()
            target.relative_to(destination)
        archive.extractall(destination)
        entries = archive.namelist()
    manifest = ET.parse(destination / "AppxManifest.xml").getroot()
    ns = {"appx": "http://schemas.microsoft.com/appx/manifest/foundation/windows10"}
    identity = manifest.find("appx:Identity", ns)
    applications = manifest.findall("appx:Applications/appx:Application", ns)
    report = {
        "schema_version": 1, "url": URL, "size": SIZE, "sha256": SHA256,
        "hash_matches_published_asset": True,
        "identity": identity.attrib if identity is not None else None,
        "applications": [app.attrib for app in applications],
        "executables": [entry for entry in entries if entry.lower().endswith(".exe")],
        "entry_count": len(entries), "extracted_directory": str(destination),
        "installed": False, "executed": False,
        "note": "MSIX inspected as ZIP data only. Matching asset digest is not an installation/runtime or signature-verification result."
    }
    evidence = ROOT / "evidence" / "p0" / "2026-10-14" / "seelen-package.json"
    evidence.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
