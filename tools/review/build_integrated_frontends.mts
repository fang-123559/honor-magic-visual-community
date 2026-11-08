// Build every embedded UI with the SDK source shipped in the same host.
// Preserve dist first; never clean or rewrite dependencies or active profiles.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
import {execFileSync} from 'node:child_process';
import {discoverEntryPoints} from '../../experiments/native-host-patch/scripts/build/steps/discover.ts';
import {extractIcons} from '../../experiments/native-host-patch/scripts/build/steps/icons.ts';
import {createReactBuildConfig} from '../../experiments/native-host-patch/scripts/build/builders/react.ts';
import {createSvelteBuildConfig} from '../../experiments/native-host-patch/scripts/build/builders/svelte.ts';
import {createVanillaBuildConfig} from '../../experiments/native-host-patch/scripts/build/builders/vanilla.ts';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const native=path.join(root,'experiments/native-host-patch');
const require=createRequire(path.join(native,'package.json'));
const esbuild=require('esbuild');
const version=process.argv[2];
if(!/^\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(version??''))throw Error('Pass the application version');
const out=process.argv[3]?path.resolve(root,process.argv[3]):path.join(root,'artifacts/integrated-frontends',version);
if(!out.startsWith(path.join(root,'artifacts')+path.sep))throw Error('Build receipt must stay inside owned artifacts');
await fs.mkdir(out,{recursive:true});
try{await fs.access(path.join(out,'build-receipt.json'));throw Error('Frontend build receipt already exists');}catch(e){if(e.code!=='ENOENT')throw e;}
process.chdir(native);
const dist=path.join(native,'dist');
if(!dist.startsWith(root+path.sep)||!out.startsWith(root+path.sep))throw Error('Output path escaped the checkout');
try{
 if((await fs.lstat(dist)).isSymbolicLink())throw Error('dist is a reparse link');
 const prior=path.join(out,'prior-dist-'+crypto.randomUUID());
 if(!prior.startsWith(root+path.sep))throw Error('Preservation path escaped the checkout');
 try{await fs.access(prior);throw Error('Prior dist already preserved');}catch(e){if(e.code!=='ENOENT')throw e;}
 await fs.rename(dist,prior);
 await fs.mkdir(dist);
 const priorIcons=path.join(prior,'icons'),currentIcons=path.join(dist,'icons');
 if(!priorIcons.startsWith(root+path.sep)||!currentIcons.startsWith(root+path.sep))throw Error('Icon path escaped the checkout');
 try{await fs.rename(priorIcons,currentIcons);}catch(e){if(e.code!=='ENOENT')throw e;}
}catch(e){if(e.code!=='ENOENT')throw e;await fs.mkdir(dist,{recursive:true});}
await extractIcons();
const entries=discoverEntryPoints().map(entry=>({...entry,path:entry.path.replaceAll('\\','/'),folder:entry.folder.replaceAll('\\','/')}));
const folders=entries.map(entry=>entry.folder);
const alias=Object.fromEntries(Object.entries({
 '@seelen-ui/lib':'libs/core/src/lib.ts','@seelen-ui/lib/types':'libs/core/gen/types/mod.ts',
 '@seelen-ui/types':'libs/core/gen/types/mod.ts','@seelen-ui/lib/styles':'libs/core/styles',
 '@seelen-ui/lib/tauri':'libs/core/src/re-exports/tauri.ts',
 '@std/encoding':'node_modules/@seelen-ui/lib/esm/deps/jsr.io/@std/encoding/1.0.11/mod.js',
}).map(([key,value])=>[key,path.join(native,value)]));
const hash=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const sources=new Map(),counts={};
for(const [framework,factory] of Object.entries({react:createReactBuildConfig,svelte:createSvelteBuildConfig,vanilla:createVanillaBuildConfig})){
 const selected=entries.filter(entry=>entry.framework===framework).map(entry=>entry.path);
 counts[framework]=selected.length;if(!selected.length)continue;
 const config=factory(selected,folders,{isProd:true,serve:false},false);
 const result=await esbuild.build({...config,absWorkingDir:native,alias:{...config.alias,...alias},metafile:true});
 await fs.writeFile(path.join(out,framework+'-metafile.json'),JSON.stringify(result.metafile,null,2));
 if(result.warnings.length)throw Error(framework+' frontend has warnings');
 for(const output of Object.values(result.metafile.outputs))if(output.imports.some(item=>item.external&&/\.ts(?:$|\?)/.test(item.path)))throw Error('Unbundled TypeScript import');
 for(const input of Object.keys(result.metafile.inputs)){
  // CSS-module plugin inputs use virtual namespaces, not filesystem paths.
  if(/^[^:]+:/.test(input)&&! /^[A-Za-z]:[\\/]/.test(input))continue;
  const full=path.resolve(native,input);
  if(full.includes(path.sep+'node_modules'+path.sep+'@seelen-ui'+path.sep+'lib'+path.sep)&&!full.includes(path.sep+'deps'+path.sep))throw Error('Old packaged SDK was bundled: '+input);
  if(full.startsWith(native+path.sep)&&!full.includes(path.sep+'node_modules'+path.sep))sources.set(path.relative(native,full),hash(await fs.readFile(full)));
 }
}
const assets=[];
async function recordAssets(directory){
 for(const entry of await fs.readdir(directory,{withFileTypes:true})){
  const full=path.join(directory,entry.name);
  if(entry.isSymbolicLink())throw Error('Frontend assets cannot be links: '+full);
  if(entry.isDirectory())await recordAssets(full);
  else if(entry.isFile())assets.push({path:path.relative(dist,full).replaceAll('\\','/'),sha256:hash(await fs.readFile(full))});
 }
}
await recordAssets(dist);assets.sort((a,b)=>a.path.localeCompare(b.path));
const assetKeys=new Set(assets.map(file=>file.path));
for(const entry of entries){
 const script=entry.path.replace(/^(?:\.\/)?src\/ui\//,'').replace(/\.(tsx?|svelte)$/,'.js');
 if(!assetKeys.has(script))throw Error('Missing built frontend: '+script);
 const html=path.join(native,'src/ui',entry.folder,'public/index.html');
 try{
  const text=await fs.readFile(html,'utf8'),htmlKey=entry.folder+'/index.html';
  if(!assetKeys.has(htmlKey))throw Error('Missing frontend page: '+htmlKey);
  for(const match of text.matchAll(/(?:src|href)=["']([^"']+)["']/g)){
   const reference=match[1];if(/^(?:[a-z]+:|#|\/\/)/i.test(reference))continue;
   const key=reference.startsWith('/')?reference.slice(1):path.posix.normalize(entry.folder+'/'+reference);
   if(!assetKeys.has(key))throw Error('Missing static dependency: '+htmlKey+' -> '+key);
  }
 }catch(error){if(error.code!=='ENOENT')throw error;}
}
const commit=execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
const receipt={at:new Date().toISOString(),version,sourceCommit:commit,counts,sdk:'Current libs/core source',active:false,
 sources:[...sources].map(([path,sha256])=>({path,sha256})),assets};
await fs.writeFile(path.join(out,'build-receipt.json'),JSON.stringify(receipt,null,2));
console.log(JSON.stringify({version,counts,sources:sources.size,active:false}));
