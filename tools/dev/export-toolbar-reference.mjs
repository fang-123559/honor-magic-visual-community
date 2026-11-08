// Rebuild embedded headers from project-owned assets without external packages.
// Only --refresh-reference imports the locked reference and community overrides.
// Both modes read dependencies without installing or modifying them.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';
function stringHeader(value,name="NativeShell"){
  const data=JSON.parse(value);return '#pragma once\nstruct '+name+'LocaleAsset { wchar_t const *language; char const *json; };\ninline constexpr '+name+'LocaleAsset '+name+'LocaleAssets[] = {\n'+Object.entries(data).map(([key,value])=>{const characters=Array.from(JSON.stringify(value)),chunks=[];for(let i=0;i<characters.length;i+=4000)chunks.push('R"SHELL('+characters.slice(i,i+4000).join('')+')SHELL"');return '{L"'+key+'",'+chunks.join('\n')+'},';}).join('\n')+'\n};\n';
}
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const host=path.join(root,'experiments/native-host-patch');
const assetRoot=path.join(root,'assets/native-shell');
const generated=path.join(root,'src/composition-trial/native-toolbar-icons.h');
if(!process.argv.includes('--refresh-reference')){
  if(!fs.existsSync(path.join(assetRoot,'manifest.json')))throw Error('Project-owned asset manifest is missing. Restore assets/native-shell or explicitly import with --refresh-reference.');
  const manifest=JSON.parse(fs.readFileSync(path.join(assetRoot,'manifest.json'),'utf8'));
  const assets=manifest.icons.map(entry=>{const svg=fs.readFileSync(path.join(assetRoot,entry.path),'utf8');if(crypto.createHash('sha256').update(svg).digest('hex')!==entry.sha256)throw Error('Asset hash mismatch: '+entry.path);return [entry.name,svg];});
  fs.writeFileSync(generated,'#pragma once\n// Embedded project-owned asset copies; see assets/native-shell/NOTICE.md.\nstruct ToolbarSvgAsset { wchar_t const *name; char const *svg; };\ninline constexpr ToolbarSvgAsset ToolbarSvgAssets[] = {\n'+assets.map(([name,svg])=>'    {L"'+name+'",R"TB('+svg+')TB"},').join('\n')+'\n};\n');
  const calendar=fs.readFileSync(path.join(assetRoot,'calendar.json'),'utf8').trim();fs.writeFileSync(path.join(root,'src/composition-trial/native-calendar-data.h'),stringHeader(calendar,'NativeCalendar'));
  const strings=fs.readFileSync(path.join(assetRoot,'strings.json'),'utf8').trim();
  fs.writeFileSync(path.join(root,'src/composition-trial/native-shell-strings.h'),stringHeader(strings));
  console.log(JSON.stringify({icons:assets.length,source:'project-owned-files',externalDependencies:false}));process.exit(0);
}
const require=createRequire(path.join(host,'package.json'));
const yaml=require('js-yaml');
const strings={};
function flatten(value,prefix,out){for(const [key,v] of Object.entries(value||{})){const full=prefix?prefix+'.'+key:key;if(typeof v==='string')out[full]=v;else if(v&&typeof v==='object')flatten(v,full,out);}}
for(const [widget,group] of [['weg','dock'],['fancy-toolbar','toolbar']]){
  const directory=path.join(host,'src/ui/svelte',widget,'i18n/translations');
  for(const name of fs.readdirSync(directory).filter(n=>n.endsWith('.yml'))){const lang=name.slice(0,-4).toLowerCase();strings[lang]??={};flatten(yaml.load(fs.readFileSync(path.join(directory,name),'utf8')),group,strings[lang]);}
}
fs.mkdirSync(assetRoot,{recursive:true});fs.mkdirSync(path.join(assetRoot,'licenses'),{recursive:true});fs.writeFileSync(path.join(assetRoot,'strings.json'),JSON.stringify(strings)+'\n');
fs.writeFileSync(path.join(host,'src/background/widgets/native-shell-strings.json'),JSON.stringify(strings)+'\n');
fs.writeFileSync(path.join(root,'src/composition-trial/native-shell-strings.h'),stringHeader(JSON.stringify(strings)));
const moment=require('moment');require('moment/min/locales');
const calendar={};for(const language of moment.locales()){
  const locale=moment.localeData(language),months=[],monthsDate=[],monthsShort=[],days=[],daysShort=[],daysMin=[];
  for(let m=0;m<12;m++){const date=moment([2026,m,1]).locale(language);months.push(locale.months(date,''));monthsDate.push(locale.months(date,'D MMMM'));monthsShort.push(locale.monthsShort(date,''));}
  for(let d=0;d<7;d++){const date=moment([2026,9,18+d]).locale(language);days.push(locale.weekdays(date,''));daysShort.push(locale.weekdaysShort(date));daysMin.push(locale.weekdaysMin(date));}
  const formats={};for(const key of ['LTS','LT','L','LL','LLL','LLLL','l','ll','lll','llll'])formats[key]=locale.longDateFormat(key);
  const meridiems=[];for(let minute=0;minute<1440;minute++){const a=locale.meridiem?.(Math.floor(minute/60),minute%60,false)|| (minute<720?'AM':'PM'),b=locale.meridiem?.(Math.floor(minute/60),minute%60,true)|| (minute<720?'am':'pm');if(!meridiems.length||meridiems.at(-1).upper!==a||meridiems.at(-1).lower!==b)meridiems.push({start:minute,upper:a,lower:b});}
  const ordinals={};for(const period of ['M','D','DDD','d','Q','w','W'])ordinals[period]=Array.from({length:period==='DDD'?366:period==='D'?31:period==='M'?12:period==='Q'?4:period==='d'?7:53},(_,i)=>String(locale.ordinal(i+(period==='d'?0:1),period)));
  calendar[language]={months,monthsDate,monthsShort,days,daysShort,daysMin,formats,meridiems,ordinals,week:locale._week,digits:Array.from({length:10},(_,i)=>locale.postformat(String(i))),comma:locale.postformat(',')};
}
fs.writeFileSync(path.join(assetRoot,'calendar.json'),JSON.stringify(calendar)+'\n');fs.writeFileSync(path.join(root,'src/composition-trial/native-calendar-data.h'),stringHeader(JSON.stringify(calendar),'NativeCalendar'));
fs.copyFileSync(path.join(host,'node_modules/moment/LICENSE'),path.join(assetRoot,'licenses/moment-LICENSE'));
const React=require('react');
const {renderToStaticMarkup}=require('react-dom/server');
const ts=require('typescript');
const bluetoothPath='libs/ui/svelte/utils/bluetoothIcons.ts';
const bluetooth=fs.readFileSync(path.join(host,bluetoothPath),'utf8');
const tableNames=['COMPUTER_ICONS','PHONE_ICONS','LAN_NETWORK_ICONS','AUDIO_VIDEO_ICONS','PERIPHERAL_SUBMINOR_ICONS','PERIPHERAL_MINOR_ICONS','WEARABLE_ICONS','TOY_ICONS','HEALTH_ICONS','HUMAN_INTERFACE_DEVICE_ICONS','APPEARANCE_ICONS'];
const transpiled=ts.transpileModule(bluetooth+`\nglobalThis.tables={${tableNames.join(',')}};`,{compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2022}}).outputText;
const context={exports:{}};vm.runInNewContext(transpiled,context,{timeout:1000});
const tables=JSON.parse(JSON.stringify(context.tables));
// Functions are represented by their corresponding dictionary in the native
// adapter; all string entries retain the exact reference values.
fs.writeFileSync(path.join(host,'src/background/widgets/native-toolbar-bluetooth.json'),JSON.stringify(tables,null,2)+'\n');
const names=new Set(['FaMemory','LuCpu','BsGpuCard','BsDeviceHdd','PiArrowsDownUpBold','FaRegCircleUser','TbWorldCancel','FaWifi','FaComputer','IoVolumeMuteOutline','IoVolumeHighOutline','IoVolumeMediumOutline','IoVolumeLowOutline','IoVolumeOffOutline','IoChevronDownCircleOutline','IoIosArrowDropdown','LuSettings2','TbZzz','MdNotificationsActive','MdOutlineNotifications','TbBluetooth','TbBluetoothConnected','TbPlugConnected','CgExtensionRemove','CgExtensionAdd','TbRestore','VscLock','VscUnlock','PiChartLineFill','RiSettings4Fill']);
for(const name of bluetooth.matchAll(/"((?:Io|Tb|Fa|Bs|Lu|Pi|Hi|Md|Ri|Gi|Lia|Cg)[A-Z][A-Za-z0-9]+)"/g))names.add(name[1]);
for(const name of ['RiUnpinLine','RiPushpinLine','IoOpenOutline','MdOutlineMyLocation','MdOutlineAdminPanelSettings','RiEditBoxLine','AiOutlineCopy','BiWindowClose','MdOutlineDangerous','PiMusicNotesPlusFill','RiFileAddLine','LuSeparatorVertical','LuSeparatorHorizontal','CgLockUnlock','CgLock','IoRemove','FaRegTrashAlt','IoClose','TbRefresh','IoAlarm','IoAlert','IoMail','IoPause','IoPlay','IoBan','IoPlaySkipBack','IoPlaySkipForward'])names.add(name);
// Catalog icons, including passive modules, are copied alongside their glyphs.
for(const folder of ['src/static/plugins','src/static/widgets']){
  function scan(directory,depth=0){if(depth>3)return;for(const entry of fs.readdirSync(directory,{withFileTypes:true})){const file=path.join(directory,entry.name);if(entry.isDirectory())scan(file,depth+1);else if(entry.name.endsWith('.yml')){const match=fs.readFileSync(file,'utf8').match(/^icon:\s*["']?([A-Z][a-z0-9]+[A-Z][A-Za-z0-9]*)["']?\s*$/m);if(match)names.add(match[1]);}}}
  scan(path.join(host,folder));
}
const assets=[];
for(const name of [...names].sort()){
  const prefix=name.match(/^[A-Z][a-z0-9]*/)[0].toLowerCase();
  const pack=name.startsWith('IoMd')||name.startsWith('IoIos')?'io':prefix==='io'?'io5':prefix;
  const Icon=require('react-icons/'+pack)[name] || (prefix==='fa'?require('react-icons/fa6')[name]:prefix==='hi'?require('react-icons/hi2')[name]:undefined);
  if(!Icon)throw Error('Reference icon not found: '+name);
  const svg=renderToStaticMarkup(React.createElement(Icon)).replace(/width="1em"/,'width="24"').replace(/height="1em"/,'height="24"');
  assets.push([name,svg]);
}
const powerPath='profiles/power-panel/0.1.2/template.js';
const power=fs.readFileSync(path.join(root,powerPath),'utf8');
const icons=JSON.parse(power.match(/const icons=(\{.*\});/)[1]);
for(const [kind,values] of Object.entries(icons))for(let i=0;i<values.length;i++){
  assets.push(['MagicBattery'+(kind==='charge'?'Charge':'Normal')+i,Buffer.from(values[i].split(',')[1],'base64').toString('utf8').replace('width="16"','width="24"').replace('height="16"','height="24"')]);
}
for(const name of ['start_menu','start_menu_dark','desktop_light','desktop_dark','trash_bin_empty_light','trash_bin_empty_dark','trash_bin_full_light','trash_bin_full_dark','missing','music_thumbnail']){
  const source=path.join(host,'src/static/icons',name+'.svg');
  let svg=fs.readFileSync(source,'utf8');svg=svg.replace(/(<svg\b[^>]*\bwidth=")[^"]*(")/,'$124$2').replace(/(<svg\b[^>]*\bheight=")[^"]*(")/,'$124$2');
  assets.push(['MagicDock_'+name,svg]);
}
fs.mkdirSync(path.join(assetRoot,'icons'),{recursive:true});fs.mkdirSync(path.join(assetRoot,'licenses'),{recursive:true});
for(const [name,svg] of assets)fs.writeFileSync(path.join(assetRoot,'icons',name+'.svg'),svg);
const out=path.join(root,'src/composition-trial');
fs.writeFileSync(path.join(out,'native-toolbar-icons.h'),'#pragma once\n// Generated by tools/dev/export-toolbar-reference.mjs. See native-toolbar-NOTICE.md.\nstruct ToolbarSvgAsset { wchar_t const *name; char const *svg; };\ninline constexpr ToolbarSvgAsset ToolbarSvgAssets[] = {\n'+assets.map(([name,svg])=>'    {L"'+name+'",R"TB('+svg+')TB"},').join('\n')+'\n};\n');
const manifest=require(path.join(path.join(host,'node_modules/react-icons'),'lib/iconsManifest.js')).IconsManifest;
const used=new Set(assets.filter(([name])=>!name.startsWith('Magic')).map(([name])=>name.startsWith('IoMd')||name.startsWith('IoIos')?'io':name.match(/^[A-Z][a-z0-9]*/)[0].toLowerCase()==='io'?'io5':name.match(/^[A-Z][a-z0-9]*/)[0].toLowerCase()));
used.add('fa6');used.add('hi2');
const notices=manifest.filter(m=>used.has(m.id)).map(m=>`- ${m.name}: ${m.license}; ${m.licenseUrl}; ${m.projectUrl}`).join('\n');
const hash=s=>crypto.createHash('sha256').update(s).digest('hex');
fs.writeFileSync(path.join(out,'native-toolbar-NOTICE.md'),`# Toolbar reference assets\n\nGenerated from the existing locked react-icons package (${JSON.parse(fs.readFileSync(path.join(host,'node_modules/react-icons/package.json'),'utf8')).version}). Icon geometry is unchanged; native rendering resolves currentColor from the Windows theme.\n\n${notices}\n\nCommunity battery icons: ${powerPath}, SHA256 ${hash(power)}.\n\nBluetooth classification table: ${bluetoothPath}, SHA256 ${hash(bluetooth)}. The JSON tables are generated from these same reference dictionaries.\n\nRegenerate with: node tools/dev/export-toolbar-reference.mjs\n`);
for(const name of ['LICENSE','LICENSE.md','LICENSE.txt']){
  const source=path.join(host,name);if(fs.existsSync(source)){fs.copyFileSync(source,path.join(assetRoot,'licenses/Seelen-'+name));break;}
}
const reactLicense=path.join(host,'node_modules/react-icons/LICENSE');if(fs.existsSync(reactLicense))fs.copyFileSync(reactLicense,path.join(assetRoot,'licenses/react-icons-LICENSE'));
fs.copyFileSync(path.join(out,'native-toolbar-NOTICE.md'),path.join(assetRoot,'NOTICE.md'));
fs.writeFileSync(path.join(assetRoot,'manifest.json'),JSON.stringify({schemaVersion:1,icons:assets.map(([name,svg])=>({name,path:'icons/'+name+'.svg',sha256:hash(svg)})),source:'Copied from the locked reference, without filesystem links'},null,2)+'\n');
console.log(JSON.stringify({icons:assets.length,bluetoothTables:tableNames.length,assetRoot}));
