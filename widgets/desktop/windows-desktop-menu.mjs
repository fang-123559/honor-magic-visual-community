import {menuIcon} from './menu-icons.mjs';
import {inlineRename} from './inline-rename.mjs';
const initialCompact=[['查看','view',true],['排序方式','sort',true],['刷新','refresh',false],['粘贴\tCtrl+V','paste',false],['新建','new',true],['显示设置','display',false],['个性化','personalize',false]].map(([label,key,submenu])=>({label,key,submenu,...(['view','sort'].includes(key)?{disabled:true,reason:'统一网格的查看与排序尚未适配；暂不操作隐藏的 Windows 图标层。'}:{}),...(key==='paste'?{disabled:true}:{})}));
const clean = text => text.replace(/\(&.\)|&/g,'');

// All panels use the current WebView and the same click-away boundary. Shell
// supplies labels, state, lazy submenus and command IDs; only "More" gets Magic
// actions. Never inserts provider-supplied text as HTML.
export function windowsDesktopMenu({document,request,claim,magic,message,changed,fileOperation,createItem,placementFor=()=>({}),bounds=()=>({left:0,top:0,right:innerWidth,bottom:innerHeight}),focusForEdit,editEnded=()=>{},canCommand=()=>true}) {
  let editor=null,active=null,panels=[],hover=0,compactCache=initialCompact,newCache=[{label:'文件夹',key:'new-folder'},{label:'快捷方式',key:'new-shortcut'}];
  const node=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!==undefined)el.textContent=text;return el;};
  const fields=items=>items.length>1?{itemIds:items.map(item=>item.id)}:items.length?{itemId:items[0].id}:{};
  const release=id=>{void request('/menu-dismiss',{requestId:id}).catch(()=>{});};
  function trim(depth){while(panels.length>depth)panels.pop().remove();changed();}
  function close(releaseLease=true){const prior=active;active=null;hover++;trim(0);if(prior?.ready&&releaseLease)release(prior.requestId);}
  function position(panel,x,y){
    const area=bounds();
    panel.style.maxHeight=Math.max(1,area.bottom-area.top-12)+'px';
    panel.style.maxWidth=Math.max(1,area.right-area.left-12)+'px';
    panel.style.left=Math.max(area.left+6,Math.min(x,area.right-panel.offsetWidth-6))+'px';
    panel.style.top=Math.max(area.top+6,Math.min(y,area.bottom-panel.offsetHeight-6))+'px';changed();
  }
  function ensure(prior,fullFileMenu=false){
    if(fullFileMenu&&prior.ready&&!prior.fullFileMenu)throw new Error('菜单查询方式已固定，请关闭菜单后重新打开“更多选项”。');
    if(!prior.ready){const id=prior.requestId;prior.fullFileMenu=fullFileMenu;prior.ready=request('/desktop-context-open',{requestId:id,fullFileMenu,...fields(prior.items)}).then(result=>{
      if(active!==prior||result.cancelled){if(active===prior)close();release(prior.requestId);return;}
      prior.more=result.more;prior.compact=result.compact;prior.renameName=result.renameName;
      if(!prior.item)compactCache=initialCompact.map(row=>row.key==='paste'?row:{...row,...result.compact.find(actual=>actual.key===row.key),icon:undefined,index:undefined});
    }).catch(error=>{
      // A failed/expired native lease cannot be reused. A retry gets new IDs
      // and command indices; a late failure must never reset another menu.
      release(id);
      if(active===prior&&prior.requestId===id){prior.ready=null;prior.fullFileMenu=false;prior.requestId=crypto.randomUUID().replaceAll('-','');}
      throw error;
    });}
    return prior.ready;
  }
  async function nativePath(prior,entry,path){
    await ensure(prior);if(active!==prior)return null;
    const current=entry.index===undefined&&path.length===0?prior.compact.find(row=>row.key===entry.key):entry;
    if(!current||current.disabled)return null;return [...path,current.index];
  }
  async function invoke(entry,parent){
    const prior=active;if(!prior)return;
    // Refresh our desktop directly. Building a complete Explorer menu first
    // adds extension/icon IPC and refreshes the hidden native view needlessly.
    if(!prior.item&&entry.key==='refresh'){close();magic('refresh');return;}
    if(prior.item&&['open','copy','cut','rename','delete','properties'].includes(entry.key)||!prior.item&&entry.key==='paste'){
      const items=prior.items,anchor=prior.anchor;close();await command(entry.key,items,anchor);return;
    }
    if(!prior.item&&createItem&&['new-folder','new-text'].includes(entry.key)){close();await createItem(entry.key==='new-folder'?'folder':'text',prior.point);return;}
    if(entry.resolveEntry){const result=await entry.resolveEntry();if(result&&active===prior)return invoke(result.entry,result.parent);return;}
    const path=await nativePath(prior,entry,parent);if(!path)return;
    close(false);
    const creating=!prior.item&&path.length===2&&prior.compact.some(row=>row.key==='new'&&row.index===path[0]);
    try{await request('/desktop-context-invoke',{requestId:prior.requestId,path,...(creating?placementFor(prior.point):{})});}
    catch(error){message(error.message,true);}
    finally{release(prior.requestId);}
  }
  async function children(entry,parent,button,depth){
    const prior=active;if(!prior)return;const sequence=++hover;trim(depth+1);
    let immediate=null;
    try{
      const r=button.getBoundingClientRect(),place=panel=>{const x=r.right+2+panel.offsetWidth>bounds().right-6?r.left-panel.offsetWidth-2:r.right+2;position(panel,x,r.top-4);};
      if(entry.rows){place(render(entry.rows(),[],depth+1));return;}
      const query=(async()=>{const path=await nativePath(prior,entry,parent);if(!path)return null;const result=await request('/desktop-context-items',{requestId:prior.requestId,path});return {items:result.items,path};})();
      if(entry.key==='new'){
        const rows=newCache.map((row,index)=>({...row,resolveEntry:async()=>{
          const result=await query;if(!result)return null;
          const actual=result.items[index]?.label===row.label?result.items[index]:result.items.find(item=>row.key!=='document'&&item.key===row.key||clean(item.label)===clean(row.label));
          return actual?{entry:actual,parent:result.path}:null;
        }}));
        immediate=render(rows,[],depth+1);place(immediate);
      }
      const result=await query;if(!result||active!==prior||(immediate?panels[depth+1]!==immediate:sequence!==hover))return;
      if(entry.key==='new')newCache=result.items;
      place(render(result.items,result.path,depth+1));
    }catch(error){if(active===prior)message(error.message,true);}
  }
  function render(rows,path,depth,compact=false){
    trim(depth);const panel=node('div','windows-desktop-menu');panel.setAttribute('role','menu');panel.setAttribute('aria-label',compact?'Windows 桌面菜单':'更多桌面操作');
    panel.addEventListener('contextmenu',event=>event.preventDefault());
    panel.addEventListener('scroll',()=>{hover++;trim(depth+1);},{passive:true});
    for(const entry of rows){
      if(entry.separator){panel.append(node('div','windows-menu-separator'));continue;}
      const button=node('button','windows-menu-item');button.type='button';button.disabled=entry.disabled===true;button.title=entry.reason||'';
      if(entry.key&&!entry.action&&!entry.run)button.dataset.nativeKey=entry.key;
      button.setAttribute('role',entry.checked?'menuitemcheckbox':'menuitem');if(entry.checked)button.setAttribute('aria-checked','true');
      const icon=node('span','windows-menu-icon');icon.setAttribute('aria-hidden','true');
      if(entry.checked)icon.textContent='\u2713';else{const value=menuIcon(document,entry);if(value)icon.append(value);}button.append(icon);
      const parts=clean(entry.label).split('\t');button.setAttribute('aria-label',parts[0]);button.append(node('span','windows-menu-label',parts[0]));
      if(parts[1])button.append(node('span','windows-menu-shortcut',parts[1]));
      if(entry.submenu){button.append(node('span','windows-menu-arrow','\u203a'));button.setAttribute('aria-haspopup','menu');}
      const select=async()=>{
        if(entry.action==='more'){
          const prior=active;if(!prior||prior.moreLoading)return;
          prior.moreLoading=true;button.disabled=true;panel.setAttribute('aria-busy','true');
          const label=button.querySelector('.windows-menu-label');label.textContent='正在加载更多选项…';button.setAttribute('aria-label',label.textContent);button.title='按 Esc 或点击菜单外可取消加载。';
          const origin=panel.getBoundingClientRect();position(panel,origin.left,origin.top);
          try{await ensure(prior,!!prior.item&&prior.items.some(item=>!item.isFolder&&!item.isVirtual));}
          catch(error){
            if(active===prior&&button.isConnected){label.textContent='重试加载更多选项';button.setAttribute('aria-label',label.textContent);button.title=error.message;}
            throw error;
          }finally{prior.moreLoading=false;button.disabled=false;panel.removeAttribute('aria-busy');}
          if(active!==prior)return;
          const rect=panels[0].getBoundingClientRect();const extras=prior.item?prior.extras:
            [{label:'新建大文件夹',action:'new-folder',key:'magic-new'},
             {label:'新建卡片',key:'magic-card',action:'new-card'},
             ...[['校准网格布局','reposition-folders','reposition'],['记录问题','feedback','feedback'],['退出桌面','exit','exit']].map(([label,action,key])=>({label,action,key}))];
          const all=[...prior.more,...(extras.length?[{separator:true},...extras]:[])];
          const expanded=render(all,[],0);position(expanded,rect.left,rect.top);expanded.querySelector('button:not(:disabled)')?.focus({preventScroll:true});
        }else if(entry.run){close();await entry.run();}
        else if(entry.action){const action=entry.action,point=active?.point;close();magic(action,point);}
        else if(entry.submenu)await children(entry,path,button,depth);
        else await invoke(entry,path);
      };
      const selectSafe=()=>{const prior=active;void select().catch(error=>{if(active===prior)message(error.message,true);});};
      button.addEventListener('click',event=>{event.stopPropagation();selectSafe();});
      button.addEventListener('pointerenter',()=>{if(entry.submenu&&!button.disabled)void children(entry,path,button,depth);else{hover++;trim(depth+1);}});
      button.addEventListener('keydown',event=>{if(event.key==='ArrowRight'&&entry.submenu){event.preventDefault();selectSafe();}else if(event.key==='ArrowLeft'&&depth){event.preventDefault();trim(depth);panels[depth-1]?.querySelector('button:not(:disabled)')?.focus();}});
      panel.append(button);
    }
    (document.querySelector('dialog[open]')||document.body).append(panel);panels.push(panel);changed();return panel;
  }
  async function command(key,items=[],anchor){
    if(!canCommand(key)){message('上一项操作尚未完成或桌面当前只读，请稍后重试。',true);return;}
    try{
      if(['copy','cut','paste'].includes(key)){await request('/desktop-clipboard-command',{action:key,...(key==='paste'?{}:fields(items))});return;}
      if(!items.length)return;
      if(key==='rename'){if(items.length===1)await rename(items[0],anchor);return;}
      if(key==='properties'){await request('/desktop-properties',{requestId:crypto.randomUUID().replaceAll('-',''),...fields(items)});return;}
      if(key==='open'&&items.length===1){await request('/action',{kind:'open',itemId:items[0].id});return;}
      if(key==='delete'&&items.length===1&&!items[0].isVirtual){await fileOperation('recycle',items[0].id,{});return;}
      const id=crypto.randomUUID().replaceAll('-','');
      try{
        const result=await request('/desktop-context-open',{requestId:id,...fields(items)});
        const actual=[...(result.compact||[]),...(result.more||[])].find(row=>row.key===key&&!row.disabled&&!row.submenu);
        if(!actual)throw new Error('Windows 未向当前选择提供此操作。');
        await request('/desktop-context-invoke',{requestId:id,path:[actual.index]});
      }finally{release(id);}
    }catch(error){message(error.message,true);}
  }
  async function rename(item,anchor){
    if(item.isVirtual)return;editor?.cancel();
    anchor=anchor||[...document.querySelectorAll('[data-item]')].find(value=>value.dataset.item===item.id);
    if(!anchor?.isConnected)return;
    const pending={cancel(){if(editor===pending){editor=null;editEnded();}}};editor=pending;
    try{const focused=await focusForEdit(anchor);if(editor!==pending)return;if(!focused){pending.cancel();return;}}
    catch(error){pending.cancel();message(error.message,true);return;}
    const original=item.renameName||item.name;
    const current=inlineRename({document,anchor,value:original,isFolder:item.isFolder,ended:()=>{editor=null;editEnded();}});editor=current;
    const value=await current.result;if(editor===current){editor=null;editEnded();}
    if(value!==null&&value!==original&&value.length)await fileOperation('rename',item.id,{name:value});
  }
  async function show(x,y,options={}){
    editor?.cancel();claim();close();const prior={requestId:crypto.randomUUID().replaceAll('-',''),more:[],compact:[],point:{x,y},item:options.item,items:options.items?.length?options.items:options.item?[options.item]:[],anchor:options.anchor,extras:options.extras||[]};active=prior;
    let compact=compactCache;
    if(prior.items.length>1)compact=[['剪切','cut'],['复制','copy'],['删除','delete'],['属性','properties']].map(([label,key])=>({label,key}));
    else if(prior.item){compact=(prior.item.isVirtual?[['打开','open'],['属性','properties']]:[['打开','open'],['剪切','cut'],['复制','copy'],['重命名','rename'],['删除','delete'],['属性','properties']]).map(([label,key])=>({label,key}));}
    const rows=[...compact,{separator:true},{label:'显示更多选项',action:'more',key:'more'}];
    const panel=render(rows,[],0,true);position(panel,x,y);panel.querySelector('button:not(:disabled)')?.focus({preventScroll:true});
    if(!prior.item)void request('/desktop-clipboard-state',{}).then(result=>{
      if(active!==prior||panels[0]!==panel)return;const paste=panel.querySelector('[data-native-key="paste"]');
      if(paste){paste.disabled=!result.canPaste;paste.title=result.canPaste?'粘贴文件或文件夹':'剪贴板中没有可粘贴到桌面的文件。';}
    }).catch(error=>{if(active===prior)message(error.message,true);});
  }
  const contains=target=>panels.some(panel=>panel.contains(target));
  const pointer=event=>{if(active&&!contains(event.target))close();if(editor&&!document.querySelector('.desktop-inline-rename'))editor.cancel();};
  const key=event=>{if(event.key==='Escape'){close();editor?.cancel();}};
  const blur=()=>close();
  document.addEventListener('pointerdown',pointer,true);document.addEventListener('keydown',key);globalThis.addEventListener('blur',blur);globalThis.addEventListener('resize',blur);
  return {show,close,rename,command,get renaming(){return editor!==null;},contains,dispose(){editor?.cancel();close();document.removeEventListener('pointerdown',pointer,true);document.removeEventListener('keydown',key);globalThis.removeEventListener('blur',blur);globalThis.removeEventListener('resize',blur);}};
}
