import {folderMotion} from './folder-motion.mjs';
import {renderDeclarativeCard} from './declarative-cards.mjs';
import {frame} from './unified-geometry.mjs';
import {renderSystemCard,isSystemCard,CLOCK_SIZES} from './system-cards.mjs';
import {renderTodoCard} from './todo-card.mjs';

// Illustrations are explicitly unavailable; they never enter the card API.
const pendingSystems = [
 {type:'pending.clock',title:'时钟',kind:'clock',columns:4,rows:2},
 {type:'pending.calendar',title:'日历',kind:'calendar',columns:2,rows:2},
 {type:'pending.status',title:'系统状态',kind:'status',columns:2,rows:2},
 {type:'pending.storage',title:'存储',kind:'status',columns:2,rows:2},
].map(card=>({placeholder:true,kind:card.kind,definition:{type:card.type,title:card.title,sizes:[{columns:card.columns,rows:card.rows}]}}));

// The sheet, snapshot previews and resize observer exist only while open.
export function createCardManager({document,client,create,getProjection=()=>null,focus=async()=>true,changed=()=>{},onError=console.error}){
 let current=null,generation=0,opening=0;
 const node=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!==undefined)el.textContent=text;return el;};
 async function close(immediate=false){
  const sheet=current;if(!sheet)return;current=null;generation++;
  sheet.abort.abort();sheet.previews.disconnect();await sheet.motion.close(immediate);sheet.motion.cancel();sheet.dialog.remove();
 }
 async function open(){
  const ticket=++opening;await close(true);if(ticket!==opening)return;
  const epoch=++generation,abort=new AbortController(),dialog=node('dialog','folder-dialog card-sheet');
  dialog.setAttribute('aria-label','添加卡片');
  const fit=(viewport,rect=viewport.getBoundingClientRect())=>{
   const width=Number(viewport.dataset.width),height=Number(viewport.dataset.height);
   if(!width||!height)return;
   const padding=viewport.classList.contains('card-preview-stage')?40:16;
   viewport.style.setProperty('--card-preview-scale',Math.max(.01,Math.min(1.5,(rect.width-padding)/width,(rect.height-padding)/height)));
  };
  const previews=new ResizeObserver(entries=>entries.forEach(entry=>fit(entry.target,entry.contentRect)));
  const motion=folderMotion(dialog),sheet={dialog,motion,abort,previews,menu:null,menuReturn:null,snapshot:null};current=sheet;
  const listen=(el,event,handler)=>el.addEventListener(event,handler,{signal:abort.signal});
  const heading=node('div','card-sheet-heading'),title=node('h2',null,'添加卡片');
  const sub=node('p','card-sheet-subtitle','选中左侧卡片，在右侧预览并添加到桌面。');
  const exit=node('button','close-button','×');exit.type='button';exit.setAttribute('aria-label','关闭添加卡片');heading.append(title,sub,exit);
  const split=node('div','card-manager-columns'),browse=node('div','card-manager-browse'),detail=node('aside','card-manager-detail');
  detail.setAttribute('aria-label','卡片预览');split.append(browse,detail);
  const status=node('p','card-manager-status','正在读取卡片…');status.setAttribute('role','status');
  const footer=node('div','card-manager-footer');footer.append(node('span',null,'在「我的卡片」上右键可卸载，卡片包将送入回收站。'),node('span','card-keyboard-hint','Enter / 空格预览 · Esc 关闭'));
  dialog.append(heading,split,status,footer);document.body.append(dialog);
  const label=node('span','card-detail-label','预览'),stage=node('div','card-preview-stage'),detailTitle=node('h3','card-detail-title'),description=node('p','card-detail-description');
  const chips=node('div','card-detail-chips'),sizeLabel=node('label','card-detail-size-label','尺寸'),sizes=node('select','card-size-select'),kind=node('span','card-detail-kind');
  sizeLabel.append(sizes);chips.append(sizeLabel,kind);
  const clockLabel=node('label','card-detail-size-label','时钟字号'),clockSizes=node('select','card-size-select');clockSizes.setAttribute('aria-label','时钟字号');
  for(const size of CLOCK_SIZES){const option=node('option',null,size.label);option.value=size.value;option.selected=size.value===82;clockSizes.append(option);}clockLabel.append(clockSizes);clockLabel.hidden=true;chips.append(clockLabel);
  const actions=node('div','card-detail-actions'),add=node('button','card-add-button','添加到桌面'),directory=node('button','card-directory-button','打开卡片目录');
  add.type=directory.type='button';add.disabled=true;actions.append(add,directory);
  detail.append(label,stage,detailTitle,description,chips,actions);
  let selected=null,selectedSize=null,creating=false,directoryBusy=false;
  const sizeChoices=new Map(),tiles=new Map();
  const systemSettings=()=>({fontSize:Number(clockSizes.value)||82});
  function dismissMenu(restore=false){
   sheet.menu?.remove();sheet.menu=null;heading.inert=split.inert=footer.inert=false;
   if(restore&&sheet.menuReturn?.isConnected)sheet.menuReturn.focus();sheet.menuReturn=null;
  }
  function error(value){if(current===sheet){status.textContent=value.message||String(value);status.classList.add('error');status.hidden=false;}}
  listen(exit,'click',()=>void close());
  listen(dialog,'cancel',event=>{event.preventDefault();if(sheet.menu)dismissMenu(true);else void close();});
  listen(dialog,'click',event=>{const r=dialog.getBoundingClientRect();if(event.target===dialog&&(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom))void close();});
  listen(dialog,'pointerdown',event=>{if(sheet.menu&&!sheet.menu.contains(event.target))dismissMenu();event.stopPropagation();});
  listen(dialog,'keydown',event=>event.stopPropagation());
  async function openDirectory(){
   if(directoryBusy)return;directoryBusy=true;directory.disabled=true;
   try{await client.openDirectory();if(current===sheet){status.textContent='将卡片包放入目录后，重新打开此面板即可读取。';status.classList.remove('error');status.hidden=false;}}
   catch(e){error(e);}finally{directoryBusy=false;if(current===sheet)directory.disabled=false;}
  }
  listen(directory,'click',()=>void openDirectory());
  function section(label){
   const section=node('section','card-manager-section'),header=node('div','card-section-heading'),h=node('h3',null,label),count=node('span','card-section-count'),list=node('div','card-manager-list');
   header.append(h,node('span','card-section-line'),count);section.append(header,list);browse.append(section);return {list,count};
  }
  const systems=section('系统卡片'),customs=section('我的卡片');
  const emptyCustom=node('p','card-manager-empty','暂无自定义卡片。打开卡片目录，放入卡片包后重新打开此面板。');
  emptyCustom.hidden=true;customs.list.before(emptyCustom);
  function paintPreview(viewport,row,size){
   const d=row.definition,system=!d.custom&&isSystemCard(d.type),art=node('div','desktop-card card-preview-art');
   const designProjection={originX:0,originY:0,stepX:96,stepY:96,inset:8};
   const projection=system?designProjection:getProjection()??designProjection;
   const bounds=frame({x:0,y:0,w:size.columns,h:size.rows},projection);
   viewport.dataset.width=bounds.width;viewport.dataset.height=bounds.height;
   art.style.width=viewport.dataset.width+'px';art.style.height=viewport.dataset.height+'px';
   viewport.setAttribute('aria-hidden','true');
   if(row.placeholder){
    art.classList.add('card-preview-pending');
    if(row.kind==='status'){
     const bars=node('div','card-preview-bars');for(let i=0;i<3;i++)bars.append(node('span'));art.append(bars);
    }else{
     art.append(node('strong','card-preview-sample',({clock:'09:41',weather:'26°',calendar:'19'})[row.kind]));
     if(row.kind!=='clock')art.append(node('span','card-preview-caption',row.kind==='weather'?'天气示意':'日历示意'));
    }
   }else{
    if(system)art.dataset.appearance='widget';else art.append(node('div','desktop-card-drag',d.title));
    const body=node('div','desktop-card-content');art.append(body);
    if(d.custom)renderDeclarativeCard(body,{definition:d,preview:true});
    else if(system&&d.type==='system.todo')renderTodoCard(body,{definition:d,instance:{id:'preview',settings:{}},preview:true});
    else if(system)renderSystemCard(body,{definition:d,instance:{settings:d.type==='system.clock'?systemSettings():{}},preview:true});
    else body.append(node('span','card-manager-empty','卡片暂不可用'));
   }
   viewport.replaceChildren(art);previews.observe(viewport);fit(viewport);
  }
  function describe(row){
   if(row.placeholder)return '系统卡片尚未接入，当前仅展示示意，暂时无法添加。';
   const custom=row.definition.custom;
   if(!custom)return ({'system.clock':'透明时钟，显示本地日期与时间；支持小、中、大三档字号。','system.calendar':'周一开始的当月日历，自动标记今天并在日期变化后更新。','system.status':'CPU 与 GPU 使用率；当前接口未提供温度时显示“—”。','system.storage':'显示物理内存及固定磁盘容量、占用比例；可打开电脑管家清理页，实际清理由你确认。','system.todo':'记录待办事项，支持完成状态、已完成列表、编辑、删除与撤销。'})[row.definition.type]||'已安装的系统卡片。选择尺寸后可添加到桌面。';
   return ({text:'展示卡片包中的文字，适合便签、座右铭或提醒。',clock:'按卡片包设置显示本地时间，支持 12 / 24 小时制。',countdown:'按卡片包设置的目标时间倒计时，到点显示完成提示。',shortcut:'点击桌面卡片中的入口，打开常用目录或网页。'})[custom.kind]||'来自本机卡片目录。';
  }
  function select(row){
   selected=row;const d=row.definition,index=sizeChoices.get(d.type)||0;selectedSize=d.sizes[index]||d.sizes[0];
   label.textContent=d.type==='system.status'||d.type==='system.storage'?'预览 · 示意数据':'预览';
   for(const [type,tile] of tiles)tile.button.setAttribute('aria-pressed',String(type===d.type));
   paintPreview(stage,row,selectedSize);detailTitle.textContent=d.title;detailTitle.title=d.title;description.textContent=describe(row);description.title=description.textContent;
   sizes.replaceChildren();d.sizes.forEach((s,i)=>{const option=node('option',null,`${s.rows} 行 × ${s.columns} 列`);option.value=i;option.selected=s===selectedSize;sizes.append(option);});
   sizes.setAttribute('aria-label',d.title+'尺寸');sizes.disabled=!!row.placeholder||d.sizes.length===1;
   clockLabel.hidden=row.placeholder||d.type!=='system.clock';
   kind.textContent=row.placeholder?'即将接入':d.custom?'自定义卡片':'系统卡片';
   add.textContent=row.placeholder?'尚未接入':creating?'正在添加…':'添加到桌面';add.disabled=!!row.placeholder||creating;
  }
  listen(sizes,'change',()=>{
   if(!selected)return;sizeChoices.set(selected.definition.type,Number(sizes.value));selectedSize=selected.definition.sizes[Number(sizes.value)];
   paintPreview(stage,selected,selectedSize);const tile=tiles.get(selected.definition.type);
   paintPreview(tile.preview,selected,selectedSize);tile.size.textContent=`${selectedSize.rows} 行 × ${selectedSize.columns} 列`;
  });
  listen(clockSizes,'change',()=>{if(selected?.definition.type==='system.clock'){paintPreview(stage,selected,selectedSize);const tile=tiles.get(selected.definition.type);paintPreview(tile.preview,selected,selectedSize);}});
  listen(add,'click',()=>{
   if(!selected||selected.placeholder||creating)return;
   const d=selected.definition,size=selectedSize;creating=true;add.disabled=true;add.textContent='正在添加…';
   void create(d.type,size.columns,size.rows,d.type==='system.clock'?systemSettings():{}).then(()=>{if(current===sheet)return close();}).catch(e=>{creating=false;if(current===sheet){select(selected);error(e);}});
  });
  function uninstall(row){
   dismissMenu();const box=node('div','card-uninstall-confirm');box.setAttribute('role','alertdialog');box.setAttribute('aria-modal','true');box.setAttribute('aria-label','确认卸载卡片');
   const text=node('p',null,`将从所有桌面移除 ${row.instances} 张「${row.definition.title}」，并将该卡片包送入回收站。`),buttons=node('div','card-confirm-actions'),cancel=node('button',null,'取消'),yes=node('button',null,'卸载卡片');
   cancel.type=yes.type='button';buttons.append(cancel,yes);box.append(node('h3',null,'卸载卡片？'),text,buttons);dialog.append(box);sheet.menu=box;sheet.menuReturn=tiles.get(row.definition.type)?.button;heading.inert=split.inert=footer.inert=true;
   listen(cancel,'click',()=>dismissMenu(true));
   listen(box,'keydown',event=>{if(event.key==='Tab'){event.preventDefault();(document.activeElement===cancel?yes:cancel).focus();}});
   listen(yes,'click',()=>{
    yes.disabled=cancel.disabled=true;const d=row.definition;
    void client.uninstall(d.type,d.custom.fingerprint,row.instances,sheet.snapshot.revision).then(async()=>{if(current!==sheet)return;dismissMenu();await load();tiles.get(selected?.definition.type)?.button.focus();}).catch(async e=>{if(current!==sheet)return;dismissMenu();await load();error(e);});
   });cancel.focus();
  }
  function contextMenu(row,button,x,y){
   dismissMenu();select(row);const menu=node('div','folder-menu card-package-menu'),remove=node('button',null,'卸载卡片');remove.type='button';menu.setAttribute('role','menu');remove.setAttribute('role','menuitem');menu.append(remove);dialog.append(menu);sheet.menu=menu;sheet.menuReturn=button;
   listen(remove,'click',()=>uninstall(row));const rect=dialog.getBoundingClientRect();
   menu.style.left=Math.max(8,Math.min(x-rect.left,rect.width-menu.offsetWidth-8))+'px';menu.style.top=Math.max(8,Math.min(y-rect.top,rect.height-menu.offsetHeight-8))+'px';remove.focus();
  }
  function entries(rows,container){
   for(const preview of container.querySelectorAll('.card-thumbnail'))previews.unobserve(preview);container.replaceChildren();
   for(const row of rows){
    const d=row.definition,previewSize=d.sizes[sizeChoices.get(d.type)||0]||d.sizes[0],item=node('article','card-manager-item'),button=node('button','card-choice'),preview=node('div','card-thumbnail'),info=node('span','card-choice-info'),size=node('span','card-size-label',`${previewSize.rows} 行 × ${previewSize.columns} 列`);
    button.type='button';button.setAttribute('aria-label','预览 '+d.title);button.setAttribute('aria-pressed','false');
    info.append(node('strong',null,d.title),size);button.append(preview,info);item.append(button);container.append(item);tiles.set(d.type,{button,preview,size});paintPreview(preview,row,previewSize);
    if(row.placeholder){item.classList.add('card-pending');preview.append(node('span','card-pending-badge','即将接入'));}
    listen(button,'click',()=>select(row));
    if(d.custom){
     listen(item,'contextmenu',event=>{event.preventDefault();event.stopPropagation();contextMenu(row,button,event.clientX,event.clientY);});
     listen(button,'keydown',event=>{if(event.key==='ContextMenu'||(event.shiftKey&&event.key==='F10')){event.preventDefault();const r=button.getBoundingClientRect();contextMenu(row,button,r.left+12,r.bottom);}});
    }
   }
  }
  function importTile(){
   const item=node('article','card-manager-item card-import'),button=node('button','card-choice'),preview=node('span','card-thumbnail'),info=node('span','card-choice-info');
   button.type='button';button.setAttribute('aria-label','导入卡片，打开卡片目录');button.title='打开卡片目录，放入卡片包后重新打开此面板';
   preview.append(node('span','card-import-plus','＋'));info.append(node('strong',null,'导入卡片'),node('span','card-size-label','card.json'));button.append(preview,info);item.append(button);customs.list.append(item);listen(button,'click',()=>void openDirectory());
  }
  async function load(){
   if(epoch!==generation||current!==sheet)return;
   try{
    const result=await client.list();if(epoch!==generation||current!==sheet)return;
    sheet.snapshot=result;const systemRows=result.types.filter(row=>!row.definition.custom),customRows=result.types.filter(row=>row.definition.custom),rows=[...(systemRows.length?systemRows:pendingSystems),...customRows];
    const previous=selected?.definition.type;tiles.clear();entries(rows.filter(row=>!row.definition.custom),systems.list);entries(customRows,customs.list);importTile();
    systems.count.textContent=systemRows.length?systemRows.length+' 种':'即将接入';customs.count.textContent=customRows.length+' 种';
    emptyCustom.hidden=customRows.length>0;
    select(rows.find(row=>row.definition.type===previous)||customRows[0]||systemRows[0]||rows[0]);status.textContent='';status.hidden=true;status.classList.remove('error');await changed();
   }catch(e){error(e);}
  }
  motion.open(()=>({left:innerWidth/2-70,top:innerHeight-1,width:140,height:1}));try{await focus(exit);}catch(e){onError(e);}void load();
 }
 return {open,close,dispose:()=>{opening++;void close(true);},get isOpen(){return current!==null;}};
}
