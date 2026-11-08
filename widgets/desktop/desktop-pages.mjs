import {menuIcon} from './menu-icons.mjs';

// Timers exist only for an active edge dwell, pager idle state or wheel burst.
// All geometry comes from the board projection; there is no desktop polling.
export function createDesktopPages({document,window,getModel,getDrag,blocked,navigate,restore,moveScreen,claim,focus}) {
 const make=(tag,cls,text)=>{const el=document.createElement(tag);el.className=cls;if(text)el.textContent=text;return el;};
 const pager=make('nav','desktop-pager');pager.setAttribute('aria-label','桌面页面');
 const menu=make('div','desktop-page-menu folder-menu');menu.hidden=true;menu.setAttribute('role','menu');
 const recovery=make('button','desktop-unplaced');recovery.type='button';recovery.hidden=true;
 const edges={},cues={},cueParts={},progressAnimations=new Map(),ringLength=2*Math.PI*22;
 const media=window.matchMedia?.('(prefers-reduced-motion: reduce)'),reduced=()=>media?.matches??false;
 const svgNode=(tag,cls)=>{const el=document.createElementNS('http://www.w3.org/2000/svg',tag);el.setAttribute('class',cls);return el;};
 const glyph=(root,key)=>{if(root.dataset.glyph===key)return;root.dataset.glyph=key;root.replaceChildren(menuIcon(document,{key}));};
 for(const side of ['left','right']){
  const button=make('button','desktop-page-edge '+side),glyph=make('span','page-edge-glyph'),label=make('span','page-edge-label');button.type='button';glyph.setAttribute('aria-hidden','true');button.append(glyph,label);button.tabIndex=-1;
  button.addEventListener('click',async event=>{
   const rect=button.getBoundingClientRect(),point=event.detail===0?{x:rect.left+rect.width/2,y:rect.top+rect.height/2}:{x:event.clientX,y:event.clientY};
   // Native focus acquisition may emit a transient DOM blur. Restore this
   // explicit hover after the handoff so consecutive clicks remain possible.
   await focus(button);pointer(point.x,point.y);void act(side);
  });edges[side]=button;
  const cue=make('div','desktop-page-cue '+side),ring=make('span','page-dwell-ring'),svg=svgNode('svg','page-dwell-svg'),icon=make('span','page-dwell-icon'),cueLabel=make('span','page-dwell-label');
  svg.setAttribute('viewBox','0 0 56 56');
  for(const name of ['track','progress']){const circle=svgNode('circle','page-dwell-'+name);circle.setAttribute('cx','28');circle.setAttribute('cy','28');circle.setAttribute('r','22');if(name==='progress'){circle.style.strokeDasharray=String(ringLength);circle.style.strokeDashoffset=String(ringLength);}svg.append(circle);}
  ring.append(svg,icon);cue.append(ring,cueLabel);cue.setAttribute('aria-hidden','true');cues[side]=cue;cueParts[side]={icon,label:cueLabel,progress:svg.lastElementChild};
  document.body.append(button,cue);
 }
 document.body.append(pager,menu,recovery);
 let side=null,dwell=0,dim=0,wheelTimer=0,wheelX=0,wheelUsed=false,flips=0,signature='',painted='',disposed=false,turning=false;
 const state=()=>getModel()?.board;
 const active=()=>state()?.pages?.findIndex(page=>page.id===state().pageId)??-1;
 const enabled=()=>!!(getModel()?.takeoverActive&&state()?.paged&&state()?.pages?.length);
 function brighten(){clearTimeout(dim);pager.classList.remove('dim');if(!pager.hidden)dim=setTimeout(()=>{if(!pager.matches(':hover,:focus-within')&&!getDrag()?.active)pager.classList.add('dim');},2200);}
 function closeMenu(){menu.hidden=true;}
 function sync(){
  if(disposed)return;
  const board=state(),pages=board?.pages??[],index=active(),available=enabled();
  pager.hidden=!available;recovery.hidden=!getModel()?.takeoverActive||!board?.unplaced?.length;
  recovery.textContent=board?.unplaced?.length?board.unplaced.length+' 项待放置':'';
  recovery.setAttribute('aria-label',recovery.textContent+'，打开恢复列表');
  const next=JSON.stringify([available,board?.pageId,pages,getModel()?.readOnly]);
  if(signature!==next){
   signature=next;const focused=pager.contains(document.activeElement);
   pager.replaceChildren(...pages.map((page,i)=>{
    const dot=make('button','desktop-page-dot');dot.type='button';dot.dataset.page=page.id;dot.setAttribute('aria-label',`第 ${i+1} 页，共 ${pages.length} 页，${page.count} 项`);dot.title=`第 ${i+1} 页 · ${page.count} 项`;dot.disabled=!!getModel()?.readOnly;
    if(i===index)dot.setAttribute('aria-current','page');
    dot.addEventListener('click',async()=>{await focus(dot);void go({kind:'select',pageId:page.id});});
    dot.addEventListener('contextmenu',event=>{event.preventDefault();showPageMenu(event,page);});return dot;
   }));
   if(focused)pager.querySelector('[aria-current]')?.focus({preventScroll:true});
   pager.querySelector('[aria-current]')?.scrollIntoView({block:'nearest',inline:'nearest'});brighten();
  }
  for(const name of ['left','right']){
   const button=edges[name],isNew=name==='left'?index===0:index===pages.length-1;
   button.hidden=!available||isNew;glyph(button.querySelector('.page-edge-glyph'),name==='left'?'page-previous':'page-next');
   button.querySelector('.page-edge-label').textContent=name==='left'?'上一页':'下一页';
   button.disabled=!!getModel()?.readOnly;button.setAttribute('aria-label',(name==='left'?'向左':'向右')+button.querySelector('.page-edge-label').textContent);
  }
  if(!available)cancel();else paint();
 }
 async function go(action,dragging=false){
  if(turning||disposed||getModel()?.readOnly)return false;
  if(action.kind==='select'&&action.pageId===state()?.pageId)return true;
  turning=true;closeMenu();brighten();
  try{return await navigate(action,dragging);}finally{turning=false;if(!disposed){sync();paint();}}
 }
 async function act(name){
  if(blocked()||!enabled())return;
  const index=active(),pages=state().pages,next=index+(name==='left'?-1:1);
  if(next>=0&&next<pages.length)await go({kind:'select',pageId:pages[next].id});
 }
 function showMenu(event,children){claim();closeMenu();menu.replaceChildren(...children);menu.hidden=false;menu.style.left=Math.max(6,Math.min(event.clientX,window.innerWidth-menu.offsetWidth-6))+'px';menu.style.top=Math.max(6,Math.min(event.clientY,window.innerHeight-menu.offsetHeight-6))+'px';menu.querySelector('button:not(:disabled)')?.focus();}
 function showPageMenu(event,page){
  const remove=make('button','','删除此页');remove.type='button';remove.setAttribute('role','menuitem');remove.disabled=!!getModel()?.readOnly||page.count!==0||state().pages.length===1;
  remove.addEventListener('click',()=>void go({kind:'delete',pageId:page.id}));
  const reason=make('p','page-menu-hint',page.count?`此页还有 ${page.count} 项，请先移走。`:state().pages.length===1?'至少保留一页。':'空页可直接删除。');showMenu(event,[remove,reason]);
 }
 recovery.addEventListener('click',event=>{
  const title=make('p','page-menu-hint','屏幕尺寸变小后放不下的项目仍保存在这里。'),rows=[title];
  for(const item of state()?.unplaced??[]){
   const row=make('div','unplaced-row'),name=make('strong','',item.name),place=make('button','','放到当前页');place.type='button';place.disabled=!!getModel()?.readOnly;
   place.addEventListener('click',()=>{closeMenu();void restore(item.key);});row.append(name,place);
   for(const screen of getModel().snapshot.boards.filter(screen=>screen.active&&screen.id!==state().boardId)){
    const move=make('button','','移到 '+screen.name);move.type='button';move.disabled=!!getModel()?.readOnly;move.addEventListener('click',()=>{closeMenu();void moveScreen(item.key,screen.id);});row.append(move);
   }rows.push(row);
  }showMenu(event,rows);
 });
 function zone(x,y){
  if(x<0||y<0||x>=window.innerWidth||y>=window.innerHeight)return null;
  const p=getModel()?.projection,board=state();if(!p||!board)return null;
  const left=Math.max(0,p.originX),right=Math.max(0,window.innerWidth-p.originX-board.columns*p.stepX),band=Math.abs(y-window.innerHeight/2)<=36;
  if(x<=left||band&&x<=edges.left.offsetWidth+22)return 'left';
  if(x>=window.innerWidth-right||band&&x>=window.innerWidth-edges.right.offsetWidth-22)return 'right';return null;
 }
 function paint(){
  const dragging=!!getDrag()?.active,available=enabled()&&!blocked(),nextPaint=JSON.stringify([available,side,dragging,active(),state()?.pages?.length,flips]);
  if(nextPaint===painted)return;painted=nextPaint;
  for(const name of ['left','right']){
   const shown=available&&side===name;
   edges[name].classList.toggle('on',shown&&!dragging);edges[name].tabIndex=shown&&!dragging?0:-1;edges[name].setAttribute('aria-hidden',String(!shown||dragging));
   const next=active()+(name==='left'?-1:1),allowed=next>=0&&next<(state()?.pages?.length??0);
   cues[name].classList.toggle('on',shown&&dragging);
   cueParts[name].label.textContent=allowed?(name==='left'?'上一页':'下一页'):name==='left'?'已到第一页':'已到最后一页';
   glyph(cueParts[name].icon,!allowed?'page-blocked':name==='left'?'page-previous':'page-next');
   cues[name].classList.toggle('blocked',!allowed);
   if(!shown||!dragging||!allowed)stopProgress(name);
  }
 }
 function stopProgress(name){progressAnimations.get(name)?.cancel();progressAnimations.delete(name);cueParts[name].progress.style.strokeDashoffset=String(ringLength);}
 function stopDwell(){clearTimeout(dwell);dwell=0;for(const name of ['left','right'])stopProgress(name);}
 function schedule(){
  stopDwell();
  if(!side||!getDrag()?.active||!enabled()||blocked()||turning)return;
  const next=active()+(side==='left'?-1:1);if(next<0||next>=state().pages.length)return;
  const currentSide=side,delay=flips?900:600,progress=cueParts[side].progress;
  if(!reduced()&&typeof progress.animate==='function'){
   const animation=progress.animate([{strokeDashoffset:String(ringLength)},{strokeDashoffset:'0'}],{duration:delay,easing:'linear',fill:'forwards'});
   progressAnimations.set(side,animation);animation.finished.catch(()=>{});
  }
  dwell=setTimeout(async()=>{
   dwell=0;if(disposed||side!==currentSide||!getDrag()?.active||blocked())return;
   const index=active()+(side==='left'?-1:1),page=state().pages[index];if(!page)return;
   if(await go({kind:'select',pageId:page.id},true))flips++;
   if(side===currentSide&&getDrag()?.active){paint();schedule();}
  },delay);
 }
 function pointer(x,y){
  const next=enabled()&&!blocked()?zone(x,y):null,dragging=!!getDrag()?.active;
  if(next!==side){side=next;flips=0;stopDwell();paint();schedule();}
  else {paint();if(dragging&&!dwell&&!turning)schedule();}
 }
 function cancel(){stopDwell();flips=0;side=null;paint();}
 function key(event){
  if(!enabled()||blocked()||getDrag()||!document.hasFocus()||event.ctrlKey||event.altKey||event.metaKey||event.shiftKey||event.target.closest('input,textarea,select,[contenteditable=true],[role=menu]'))return false;
  const index=active(),pages=state().pages,target=({ArrowLeft:index-1,PageUp:index-1,ArrowRight:index+1,PageDown:index+1,Home:0,End:pages.length-1})[event.key];
  if(target===undefined)return false;event.preventDefault();if(target>=0&&target<pages.length&&target!==index)void go({kind:'select',pageId:pages[target].id});return true;
 }
 const listeners=[];
 function listen(target,name,callback,options){target.addEventListener(name,callback,options);listeners.push(()=>target.removeEventListener(name,callback,options));}
 if(media)listen(media,'change',()=>{for(const name of ['left','right'])stopProgress(name);});
 listen(document,'pointermove',event=>pointer(event.clientX,event.clientY));
 listen(document,'pointerdown',event=>{if(!menu.contains(event.target))closeMenu();});
 listen(window,'blur',()=>{cancel();closeMenu();});
 listen(document,'visibilitychange',()=>{if(document.hidden){cancel();closeMenu();}});
 listen(document,'pointerout',event=>{if(!event.relatedTarget)cancel();});
 listen(pager,'pointerenter',brighten);listen(pager,'pointerleave',brighten);listen(pager,'focusin',brighten);listen(pager,'focusout',brighten);
 listen(document,'wheel',event=>{
  if(!enabled()||blocked()||getDrag()||event.target.closest('[data-tile],dialog,[role=menu],.desktop-page-menu')||Math.abs(event.deltaX)<=Math.abs(event.deltaY)*1.2)return;
  event.preventDefault();clearTimeout(wheelTimer);wheelTimer=setTimeout(()=>{wheelX=0;wheelUsed=false;},180);
  if(wheelUsed)return;wheelX+=event.deltaX*(event.deltaMode===1?16:event.deltaMode===2?window.innerWidth:1);
  if(Math.abs(wheelX)<110)return;wheelUsed=true;const index=active()+(wheelX>0?1:-1),page=state().pages[index];if(page)void go({kind:'select',pageId:page.id});
 },{passive:false});
 return {sync,key,pointer,cancel,closeMenu,get turning(){return turning;},dispose(){disposed=true;cancel();clearTimeout(dim);clearTimeout(wheelTimer);for(const remove of listeners)remove();for(const el of [pager,menu,recovery,...Object.values(edges),...Object.values(cues)])el.remove();}};
}
