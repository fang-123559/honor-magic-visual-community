"""Fetch a pinned Microsoft SDK into this project only; verify SHA-512 before extraction."""
import hashlib, json, pathlib, urllib.request, zipfile, time

root = pathlib.Path(__file__).resolve().parents[1]
version = '10.0.402'
metadata_url = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
with urllib.request.urlopen(metadata_url, timeout=60) as response:
    metadata = json.load(response)
sdk = next(s for release in metadata['releases'] for s in release.get('sdks', [release.get('sdk', {})]) if s.get('version') == version)
asset = next(f for f in sdk['files'] if f['rid'] == 'win-x64' and f['name'].endswith('.zip'))
destination = root / 'tools' / 'downloads' / pathlib.PurePosixPath(asset['url']).name
destination.parent.mkdir(parents=True, exist_ok=True)
part = destination.with_suffix('.zip.part')
if not destination.exists():
    total = 0
    started = time.monotonic()
    with urllib.request.urlopen(asset['url'], timeout=120) as response, part.open('wb') as out:
        while block := response.read(1024 * 1024):
            out.write(block)
            total += len(block)
            if total % (20 * 1024 * 1024) == 0:
                print(f'SDK download {total // (1024*1024)} MiB, {time.monotonic()-started:.0f}s', flush=True)
    digest = hashlib.file_digest(part.open('rb'), 'sha512').hexdigest()
    if digest.lower() != asset['hash'].lower():
        raise RuntimeError('SDK SHA-512 mismatch; partial archive retained, never extracted')
    part.replace(destination)
with destination.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha512').hexdigest()
if digest.lower() != asset['hash'].lower():
    raise RuntimeError('SDK SHA-512 mismatch')
sdk_root = root / 'tools' / 'dotnet'
sdk_root.mkdir(exist_ok=True)
with zipfile.ZipFile(destination) as archive:
    for entry in archive.infolist():
        target = (sdk_root / entry.filename).resolve()
        if not target.is_relative_to(sdk_root.resolve()):
            raise RuntimeError('Archive path escapes SDK directory')
    archive.extractall(sdk_root)
receipt = {'sdk': version, 'url': asset['url'], 'sha512': digest, 'bytes': destination.stat().st_size,
           'metadata_url': metadata_url, 'installation': str(sdk_root), 'scope': 'project only'}
(root / 'tools' / 'dotnet.lock.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
print(json.dumps(receipt, indent=2), flush=True)
