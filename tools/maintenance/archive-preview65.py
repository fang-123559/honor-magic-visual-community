"""Private baseline archive: no retired packages/caches; no source mutation."""
from pathlib import Path
from datetime import datetime
import hashlib, json, os, shutil, time, zipfile

root=Path(__file__).resolve().parents[2]
local=root/'local-maintenance';local.mkdir(exist_ok=True)
stamp=datetime.now().strftime('%Y%m%d-%H%M%S')
destination=Path(r'F:\magic视界社区版备份')/('preview65-'+stamp)
destination.mkdir(exist_ok=False)
(local/'backup-location.json').write_text(json.dumps({'directory':str(destination)},ensure_ascii=False,indent=2),encoding='utf-8')
def save(name,data):
    (destination/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
save('backup-state.json',{'status':'archiving','baseline':'original preview.65','history':'Retired packages and regenerable build caches excluded'})
started=time.time();entries={}
skip_names={'node_modules','bin','obj','target','.git','__pycache__'}
skips={'versions','profiles','evidence','logs','local-maintenance','artifacts',
       'experiments/native-host-target','experiments/native-ui-deps',
       'tools/cargo-home','tools/rustup-native','tools/dotnet-home','tools/downloads','tools/npm-cache','tools/pip-cache','tools/dotnet',
       'references/seelen-v2.8.9/downloads','references/seelen-v2.8.9/package-extracted'}
def add_tree(source,prefix,excluded=(),excluded_files=()):
    if not source.exists():return
    for base,dirs,files in os.walk(source,followlinks=False):
        b=Path(base)
        dirs[:]=[d for d in dirs if d not in skip_names and not d.startswith('target-') and (b/d).relative_to(source).as_posix() not in excluded and not (b/d).is_junction()]
        for name in files:
            if name in excluded_files or name.startswith('app.current.tmp-'):continue
            p=b/name
            if p.is_symlink():continue
            entries[prefix+'/'+p.relative_to(source).as_posix()]=p
add_tree(root,'project',skips)
for relative in ['versions/0.2.0-preview.65','profiles/native-host/0.1.0-rc.11','profiles/seelen-popups/0.1.0-rc.19',
                 'profiles/community-ui/0.1.2','profiles/power-panel/0.1.2','profiles/seelen-toolbar/0.1.2','tools/dotnet','evidence/preview/0.2.0-preview.65']:
    add_tree(root/relative,'project/'+relative)
original=root/'evidence/desktop-cards/2026-10-19/time-weather-live-preview/baseline'
recovered=root/'local-maintenance/recovered-source65'
for name in ['unified.css','card-catalog.json','card-registry.mjs','card-host.mjs','card-modules.mjs','card-manager.mjs']:
    entries['overlay-source/'+name]=root/'widgets/desktop'/name
    entries['project/widgets/desktop/'+name]=recovered/name
entries['overlay-source/time-weather-card.mjs']=root/'widgets/desktop/time-weather-card.mjs'
entries.pop('project/widgets/desktop/time-weather-card.mjs',None)
for name in ['index.js','index.css','card-catalog.json']:
    relative='versions/0.2.0-preview.65/desktop-widget/'+name
    entries['project/'+relative]=original/relative
for name in ['app.current.json','app.previous.json']:
    entries['project/'+name]=original/'app.current.json'
add_tree(Path(os.environ['APPDATA'])/'com.seelen.seelen-ui','external/seelen-user-data',excluded_files={'clipboard_history.bin'})
add_tree(Path(os.environ['LOCALAPPDATA'])/'MagicCommunity/desktop-cards','external/desktop-cards')
archive=local/('preview65-'+stamp+'.zip');manifest=[]
print('Archive input files: '+str(len(entries)),flush=True)
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=1,allowZip64=True) as z:
    for index,(name,source) in enumerate(sorted(entries.items())):
        sha=hashlib.sha256();size=0
        with source.open('rb') as f,z.open(name,'w',force_zip64=True) as output:
            while chunk:=f.read(1024*1024):
                output.write(chunk);sha.update(chunk);size+=len(chunk)
        manifest.append({'path':name,'bytes':size,'sha256':sha.hexdigest()})
        if index and index%3000==0:print('Archived '+str(index)+' files',flush=True)
    z.writestr('manifest.json',json.dumps({'schemaVersion':1,'baseline':'0.2.0-preview.65','files':manifest},ensure_ascii=False))
print('Archive created; verifying entries',flush=True)
with zipfile.ZipFile(archive) as z:
    for row in manifest:
        sha=hashlib.sha256()
        with z.open(row['path']) as f:
            while chunk:=f.read(1024*1024):sha.update(chunk)
        if sha.hexdigest()!=row['sha256']:raise RuntimeError('Archive entry mismatch: '+row['path'])
def digest(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
print('Archive entries verified; copying one file to backup drive',flush=True)
target=destination/'preview65.zip';shutil.copyfile(archive,target)
a,b=digest(archive),digest(target)
if a!=b:raise RuntimeError('Backup drive archive hash mismatch')
save('manifest.json',{'schemaVersion':1,'baseline':'0.2.0-preview.65','files':manifest})
save('backup-state.json',{'status':'verified','baseline':'original preview.65','archive':'preview65.zip','sha256':b,'files':len(manifest),'archiveBytes':target.stat().st_size,'uncompressedBytes':sum(r['bytes'] for r in manifest),'elapsedSeconds':round(time.time()-started,1),'history':'Retired versions and regenerable caches excluded; preview.66 is void','privacy':'Private local backup includes user layout, feedback and permissions; never publish','restore':'Extract project/ to the original project path. Restore external data only when needed; never overwrite newer user state blindly. Node/Rust dependencies are restored from lockfiles. Seelen 2.8.9 must be installed.'})
print(json.dumps({'directory':str(destination),'files':len(manifest),'sha256':b,'seconds':round(time.time()-started,1)},ensure_ascii=False),flush=True)
