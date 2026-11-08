import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(import.meta.url); // Optional offline importer dependency only.
export const protocol = 'Magic.System.Configuration/1';
const aliases = new Map([
  ['@seelen/tb-user-menu','magic.user'],['@seelen/tb-power-menu','magic.power-menu'],
  ['@seelen/tb-calendar-popup','magic.clock'],['@seelen/tb-system-tray','magic.tray'],
  ['@seelen/tb-bluetooth-popup','magic.bluetooth'],['@seelen/tb-keyboard-selector','magic.keyboard'],
  ['@default/power','magic.battery'],['@seelen/tb-network-popup','magic.network'],
  ['@seelen/tb-quick-settings','magic.quick-settings'],['@seelen/tb-notifications','magic.notifications'],
  ['@seelen/tb-media-popup','magic.audio'],['@default/weg-start-menu','magic.start'],
  ['@default/weg-show-desktop','magic.desktop'],['@default/weg-trash-bin','magic.recycle'],
  ['@default/weg-power','magic.power-menu'],['@seelen/dock-system-tray','magic.tray'],
  ['@seelen/dock-calendar-popup','magic.clock'],['@seelen/dock-media-popup','magic.audio'],
  ['@seelen/dock-bluetooth-popup','magic.bluetooth'],['@seelen/dock-keyboard-selector','magic.keyboard'],
  ['@seelen/dock-notifications','magic.notifications'],['@seelen/dock-network-popup','magic.network'],
  ['@default/memory-usage','magic.memory'],['@default/cpu-usage','magic.cpu'],
  ['@default/gpu-usage','magic.gpu'],['@default/disk-usage','magic.storage'],
  ['@default/network-usage','magic.network-usage'],['@default/focused-app','magic.focused-app'],
  ['@default/focused-app-title','magic.focused-title']
]);
const styleFields = {
  enabled:'enabled',position:'edge',hideMode:'autoHide',margin:'outerMargin',padding:'innerPadding',
  delayToShow:'showDelayMs',delayToHide:'hideDelayMs',itemSize:'itemSize',size:'iconSize',
  spaceBetweenItems:'itemGap',mode:'widthMode',middleClickAction:'middleClickAction',
  pinnedItemsVisibility:'pinnedVisibility',temporalItemsVisibility:'runningVisibility',
  revealOnAttention:'attentionReveal',showEndTask:'showEndTask',showInstanceCounter:'showInstanceCount',
  showWindowTitle:'showWindowTitle',splitWindows:'splitWindows'
};
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
function text(value, limit=32768) {
  if(value === undefined || value === null) return '';
  if(typeof value !== 'string' || value.length > limit || value.includes('\0')) throw new Error('Invalid source string');
  return value;
}
function plain(file) {
  const full=path.resolve(file);
  for(let cursor=full;;cursor=path.dirname(cursor)) {
    if(fs.existsSync(cursor) && fs.lstatSync(cursor).isSymbolicLink()) throw new Error('Source/output contains a reparse point');
    const parent=path.dirname(cursor);if(parent===cursor) break;
  }
  return full;
}
export function transform(settings, toolbar, dock) {
  const warnings=[], ids=new Set();
  let total=0;
  const idFor=(surface,section,index,source) => {
    const id=source || `${surface}-${section}-${index}`;
    if(typeof id!=='string'||id.length>96||!id||ids.has(id)) throw new Error('Duplicate or invalid source item ID');
    ids.add(id);return id;
  };
  function moduleRow(id, source) {
    source=text(source,256);
    if(aliases.has(source)) return {id,kind:'module',module:aliases.get(source)};
    warnings.push({kind:'compatibility-module',item:id,sourceModuleId:source,reason:'Retained in order; executable plugin is not run by the native backend'});
    return {id,kind:'compatibility',sourceModuleId:source};
  }
  function row(surface,section,index,value) {
    if(++total>256) throw new Error('Source item count exceeds native configuration bound');
    if(typeof value==='string') return moduleRow(idFor(surface,section,index),value);
    if(!value || typeof value!=='object'||Array.isArray(value)) throw new Error('Invalid source item');
    const id=idFor(surface,section,index,value.id);
    if(value.type==='Plugin') return moduleRow(id,value.plugin);
    if(value.type==='Separator') return {id,kind:'separator'};
    if(value.type==='Media' || value.type==='MediaSession') return {id,kind:'module',module:'magic.player'};
    if(value.type!=='AppOrFile') throw new Error('Unknown source item type; no item discarded');
    for(const field of ['pinned','preventPinning'])if(value[field]!==undefined&&value[field]!==null&&typeof value[field]!=='boolean')throw new Error('Invalid application boolean');
    const result={id,kind:'application',displayName:text(value.displayName,512),target:text(value.path),aumid:text(value.umid,512),pinned:Boolean(value.pinned),allowPin:!value.preventPinning};
    if(value.relaunch) {
      const launch=value.relaunch;
      if(typeof launch!=='object'||Array.isArray(launch)) throw new Error('Unknown relaunch schema; cannot safely transform');
      for(const name of Object.keys(launch)) if(!['command','args','workingDir','icon'].includes(name)) throw new Error('Unknown relaunch field; cannot safely transform');
      let args=launch.args ?? [];
      if(Array.isArray(args)){if(args.length>128)throw new Error('Too many launch arguments');args=args.map(x=>text(x,8192));}else args=text(args,8192);
      result.launch={executable:text(launch.command),arguments:args,workingDirectory:text(launch.workingDir),iconSource:text(launch.icon)};
      if(/(?:password|api[_-]?key|secret|access[_-]?token)\s*(?:=|:|\s)/i.test(JSON.stringify(result.launch))) throw new Error('Launch configuration may contain credentials; manual migration required');
    }
    return result;
  }
  function style(value,depth=0){
    if(depth>2)throw new Error('Appearance nesting exceeds bound');
    if(value===null||typeof value==='boolean')return value;
    if(typeof value==='number'){if(!Number.isFinite(value)||value< -4096||value>60000)throw new Error('Appearance number outside bound');return value;}
    if(typeof value==='string')return text(value,128);
    if(typeof value!=='object'||Array.isArray(value)||Object.keys(value).length>32)throw new Error('Unsupported appearance structure');
    return Object.fromEntries(Object.entries(value).map(([key,item])=>[text(key,64),style(item,depth+1)]));
  }
  function bar(name,state,legacyId) {
    if(!state || typeof state!=='object'||Array.isArray(state)) throw new Error('Missing bar state');
    const sections={};for(const section of ['left','center','right']) {
      if(!Array.isArray(state[section])) throw new Error('Missing source section');
      if(state[section].length>256)throw new Error('Source section exceeds bound');
      sections[section]=state[section].map((item,index)=>row(name,section,index,item));
    }
    const appearance={}, source=settings.byWidget?.[legacyId] || {};
    for(const [key,value] of Object.entries(source)) {
      if(key==='$shortcuts'){if(value && Object.keys(value).length) warnings.push({kind:'shortcuts-not-migrated',surface:name});continue;}
      if(!(key in styleFields)){warnings.push({kind:'appearance-field-not-migrated',surface:name,field:key});continue;}
      appearance[styleFields[key]]=style(value);
    }
    if(state.isReorderDisabled!==undefined&&typeof state.isReorderDisabled!=='boolean')throw new Error('Invalid reorder flag');
    return {locked:Boolean(state.isReorderDisabled),sections,appearance};
  }
  const preferences={};if(settings.language!==undefined)preferences.language=text(settings.language,64);if(settings.dateFormat!==undefined)preferences.dateFormat=text(settings.dateFormat,128);
  const configuration={protocol,schemaVersion:1,revision:1,toolbar:bar('toolbar',toolbar,'@seelen/fancy-toolbar'),dock:bar('dock',dock,'@seelen/weg'),preferences};
  if(Buffer.byteLength(JSON.stringify(configuration))>256*1024) throw new Error('Configuration exceeds native bound');
  return {configuration,warnings,itemCount:total,requiresReview:warnings.length>0};
}
export function migrate(source, output, apply=false, yamlModule) {
  const yaml = require(yamlModule || 'js-yaml');
  if(typeof yaml.load!=='function'||!yaml.JSON_SCHEMA)throw new Error('A compatible js-yaml parser is required');
  source=plain(source);output=plain(output);
  const artifacts=path.join(root,'artifacts')+path.sep;
  if(!output.toLowerCase().startsWith(artifacts.toLowerCase())||fs.existsSync(output)) throw new Error('Use a new directory under this worktree artifacts');
  const names=['settings.json','data/seelen-fancy-toolbar/state.yml','data/seelen-weg/state.yml'];
  const files=names.map(name=>{const file=plain(path.join(source,name));const bytes=fs.readFileSync(file);if(bytes.length>(name.endsWith('.json')?1024*1024:256*1024))throw new Error('Source exceeds bound');return {name,file,bytes,sha256:sha(bytes)};});
  const settings=JSON.parse(files[0].bytes.toString('utf8'));
  const parse=bytes=>yaml.load(bytes.toString('utf8'),{schema:yaml.JSON_SCHEMA,json:false});
  const result=transform(settings,parse(files[1].bytes),parse(files[2].bytes));
  const plan={protocol,schemaVersion:1,mode:apply?'isolated-data-published':'plan-only',itemCount:result.itemCount,warnings:result.warnings,requiresReview:result.requiresReview,activationAllowed:false,sourceRetained:true,accountTokensImported:false,permissionsImported:false,startupChanged:false,sources:files.map(({name,sha256})=>({name,sha256})),configurationSha256:sha(Buffer.from(JSON.stringify(result.configuration,null,2))),scope:'Toolbar/Dock partitions, ordered items, launch metadata and declared appearance only; no desktop/permission/plugin runtime migration'};
  fs.mkdirSync(path.dirname(output),{recursive:true});plain(path.dirname(output));
  const staging=output+'.staging-'+crypto.randomUUID();fs.mkdirSync(staging);
  fs.writeFileSync(path.join(staging,'migration-plan.json'),JSON.stringify(plan,null,2));
  fs.writeFileSync(path.join(staging,apply?'configuration.json':'configuration.candidate.json'),JSON.stringify(result.configuration,null,2));
  for(const file of files) if(sha(fs.readFileSync(file.file))!==file.sha256) throw new Error('Source changed during migration; staging retained, no target published');
  fs.renameSync(staging,output);
  return {...plan,output};
}
if(process.argv[1] && import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href) {
  const args=process.argv.slice(2);const source=args[args.indexOf('--source')+1],output=args[args.indexOf('--output')+1];
  if(!args.includes('--source')||!args.includes('--output')) throw new Error('Explicit --source and --output required');
  const yamlModule=args.includes('--yaml-module')?args[args.indexOf('--yaml-module')+1]:undefined;
  const result=migrate(source,output,args.includes('--apply'),yamlModule);
  console.log(JSON.stringify({output:result.output,mode:result.mode,itemCount:result.itemCount,requiresReview:result.requiresReview,warningCount:result.warnings.length,activationAllowed:false,sourceRetained:true},null,2));
}
