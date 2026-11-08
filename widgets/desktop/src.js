import { Widget, ConnectedMonitorList, Settings } from "@seelen-ui/lib";
import { getCurrentWebview } from "@tauri-apps/api/webview";

const session=globalThis.__MAGIC_BRIDGE;
const widget=Widget.self;
const monitorId=widget.decoded.monitorId;
const status=document.getElementById("status");
let model=null,selected=null,dragging=null,lastState="",busy=false;
function message(text,error=false){status.textContent=text;status.classList.toggle("error",error);}
async function request(path,data){
  const response=await fetch(session.endpoint+path,{method:data===undefined?"GET":"POST",headers:{Authorization:"Bearer "+session.token,...(data===undefined?{}:{"Content-Type":"application/json"})},body:data===undefined?undefined:JSON.stringify(data),signal:AbortSignal.timeout(7000)});
  const result=await response.json();if(!response.ok)throw new Error(result.error||"操作未完成");return result;
}
async function action(kind,values={}){
  try{busy=true;await request("/action",{monitorId,kind,...values});if(!["settings","feedback","exit"].includes(kind))await update(true);message(kind==="remove"?"引用已移除，原文件保留。":"操作已完成。");}
  catch(error){message(error.message,true);}finally{busy=false;}
}
function tile(item,sourceBox=null){
  const node=document.createElement("div");node.className="tile"+(selected===item.id?" selected":"");node.draggable=true;node.title=item.name;node.tabIndex=0;
  if(item.icon){const icon=document.createElement("img");icon.src=item.icon;icon.alt="";node.append(icon);}
  const name=document.createElement("span");name.className="name";name.textContent=item.name;node.append(name);
  node.addEventListener("click",()=>{
    selected=item.id;
    // Preserve the clicked DOM node so Chromium can recognize the second click.
    for(const tile of document.querySelectorAll(".tile"))tile.classList.toggle("selected",tile.dataset.item===selected);
    for(const button of document.querySelectorAll(".add"))button.disabled=model.readOnly;
  });
  node.dataset.item=item.id;
  node.addEventListener("dblclick",()=>action("open",{itemId:item.id}));
  node.addEventListener("keydown",event=>{if(event.key==="Enter")action("open",{itemId:item.id});});
  node.addEventListener("dragstart",event=>{dragging={itemId:item.id,sourceBox};event.dataTransfer.setData("application/x-magic-reference",JSON.stringify(dragging));event.dataTransfer.effectAllowed="copyMove";});
  node.addEventListener("dragend",()=>{dragging=null;});
  if(sourceBox){
    const tools=document.createElement("div");tools.className="item-actions";
    const remove=document.createElement("button");remove.textContent="×";remove.title="移除引用，保留原文件";remove.disabled=model.readOnly;
    remove.addEventListener("click",event=>{event.stopPropagation();action("remove",{itemId:item.id,boxId:sourceBox});});tools.append(remove);node.append(tools);
    node.addEventListener("contextmenu",event=>{
      event.preventDefault();document.querySelectorAll(".move-menu").forEach(x=>x.remove());
      const menu=document.createElement("div");menu.className="move-menu";
      for(const destination of model.destinations.filter(x=>x.id!==sourceBox)){
        const button=document.createElement("button");button.textContent="移到 "+destination.label;button.disabled=model.readOnly;
        button.addEventListener("click",event=>{event.stopPropagation();menu.remove();action("move",{itemId:item.id,sourceBox,boxId:destination.id});});menu.append(button);
      }
      const reveal=document.createElement("button");reveal.textContent="打开所在目录";reveal.addEventListener("click",event=>{event.stopPropagation();menu.remove();action("reveal",{itemId:item.id});});menu.append(reveal);
      node.append(menu);
    });
  }
  return node;
}
function render(){
  const boxes=document.getElementById("boxes");boxes.replaceChildren();
  for(const box of model.boxes){
    const card=document.createElement("article");card.className="box";card.dataset.box=box.id;
    const title=document.createElement("div");title.className="box-title";
    const fold=document.createElement("button");fold.textContent=(box.collapsed?"▸ ":"▾ ")+box.name+" ("+box.items.length+")";fold.disabled=model.readOnly;fold.addEventListener("click",()=>action("toggle",{boxId:box.id}));title.append(fold);
    const add=document.createElement("button");add.className="add";add.textContent="加入选中";add.disabled=!selected||model.readOnly;add.addEventListener("click",()=>action("assign",{itemId:selected,boxId:box.id}));title.append(add);card.append(title);
    if(!box.collapsed){const tiles=document.createElement("div");tiles.className="tiles";for(const item of box.items)tiles.append(tile(item,box.id));card.append(tiles);if(!box.items.length){const hint=document.createElement("p");hint.className="empty";hint.textContent="从下方选择项目，或把文件拖到这里。";card.append(hint);}}
    card.addEventListener("dragover",event=>{if(!model.readOnly){event.preventDefault();card.classList.add("drag-target");event.dataTransfer.dropEffect="move";}});
    card.addEventListener("dragleave",()=>card.classList.remove("drag-target"));
    card.addEventListener("drop",event=>{event.preventDefault();card.classList.remove("drag-target");if(model.readOnly)return;const value=event.dataTransfer.getData("application/x-magic-reference");if(value){try{const ref=JSON.parse(value);action(ref.sourceBox?"move":"assign",{...ref,boxId:box.id});}catch{message("无法识别拖动项目。",true);}}});
    boxes.append(card);
  }
  const library=document.getElementById("library");library.replaceChildren();for(const item of model.library)library.append(tile(item));
  document.getElementById("count").textContent="("+model.library.length+")";
}
async function update(force=false){
  const next=await request("/state?monitor="+encodeURIComponent(monitorId));
  const text=JSON.stringify(next);model=next;if(force||text!==lastState){lastState=text;render();}
}
async function main(){
  await widget.init({useThemes:false,saveAndRestoreLastRect:false});
  const monitors=await ConnectedMonitorList.getAsync();const monitor=monitors.asArray().find(x=>x.id===monitorId);if(!monitor)throw new Error("当前显示器已断开。");
  const registration=await request("/register",{monitorId,rect:monitor.rect,scale:monitor.scaleFactor});
  document.getElementById("screen").textContent=registration.label;
  document.getElementById("version").textContent=session.version;
  await widget.setPosition(registration.rect);await update();await widget.ready();
  const configuration=await Settings.getAsync();
  console.info("MAGIC_DESKTOP_READY",JSON.stringify({widgetId:widget.id,monitorId,sessionId:session.sessionId,version:session.version,sdkVersion:"2.8.4",rect:registration.rect,origin:location.origin,windowId:widget.windowId,dockEnabled:configuration.byWidget["@seelen/weg"]?.enabled,toolbarEnabled:configuration.byWidget["@seelen/fancy-toolbar"]?.enabled,nativeIconTakeover:false}));
  await request("/ready",{monitorId,widgetId:widget.id,windowId:widget.windowId,origin:location.origin,sdkVersion:"2.8.4"});
  for(const kind of ["refresh","settings","feedback","exit"])document.getElementById(kind).addEventListener("click",()=>{if(kind!=="exit"||confirm("退出 Magic 桌面并返回 Windows？布局和原文件会保留。"))action(kind);});
  try{
    await getCurrentWebview().onDragDropEvent(event=>{
      if(event.payload.type!=="drop"||model.readOnly)return;
      const point=event.payload.position;const element=document.elementFromPoint(point.x/devicePixelRatio,point.y/devicePixelRatio);const box=element?.closest("[data-box]")?.dataset.box;
      if(box)action("drop-files",{boxId:box,paths:event.payload.paths});else message("请把文件拖到指定的收纳盒内。",true);
    });
  }catch(error){console.warn("MAGIC_NATIVE_DROP_NOT_AVAILABLE",error.message);}
  message("桌面已连接 · 选择项目加入收纳盒，双击打开。");
  setInterval(()=>{if(!busy&&!dragging)update().catch(()=>message("连接已断开，请从固定入口重新进入桌面。",true));},2500);
}
main().catch(async error=>{message("桌面连接失败："+error.message+"。请从固定入口重试。",true);console.error("MAGIC_DESKTOP_FAILED",error.message);try{await widget.ready();}catch{}});
