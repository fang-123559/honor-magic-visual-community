// Animate the existing current-page layer, as folder-motion does for dialogs.
// The commit never waits for an animation; no extra pages or card hosts mount.
export function desktopPageMotion(grid, reduced = () => globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false) {
 const document=grid.ownerDocument,window=document.defaultView,media=window.matchMedia?.('(prefers-reduced-motion: reduce)');
 let animation=null,generation=0,disposed=false;
 function cancel(){generation++;animation?.cancel();animation=null;}
 const interrupt=()=>cancel();
 for(const name of ['pointerdown','keydown'])document.addEventListener(name,interrupt,true);
 document.addEventListener('visibilitychange',interrupt);
 for(const name of ['blur','resize'])window.addEventListener(name,interrupt);
 media?.addEventListener('change',interrupt);
 return {
  async run(direction,commit,{dragging=false}={}){
   cancel();const current=generation;
   const changed=await commit();
   if(!changed||disposed||current!==generation||reduced()||typeof grid.animate!=='function')return changed;
   // During a drag, geometry must stay stationary for hit-testing and the
   // projected drop preview. Opacity gives feedback without shifting targets.
   // One grid-step of travel and the prototype's timing make the direction
   // legible while keeping the existing single-page mount and immediate commit.
   const shift=dragging?0:Math.sign(direction)*96;
   const value=grid.animate([{opacity:.55,transform:`translateX(${shift}px)`},{opacity:1,transform:'translateX(0px)'}],{duration:300,easing:'cubic-bezier(.22,.61,.36,1)'});
   animation=value;
   value.finished.then(()=>{if(animation===value){value.cancel();animation=null;}},()=>{});
   return changed;
  },
  cancel,
  dispose(){disposed=true;cancel();for(const name of ['pointerdown','keydown'])document.removeEventListener(name,interrupt,true);document.removeEventListener('visibilitychange',interrupt);for(const name of ['blur','resize'])window.removeEventListener(name,interrupt);media?.removeEventListener('change',interrupt);},
 };
}
