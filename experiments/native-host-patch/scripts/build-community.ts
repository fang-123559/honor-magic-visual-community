// Local dev host with minified embedded UI; retains pinned upstream builders.
// SDK aliases always point at this 2.8.9 source, never the helper npm SDK.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {execFileSync} from 'node:child_process';
import esbuild from 'esbuild';
import {extractIcons} from './build/steps/icons.ts';
import {discoverEntryPoints, groupEntryPointsByFramework} from './build/steps/discover.ts';
import {createReactBuildConfig} from './build/builders/react.ts';
import {createSvelteBuildConfig} from './build/builders/svelte.ts';
import {createVanillaBuildConfig} from './build/builders/vanilla.ts';

const root=process.cwd();
const scrollbarRoot=path.resolve(root,'../../widgets/shared');
const scrollbarCss=fs.readFileSync(path.join(scrollbarRoot,'overlay-scrollbars.css'),'utf8');
const overlay=await esbuild.build({stdin:{contents:`import {installScrollbars} from ${JSON.stringify(path.join(scrollbarRoot,'overlay-scrollbars.js'))};if(typeof document!=='undefined'&&typeof window!=='undefined'&&!window.__magicScrollbars){const style=document.createElement('style');style.dataset.magicScrollbars='';style.textContent=${JSON.stringify(scrollbarCss)};(document.head||document.documentElement).append(style);installScrollbars();}`,resolveDir:root},bundle:true,write:false,format:'iife',minify:true,platform:'browser',target:'es2022'});
const overlayBanner=overlay.outputFiles[0].text;
const entries=discoverEntryPoints();
const grouped=groupEntryPointsByFramework(entries);
const folders=entries.map(e=>e.folder);
const aliases={
  '@seelen-ui/lib':path.join(root,'libs/core/src/lib.ts'),
  '@seelen-ui/lib/types':path.join(root,'libs/core/gen/types/mod.ts'),
  '@seelen-ui/types':path.join(root,'libs/core/gen/types/mod.ts'),
  '@seelen-ui/lib/styles':path.join(root,'libs/core/styles'),
  '@seelen-ui/lib/tauri':path.join(root,'libs/core/src/re-exports/tauri.ts'),
  '@std/encoding':path.join(root,'node_modules/@seelen-ui/lib/esm/deps/jsr.io/@std/encoding/1.0.11/mod.js'),
};
await extractIcons();
const args={isProd:true,serve:false};
for(const [name,create] of [['react',createReactBuildConfig],['svelte',createSvelteBuildConfig],['vanilla',createVanillaBuildConfig]] as const){
  if(!grouped[name].length)continue;
  const config=create(grouped[name],folders,args,false);
  config.alias={...config.alias,...aliases};
  // The entry-point bootstrap runs before a third-party loader replaces body.
  // Install in actual app entries, and at the END of that loader instead.
  if(name!=='vanilla')config.banner={...config.banner,js:overlayBanner};
  await esbuild.build(config);
}
fs.appendFileSync(path.join(root,'dist/vanilla/third_party/index.js'),'\n'+overlayBanner);
for(const entry of entries){
  const files=['index.js',...(fs.existsSync(path.join(root,'src/ui',entry.folder,'public/index.html'))?['index.html']:[])];
  for(const file of files){
    if(!fs.existsSync(path.join(root,'dist',entry.folder,file)))throw new Error(`Missing output ${entry.folder}/${file}`);
  }
}
const receipt={at:new Date().toISOString(),sourceCommit:execFileSync('git',['rev-parse','HEAD'],{encoding:'utf8'}).trim(),node:process.version,minified:true,devServer:false,frameworks:Object.fromEntries(Object.entries(grouped).map(([k,v])=>[k,v.length])),sdk:'pinned local 2.8.9',dependencyLockSha256:crypto.createHash('sha256').update(fs.readFileSync('../native-ui-deps/package-lock.json')).digest('hex'),scrollbars:{allEmbeddedEntries:true,includesContextMenu:true,bannerBytes:Buffer.byteLength(overlayBanner),sources:['overlay-scrollbars.js','overlay-scrollbars.css','scrollbar-math.mjs'].map(file=>({file:'widgets/shared/'+file,sha256:crypto.createHash('sha256').update(fs.readFileSync(path.join(scrollbarRoot,file))).digest('hex')}))}};
const receiptPath=process.env.MAGIC_UI_RECEIPT||'../../evidence/community-host/2026-10-16/ui-build.json';
fs.mkdirSync(path.dirname(path.resolve(receiptPath)),{recursive:true});
fs.writeFileSync(receiptPath,JSON.stringify(receipt,null,2));
console.log(JSON.stringify(receipt));
