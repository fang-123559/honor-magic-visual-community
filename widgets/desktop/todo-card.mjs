const CARD_WIDTH=444,CARD_HEIGHT=217,MAX_UNDO=40,MAX_SETTINGS_BYTES=15000;
const svgNamespace='http://www.w3.org/2000/svg';
const stateByRoot=new WeakMap(),activeCards=new Map(),editingCards=new Set();
let listeningDocument=null;

const demoItems=[
 {id:'demo-1',text:'交材料力学实验报告',order:1,done:false,doneAt:0},
 {id:'demo-2',text:'回复导师的邮件',order:2,done:false,doneAt:0},
 {id:'demo-3',text:'买牛奶 · 面包',order:3,done:false,doneAt:0},
 {id:'demo-4',text:'预约眼科复诊',order:4,done:false,doneAt:0},
 {id:'demo-5',text:'整理抓娃娃脚本的坐标逻辑',order:5,done:false,doneAt:0},
 {id:'demo-6',text:'备份手机照片',order:6,done:true,doneAt:Date.now()-3600000},
];

function node(document,tag,className,text){
 const element=document.createElement(tag);
 if(className)element.className=className;
 if(text!==undefined)element.textContent=text;
 return element;
}
function clone(value){return JSON.parse(JSON.stringify(value));}
function normalizedItems(value){
 if(!Array.isArray(value))return [];
 const seen=new Set(),items=[];
 for(let index=0;index<Math.min(value.length,512);index++){
  const item=value[index];
  if(!item||typeof item!=='object')continue;
  const rawId=item.id;
  if(!(typeof rawId==='string'||typeof rawId==='number'))continue;
  const id=String(rawId);
  if(!id||id.length>64||seen.has(id)||typeof item.text!=='string')continue;
  seen.add(id);
  const order=Number.isSafeInteger(item.order)&&item.order>=0?item.order:index+1;
  const doneAt=Number.isFinite(item.doneAt)&&item.doneAt>0?item.doneAt:0;
  items.push({id,text:item.text,order,done:item.done===true,doneAt});
 }
 return items;
}
function nextOrderFor(items,value){
 const maximum=items.reduce((max,item)=>Math.max(max,item.order),0);
 return Number.isSafeInteger(value)&&value>maximum?value:maximum+1;
}
function persistedKey(settings){
 const items=normalizedItems(settings?.items),nextOrder=nextOrderFor(items,settings?.nextOrder);
 return JSON.stringify({items,nextOrder});
}
function createState(id,settings){
 const items=normalizedItems(settings?.items);
 const nextOrder=nextOrderFor(items,settings?.nextOrder);
 return {id,items,nextOrder,undoStack:[],view:'list',addText:'',focusRequest:null,restoreFocus:null,baseWidth:0,baseHeight:0,
  persistedKey:JSON.stringify({items,nextOrder}),dirty:false,saving:false,saveFailed:false,toastMessage:'',toastUntil:0,toastTimer:0};
}
function ensureState(root,id,settings){
 let state=stateByRoot.get(root);
 if(!state){state=createState(id,settings);stateByRoot.set(root,state);return state;}
 const key=persistedKey(settings);
 if(key!==state.persistedKey&&!state.saving&&!state.dirty){
  const items=normalizedItems(settings?.items),nextOrder=nextOrderFor(items,settings?.nextOrder);
  state.items=items;state.nextOrder=nextOrder;state.undoStack=[];state.persistedKey=JSON.stringify({items,nextOrder});
 }
 return state;
}
function completed(items){return items.filter(item=>item.done).sort((a,b)=>b.doneAt-a.doneAt||b.order-a.order);}
function pending(items){return items.filter(item=>!item.done).sort((a,b)=>a.order-b.order);}
function ordered(items){return [...items].sort((a,b)=>a.order-b.order);}
function settingsFor(api,state,items=state.items,nextOrder=state.nextOrder){
 const source=api?.context?.instance?.settings;
 const settings=source&&typeof source==='object'&&!Array.isArray(source)?Object.assign({},source):{};
 settings.items=clone(items);
 settings.nextOrder=nextOrder;
 return settings;
}
function settingsBytes(settings){
 const json=JSON.stringify(settings);
 return typeof TextEncoder==='function'?new TextEncoder().encode(json).byteLength:json.length*3;
}
function fitsSettings(api,state,items=state.items,nextOrder=state.nextOrder){
 return settingsBytes(settingsFor(api,state,items,nextOrder))<=MAX_SETTINGS_BYTES;
}
function pushUndo(state,label,items=state.items,nextOrder=state.nextOrder){
 state.undoStack.push({items:clone(items),nextOrder,label});
 if(state.undoStack.length>MAX_UNDO)state.undoStack.shift();
}
function topUndo(state){return state.undoStack[state.undoStack.length-1]||null;}
function updateEditingSet(state){
 if(state.view==='edit')editingCards.add(state.id);else editingCards.delete(state.id);
}
function notifyMenuClose(document,id){
 document.dispatchEvent(new CustomEvent('magic-card-menu-close',{detail:{cardId:id}}));
}
function cardForEvent(document,event){
 const menu=event.target?.closest?.('[data-desktop-card-menu-id]');
 if(menu)return activeCards.get(menu.dataset.desktopCardMenuId)||null;
 for(const api of activeCards.values())if(api.root.contains(event.target))return api;
 return null;
}
function hoveredCard(document){
 const surface=document.querySelector('.todo-card-surface:hover');
 return surface?activeCards.get(surface.dataset.todoCardId)||null:null;
}
function installDocumentHandlers(document){
 if(listeningDocument===document)return;
 removeDocumentHandlers();
 listeningDocument=document;
 document.addEventListener('pointerdown',onDocumentPointerDown,true);
 document.addEventListener('keydown',onDocumentKeyDown,true);
 document.addEventListener('visibilitychange',onDocumentVisibility);
 document.defaultView?.addEventListener('blur',onDocumentBlur);
}
function removeDocumentHandlers(){
 if(!listeningDocument)return;
 listeningDocument.removeEventListener('pointerdown',onDocumentPointerDown,true);
 listeningDocument.removeEventListener('keydown',onDocumentKeyDown,true);
 listeningDocument.removeEventListener('visibilitychange',onDocumentVisibility);
 listeningDocument.defaultView?.removeEventListener('blur',onDocumentBlur);
 listeningDocument=null;
}
function onDocumentPointerDown(event){
 const document=event.currentTarget,menu=event.target?.closest?.('[data-desktop-card-menu-id]'),menuId=menu?.dataset.desktopCardMenuId;
 for(const id of [...editingCards]){
  const api=activeCards.get(id);
  if(api&&!api.root.contains(event.target)&&menuId!==id)api.exitEdit();
 }
}
function onDocumentKeyDown(event){
 const document=event.currentTarget,menu=event.target?.closest?.('[data-desktop-card-menu-id]'),menuId=menu?.dataset.desktopCardMenuId;
 const api=cardForEvent(document,event);
 const typing=event.target?.matches?.('input,textarea,[contenteditable="true"]')||false;
 if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'&&api){
  event.preventDefault();event.stopPropagation();notifyMenuClose(document,api.id);api.undo();return;
 }
 if(event.key==='Escape'&&menuId){
  event.preventDefault();event.stopPropagation();notifyMenuClose(document,menuId);return;
 }
 if(event.key==='Escape'&&api&&api.state.view==='edit'){
  event.preventDefault();event.stopPropagation();api.exitEdit();return;
 }
 if(!typing&&(event.key==='e'||event.key==='E')){
  const target=api||hoveredCard(document);
  if(target){event.preventDefault();event.stopPropagation();if(menuId)notifyMenuClose(document,target.id);target.enterEdit();return;}
 }
}
function onDocumentBlur(){
 for(const id of [...editingCards])activeCards.get(id)?.exitEdit();
}
function onDocumentVisibility(){
 if(!listeningDocument?.hidden)return;
 for(const id of [...editingCards])activeCards.get(id)?.exitEdit();
}
function activate(api){
 activeCards.set(api.id,api);
 if(api.state.view==='edit')editingCards.add(api.id);else editingCards.delete(api.id);
 installDocumentHandlers(api.document);
}
function deactivate(api){
 if(activeCards.get(api.id)===api)activeCards.delete(api.id);
 editingCards.delete(api.id);
 if(!activeCards.size)removeDocumentHandlers();
}

function checkbox(document,item){
 const button=node(document,'button','todo-check');
 button.type='button';button.dataset.todoAction='toggle';button.dataset.todoId=item.id;
 button.dataset.focusKey='check:'+item.id;
 button.setAttribute('aria-label',(item.done?'取消完成：':'标记完成：')+item.text);
 button.title=item.done?'取消完成':'标记完成';
 const icon=document.createElementNS(svgNamespace,'svg');
 icon.setAttribute('viewBox','0 0 20 20');icon.setAttribute('aria-hidden','true');
 const ring=document.createElementNS(svgNamespace,'circle');
 ring.setAttribute('class','todo-check-ring');ring.setAttribute('cx','10');ring.setAttribute('cy','10');ring.setAttribute('r','8.2');
 const tick=document.createElementNS(svgNamespace,'path');
 tick.setAttribute('class','todo-check-tick');tick.setAttribute('d','M6.1 10.4 L8.9 13.1 L13.9 7.5');
 icon.append(ring,tick);button.append(icon);
 if(item.done)button.classList.add('is-on');
 return button;
}
function todoRow(document,item,{doneBar=false,more=0}={}){
 const row=node(document,'div','todo-row'+(item.done?' is-done':'')+(doneBar?' todo-done-bar-row':''));
 row.dataset.todoId=item.id;row.setAttribute('role','listitem');
 row.append(checkbox(document,item));
 const text=node(document,'span','todo-text');
 text.append(node(document,'span','todo-ink',item.text));
 row.append(text);
 if(more>0){
  const button=node(document,'button','todo-more','+'+more);
  button.type='button';button.dataset.todoAction='more';button.setAttribute('aria-label','查看全部已完成，共 '+(more+1)+' 项');
  button.title='查看全部已完成';row.append(button);
 }
 return row;
}
function emptyRow(document,text){
 const empty=node(document,'div','todo-empty',text);
 empty.setAttribute('role','status');return empty;
}
function appendHead(document,view,title,count){
 const head=node(document,'div','todo-head');
 const heading=node(document,'h2','todo-title',title);
 head.append(heading);
 if(view==='done'){
  const back=node(document,'button','todo-back','‹');
  back.type='button';back.dataset.todoAction='back';back.dataset.focusKey='done-back';back.title='返回待办';back.setAttribute('aria-label','返回待办');
  head.prepend(back);
 }
 const countNode=node(document,'span','todo-count',count);
 head.append(countNode);
 if(view==='edit'){
  const done=node(document,'button','todo-finish','完成');
  done.type='button';done.dataset.todoAction='finish-edit';done.dataset.focusKey='finish-edit';
  head.append(done);
 }
 return {head,countNode};
}
function restoreFocus(root,state){
 const request=state.focusRequest||state.restoreFocus;
 if(!request)return;
 const target=[...root.querySelectorAll('[data-focus-key]')].find(element=>element.dataset.focusKey===request.key);
 state.focusRequest=null;state.restoreFocus=null;
 if(!target)return;
 target.focus({preventScroll:true});
 if(target instanceof HTMLInputElement&&Number.isInteger(request.start)&&Number.isInteger(request.end)){
  try{target.setSelectionRange(request.start,request.end);}catch{}
 }
}
function editHeight(state){
 const rows=Math.min(state.items.length,9);
 return Math.max(CARD_HEIGHT,Math.min(560,20+34+rows*36+44+16));
}
function syncOverlay(cardRoot,surface,state,preview){
 const width=cardRoot.clientWidth;
 const scale=width>0?width/CARD_WIDTH:376/CARD_WIDTH;
 surface.style.transform='scale('+scale+')';
 if(preview)return;
 if(width>0&&(!state.baseWidth||Math.abs(width-state.baseWidth)>1)){
  state.baseWidth=width;
  state.baseHeight=state.view==='edit'?CARD_HEIGHT*scale:(cardRoot.clientHeight||CARD_HEIGHT*scale);
 }
 const baseHeight=state.baseHeight||CARD_HEIGHT*scale;
 const expanded=state.view==='edit';
 const height=expanded?Math.max(baseHeight,editHeight(state)*scale):baseHeight;
 cardRoot.dataset.todoEditing=String(expanded);
 if(expanded)cardRoot.dataset.cardOverlayHeight=String(height);
 else delete cardRoot.dataset.cardOverlayHeight;
 const cssHeight=height+'px';
 if(cardRoot.style.height!==cssHeight)cardRoot.style.height=cssHeight;
}
async function flushSettings(state){
 if(state.saving||state.saveFailed||!state.dirty)return;
 state.saving=true;
 try{
  while(state.dirty){
   const api=activeCards.get(state.id);
   if(!api||api.context.signal?.aborted)return;
   const items=clone(state.items),nextOrder=state.nextOrder,payload=settingsFor(api,state,items,nextOrder);
   if(settingsBytes(payload)>MAX_SETTINGS_BYTES){
    state.dirty=false;state.saveFailed=true;
    api.showToast('待办内容过多，无法保存');
    return;
   }
   state.dirty=false;
   try{await api.context.updateSettings(payload);}
   catch(error){
    state.dirty=true;state.saveFailed=true;
    const current=activeCards.get(state.id);
    current?.showToast('保存失败：'+(error?.message||'请重试'));
    return;
   }
   state.persistedKey=JSON.stringify({items,nextOrder});
  }
 }finally{
  state.saving=false;
  if(state.dirty&&!state.saveFailed&&activeCards.has(state.id))queueMicrotask(()=>void flushSettings(state));
 }
}
function saveChanges(state){
 state.dirty=true;state.saveFailed=false;void flushSettings(state);
}
function commitTextInput(state,input){
 const before=input.dataset.before;
 const item=state.items.find(value=>value.id===input.dataset.todoId);
 if(!item||before===undefined||before===item.text)return false;
 const previous=clone(state.items),old=previous.find(value=>value.id===item.id);
 if(old)old.text=before;
 pushUndo(state,'修改「'+before+'」',previous,state.nextOrder);
 input.dataset.before=item.text;
 state.dirty=true;
 return true;
}

export function renderTodoCard(root,context){
 const document=root.ownerDocument,preview=context.preview===true,id=String(context.instance?.id||'preview');
 const settings=context.instance?.settings||{};
 const state=preview
  ?{id,items:Array.isArray(settings.items)?normalizedItems(settings.items):clone(demoItems),nextOrder:1,undoStack:[],view:'list',addText:'',focusRequest:null,restoreFocus:null,baseWidth:0,baseHeight:0,toastMessage:'',toastUntil:0}
  :ensureState(root,id,settings);
 root.classList.add('todo-card-content');

 const surface=node(document,'section','todo-card-surface');
 surface.dataset.todoCardId=id;surface.setAttribute('aria-label','待办事项');if(preview){surface.inert=true;surface.style.pointerEvents='none';surface.setAttribute('aria-hidden','true');}
 const listView=node(document,'div','todo-view todo-list-view');
 const listHead=appendHead(document,'list','待办事项','0 / 0');
 const pendingList=node(document,'div','todo-scroll todo-pending-list');
 pendingList.setAttribute('role','list');pendingList.setAttribute('aria-label','待办事项');
 const doneBar=node(document,'div','todo-done-bar');
 listView.append(listHead.head,pendingList,doneBar);

 const doneView=node(document,'div','todo-view todo-completed-view');doneView.hidden=true;
 const doneHead=appendHead(document,'done','已完成','');
 const doneList=node(document,'div','todo-scroll todo-completed-list');
 doneList.setAttribute('role','list');doneList.setAttribute('aria-label','已完成事项');
 doneView.append(doneHead.head,doneList);

 const editView=node(document,'div','todo-view todo-edit-view');editView.hidden=true;
 const editHead=appendHead(document,'edit','编辑待办','');
 const editList=node(document,'div','todo-scroll todo-edit-list');
 editList.setAttribute('role','list');
 editView.append(editHead.head,editList);

 const toast=node(document,'div','todo-toast');toast.setAttribute('role','status');toast.setAttribute('aria-live','polite');
 surface.append(listView,doneView,editView);
 root.replaceChildren(surface,toast);

 function renderToast(){
  const visible=!!state.toastMessage&&Date.now()<state.toastUntil;
  toast.hidden=!visible;
  if(visible)toast.textContent=state.toastMessage;
 }
 function showToast(message){
  state.toastMessage=String(message);state.toastUntil=Date.now()+1700;
  clearTimeout(state.toastTimer);
  state.toastTimer=setTimeout(()=>{
   state.toastMessage='';state.toastUntil=0;
   activeCards.get(state.id)?.paintToast();
  },1700);
  renderToast();
 }
 function updateFade(){
  const overflow=pendingList.scrollHeight-pendingList.scrollTop-pendingList.clientHeight>2;
  listView.classList.toggle('has-overflow',overflow);
 }
 function render(){
  const tasksPending=pending(state.items),tasksDone=completed(state.items);
  listHead.countNode.textContent=tasksDone.length+' / '+state.items.length;
  pendingList.replaceChildren();
  if(!tasksPending.length)pendingList.append(emptyRow(document,'全部完成了 · 点空白处添加新的'));
  else for(const item of tasksPending)pendingList.append(todoRow(document,item));
  doneBar.replaceChildren();
  if(!tasksDone.length)doneBar.append(emptyRow(document,'还没有完成的项目'));
  else doneBar.append(todoRow(document,tasksDone[0],{doneBar:true,more:tasksDone.length-1}));
  doneHead.countNode.textContent=tasksDone.length?tasksDone.length+' 项':'';
  doneList.replaceChildren();
  if(!tasksDone.length)doneList.append(emptyRow(document,'还没有完成的项目'));
  else for(const item of tasksDone)doneList.append(todoRow(document,item));
  editHead.countNode.textContent=state.items.length?state.items.length+' 项':'';
  editList.replaceChildren();
  for(const item of ordered(state.items)){
   const row=node(document,'div','todo-edit-row');row.dataset.todoId=item.id;row.setAttribute('role','listitem');
   row.append(checkbox(document,item));
   const input=node(document,'input','todo-edit-input');input.type='text';input.autocomplete='off';input.spellcheck=false;input.value=item.text;
   input.dataset.todoId=item.id;input.dataset.before=item.text;input.dataset.focusKey='input:'+item.id;
   input.setAttribute('aria-label','编辑待办：'+item.text);row.append(input);
   const remove=node(document,'button','todo-edit-delete','×');remove.type='button';remove.dataset.todoAction='delete';remove.dataset.todoId=item.id;remove.dataset.focusKey='delete:'+item.id;
   remove.setAttribute('aria-label','删除：'+item.text);remove.title='删除';row.append(remove);editList.append(row);
  }
  const add=node(document,'div','todo-edit-add'),plus=node(document,'span','todo-edit-plus','+'),addInput=node(document,'input','todo-edit-add-input');
  addInput.type='text';addInput.autocomplete='off';addInput.spellcheck=false;addInput.placeholder='添加一项待办…';addInput.dataset.focusKey='add';
  addInput.value=state.addText||'';
  addInput.setAttribute('aria-label','添加一项待办');add.append(plus,addInput);editList.append(add);
  listView.hidden=state.view!=='list';doneView.hidden=state.view!=='done';editView.hidden=state.view!=='edit';
  surface.dataset.view=state.view;surface.style.height=(state.view==='edit'?editHeight(state):CARD_HEIGHT)+'px';
  syncOverlay(cardRoot,surface,state,preview);
  updateEditingSet(state);renderToast();restoreFocus(root,state);
  requestAnimationFrame(()=>{if(pendingList.isConnected)updateFade();});
 }
 const cardRoot=root.closest('.desktop-card')||root;
 const controller=new AbortController(),signal=controller.signal;
 const onClick=event=>{
  const action=event.target.closest?.('[data-todo-action]');
  if(action){
   event.stopPropagation();
   const command=action.dataset.todoAction,idValue=action.dataset.todoId;
   if(command==='toggle'){toggle(idValue);return;}
   if(command==='more'){state.view='done';state.focusRequest=null;render();return;}
   if(command==='back'){state.view='list';render();return;}
   if(command==='finish-edit'){exitEdit();return;}
   if(command==='delete'){removeItem(idValue);return;}
  }
  if(state.view==='list'){
   const row=event.target.closest?.('.todo-row');
   if(row){enterEdit(row.dataset.todoId);return;}
   if(event.target.closest?.('.todo-list-view')){enterEdit();return;}
  }else if(state.view==='done'){state.view='list';render();}
 };
 const onInput=event=>{
  const input=event.target.closest?.('.todo-edit-input');
  if(input){
   const item=state.items.find(value=>value.id===input.dataset.todoId);if(!item)return;
   const previous=item.text;item.text=input.value;
   if(!fitsSettings(activeCards.get(state.id),state)){
    item.text=previous;input.value=previous;showToast('待办内容已达到保存上限');
   }
   return;
  }
  const addInput=event.target.closest?.('.todo-edit-add-input');
  if(addInput)state.addText=addInput.value;
 };
 const onKeyDown=event=>{
  const addInput=event.target.closest?.('.todo-edit-add-input');
  if(addInput&&event.key==='Enter'){
   event.preventDefault();const text=addInput.value.trim();if(text)addItem(text);
  }
 };
 const onFocusIn=event=>{
  const input=event.target.closest?.('.todo-edit-input');
  if(input&&input.dataset.before===undefined)input.dataset.before=input.value;
 };
 const onFocusOut=event=>{
  const input=event.target.closest?.('.todo-edit-input');
  if(input)commitTextInput(state,input);
 };
 const finalizeTextEdits=()=>{
  let changed=false;
  for(const input of editList.querySelectorAll('.todo-edit-input'))changed=commitTextInput(state,input)||changed;
  return changed;
 };
 const fit=()=>syncOverlay(cardRoot,surface,state,preview);
 const resizeObserver=!preview&&globalThis.ResizeObserver?new ResizeObserver(fit):null;
 if(resizeObserver)resizeObserver.observe(cardRoot);
 else if(!preview)document.defaultView?.addEventListener('resize',fit,{signal});
 pendingList.addEventListener('scroll',updateFade,{passive:true,signal});
 surface.addEventListener('click',onClick,{signal});
 editList.addEventListener('input',onInput,{signal});editList.addEventListener('keydown',onKeyDown,{signal});
 editList.addEventListener('focusin',onFocusIn,{signal});editList.addEventListener('focusout',onFocusOut,{signal});

 function enterEdit(focusId=null){
  if(state.view==='edit'&&focusId===null)return;
  if(state.view!=='edit')state.addText='';
  state.view='edit';state.focusRequest=focusId===null?{key:'input:'+ordered(state.items)[0]?.id}: {key:'input:'+focusId};
  if(focusId===null&&!state.items.length)state.focusRequest={key:'add'};
  render();
 }
 function exitEdit(){
  if(state.view!=='edit')return;
  finalizeTextEdits();state.addText='';state.view='list';state.focusRequest=null;state.restoreFocus=null;render();
  if(state.dirty)saveChanges(state);
 }
 function toggle(itemId){
  if(state.view==='edit')finalizeTextEdits();
  const item=state.items.find(value=>value.id===itemId);if(!item)return;
  const before=clone(state.items),label=(item.done?'取消完成「':'完成「')+item.text+'」';
  pushUndo(state,label,before,state.nextOrder);
  item.done=!item.done;item.doneAt=item.done?Date.now():0;
  state.focusRequest={key:'check:'+item.id};
  render();saveChanges(state);
 }
 function addItem(text){
  finalizeTextEdits();
  const item={id:String(state.nextOrder),text,order:state.nextOrder,done:false,doneAt:0};
  const nextItems=[...state.items,item],nextOrder=state.nextOrder+1;
  if(!fitsSettings(activeCards.get(state.id),state,nextItems,nextOrder)){showToast('待办内容已达到保存上限');return;}
  pushUndo(state,'添加「'+text+'」');
  state.items=nextItems;state.nextOrder=nextOrder;state.addText='';state.focusRequest={key:'add'};
  render();saveChanges(state);
 }
 function removeItem(itemId){
  finalizeTextEdits();
  const item=state.items.find(value=>value.id===itemId);if(!item)return;
  pushUndo(state,'删除「'+item.text+'」');
  state.items=state.items.filter(value=>value.id!==itemId);
  state.focusRequest={key:'add'};
  render();showToast('已删除');saveChanges(state);
 }
 function clearCompleted(){
  finalizeTextEdits();const count=completed(state.items).length;if(!count)return false;
  pushUndo(state,'清除 '+count+' 项已完成');
  state.items=state.items.filter(item=>!item.done);
  render();showToast('已清除 '+count+' 项已完成');saveChanges(state);return true;
 }
 function undo(){
  const active=document.activeElement;const focusKey=state.view==='edit'&&root.contains(active)?(active.dataset?.focusKey||'add'):'add';
  finalizeTextEdits();const entry=state.undoStack.pop();
  if(!entry){showToast('没有可撤销的操作');return false;}
  state.items=clone(entry.items);state.nextOrder=entry.nextOrder;
  state.focusRequest=state.view==='edit'?{key:focusKey}:null;
  render();showToast('已撤销：'+(entry.label||'上一步操作'));saveChanges(state);return true;
 }
 const api={id,state,root,document,context,enterEdit,exitEdit,undo,showToast,paintToast:renderToast,
  menuState(){const latest=topUndo(state);return {canUndo:!!latest,undoLabel:latest?.label||'',hasCompleted:state.items.some(item=>item.done)};},
  runMenuAction(action){if(action==='undo')return undo();if(action==='edit'){enterEdit();return true;}if(action==='clear')return clearCompleted();return false;}
 };
 render();if(!preview)activate(api);
 if(!preview&&state.dirty)queueMicrotask(()=>void flushSettings(state));
 return preview?undefined:()=>{
  const active=document.activeElement;
  if(root.contains(active)&&active.dataset?.focusKey){
   state.restoreFocus={key:active.dataset.focusKey};
   if(active instanceof HTMLInputElement){state.restoreFocus.start=active.selectionStart;state.restoreFocus.end=active.selectionEnd;}
  }
  controller.abort();resizeObserver?.disconnect();deactivate(api);
  root.classList.remove('todo-card-content');
 };
}

export function todoCardMenuState(id){
 return activeCards.get(String(id))?.menuState()||{canUndo:false,undoLabel:'',hasCompleted:false};
}
export function runTodoCardMenuAction(id,action){
 const api=activeCards.get(String(id));
 return api?api.runMenuAction(action):false;
}
