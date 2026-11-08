// Selection is view-local and event-driven. Nothing is persisted or polled.
export function selectionState() {
  let scope = null, ids = new Set(), primary = null;
  const replace = (nextScope, values) => { scope=nextScope;ids=new Set(values);primary=[...ids].at(-1)??null; };
  return {
    get scope(){return scope;}, get ids(){return [...ids];}, get size(){return ids.size;}, get primary(){return primary;},
    has(id,atScope=scope){return scope===atScope&&ids.has(id);},
    click(id,atScope,additive=false){
      if(!additive||scope!==atScope)replace(atScope,[id]);
      else if(ids.has(id)){ids.delete(id);primary=[...ids].at(-1)??null;}
      else{ids.add(id);primary=id;}
    },
    context(id,atScope){if(!this.has(id,atScope))replace(atScope,[id]);},
    replace,
    clear(){replace(null,[]);},
    prune(valid){const allowed=new Set(valid);replace(scope,[...ids].filter(id=>allowed.has(id)));},
    snapshot(){return {scope,ids:[...ids]};},
    restore(saved){replace(saved.scope,saved.ids);}
  };
}
export function selectionRect(a,b){return {left:Math.min(a.x,b.x),top:Math.min(a.y,b.y),right:Math.max(a.x,b.x),bottom:Math.max(a.y,b.y)};}
export function intersectingItems(rect,boxes){return boxes.filter(box=>box.right>rect.left&&box.left<rect.right&&box.bottom>rect.top&&box.top<rect.bottom).map(box=>box.id);}

// Bounds are read once at drag start, then hit-tested in scrolled content space.
// Only a live marquee owns an animation frame; it is removed on all exits.
export function installMarquee({document,window,viewport,grid,dialog,state,paint,closeMenus,blocked,focus,finished}){
  let drag=null,rectangle=null,frame=0,point=null,disposed=false;
  const contentPoint=e=>({x:e.clientX+viewport.scrollLeft,y:e.clientY+viewport.scrollTop});
  const update=()=>{
    frame=0;if(!drag||!point)return;
    const rect=selectionRect(drag.start,contentPoint(point));
    if(!drag.active){if(Math.hypot(rect.right-rect.left,rect.bottom-rect.top)<4)return;drag.active=true;rectangle=document.createElement('div');rectangle.className='desktop-selection-rect';rectangle.setAttribute('aria-hidden','true');document.body.append(rectangle);}
    rectangle.style.left=(rect.left-viewport.scrollLeft)+'px';rectangle.style.top=(rect.top-viewport.scrollTop)+'px';rectangle.style.width=(rect.right-rect.left)+'px';rectangle.style.height=(rect.bottom-rect.top)+'px';
    state.replace('desktop',[...drag.base,...intersectingItems(rect,drag.boxes)]);paint();
  };
  function end(cancel=false){
    const prior=drag;if(!prior)return;
    if(!cancel)update();drag=null;point=null;if(frame)window.cancelAnimationFrame(frame);frame=0;rectangle?.remove();rectangle=null;
    if(viewport.hasPointerCapture?.(prior.pointerId))viewport.releasePointerCapture(prior.pointerId);
    if(cancel){state.restore(prior.original);paint();}finished(prior.active,cancel);if(!cancel)void focus(viewport);
  }
  const down=e=>{
    if(disposed||e.button!==0||dialog.open||blocked()||e.target.closest('[data-tile],button,input,textarea,select,[role="menu"],[data-inline-rename]'))return;
    closeMenus();const original=state.snapshot(),base=e.ctrlKey&&state.scope==='desktop'?state.ids:[];
    if(!e.ctrlKey){state.clear();paint();}
    const boxes=[...grid.querySelectorAll(':scope > [data-item]')].map(el=>{const r=el.getBoundingClientRect();return {id:el.dataset.item,left:r.left+viewport.scrollLeft,top:r.top+viewport.scrollTop,right:r.right+viewport.scrollLeft,bottom:r.bottom+viewport.scrollTop};});
    drag={pointerId:e.pointerId,start:contentPoint(e),boxes,base,original,active:false};point=e;viewport.setPointerCapture?.(e.pointerId);e.preventDefault();e.stopPropagation();
  };
  const move=e=>{if(!drag||e.pointerId!==drag.pointerId)return;if(!(e.buttons&1)){end(true);return;}point=e;e.preventDefault();if(!frame)frame=window.requestAnimationFrame(update);};
  const up=e=>{if(drag?.pointerId===e.pointerId){point=e;e.preventDefault();end();}};
  const cancel=e=>{if(drag?.pointerId===e.pointerId)end(true);};
  const escape=e=>{if(e.key==='Escape'&&drag){e.preventDefault();end(true);}};
  const blur=()=>end(true);
  const scroll=()=>{if(drag&&!frame)frame=window.requestAnimationFrame(update);};
  viewport.tabIndex=-1;viewport.addEventListener('pointerdown',down);document.addEventListener('pointermove',move,{passive:false});document.addEventListener('pointerup',up);document.addEventListener('pointercancel',cancel);viewport.addEventListener('lostpointercapture',cancel);document.addEventListener('keydown',escape);window.addEventListener('blur',blur);viewport.addEventListener('scroll',scroll,{passive:true});
  return {get active(){return drag!==null;},cancel:()=>end(true),dispose(){disposed=true;end(true);viewport.removeEventListener('pointerdown',down);document.removeEventListener('pointermove',move);document.removeEventListener('pointerup',up);document.removeEventListener('pointercancel',cancel);viewport.removeEventListener('lostpointercapture',cancel);document.removeEventListener('keydown',escape);window.removeEventListener('blur',blur);viewport.removeEventListener('scroll',scroll);}};
}
