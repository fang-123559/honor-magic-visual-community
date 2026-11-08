// A short-lived editor at the existing label; no dialog, backdrop or timer.
export function inlineRename({document,anchor,value,isFolder=false,ended=()=>{}}){
  if(!anchor?.isConnected)return {result:Promise.resolve(null),cancel(){}};
  const label=anchor.querySelector('.app-name,.name'),host=anchor.closest('dialog[open]')||document.body;
  const input=document.createElement('textarea');input.className='desktop-inline-rename';input.rows=2;input.value=value;input.setAttribute('aria-label','新名称');input.spellcheck=false;
  const style=getComputedStyle(label||anchor);input.style.font=style.font;input.style.lineHeight=style.lineHeight;input.style.position=host===document.body?'fixed':'absolute';
  let finish,done=false;const result=new Promise(resolve=>{finish=(answer,restoreFocus=false)=>{
    if(done)return;done=true;document.removeEventListener('pointerdown',outside,true);globalThis.removeEventListener('resize',position);document.removeEventListener('scroll',position,true);input.remove();if(label)label.style.visibility='';if(restoreFocus&&anchor.isConnected)anchor.focus({preventScroll:true});resolve(answer);ended();
  };});
  function position(){
    if(!anchor.isConnected){finish(null);return;}
    const r=(label||anchor).getBoundingClientRect(),a=anchor.getBoundingClientRect(),h=host.getBoundingClientRect();
    const width=Math.min(Math.max(a.width-4,88),Math.max(44,innerWidth-12));
    const x=Math.max(6,Math.min(r.left+r.width/2-width/2,innerWidth-width-6)),y=Math.max(6,Math.min(label?r.top-2:a.bottom-3,innerHeight-64));
    input.style.width=width+'px';input.style.left=(host===document.body?x:x-h.left-host.clientLeft+host.scrollLeft)+'px';input.style.top=(host===document.body?y:y-h.top-host.clientTop+host.scrollTop)+'px';
  }
  const outside=event=>{if(event.target!==input)finish(input.value.trim());};
  input.addEventListener('pointerdown',event=>event.stopPropagation());input.addEventListener('contextmenu',event=>event.stopPropagation());
  input.addEventListener('keydown',event=>{
    event.stopPropagation();if(event.isComposing||event.keyCode===229)return;
    if(event.key==='Enter'){event.preventDefault();finish(input.value.trim(),true);}else if(event.key==='Escape'){event.preventDefault();finish(null,true);}
  });
  input.addEventListener('blur',()=>finish(input.value.trim()));
  if(label)label.style.visibility='hidden';host.append(input);position();document.addEventListener('pointerdown',outside,true);globalThis.addEventListener('resize',position);document.addEventListener('scroll',position,true);
  input.focus({preventScroll:true});const dot=value.lastIndexOf('.');input.setSelectionRange(0,!isFolder&&dot>0?dot:value.length);
  return {result,cancel:()=>finish(null)};
}
