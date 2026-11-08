import {thumbMetrics,offsetAt} from './scrollbar-math.mjs';

// One event-driven controller per view. No interval, extra view, or per-frame idle loop.
export function installScrollbars() {
  if(window.__magicScrollbars || !window.ResizeObserver)return;
  window.__magicScrollbars=true;
  const records=new Map(),forced=matchMedia('(forced-colors: active)');
  const host=document.createElement('div');host.className='magic-scrollbar-layer';
  let frame=0,needsScan=true,drag=null;
  const hidden=()=>document.documentElement.hasAttribute('data-widget-hidden');
  const schedule=(scan=false)=>{
    needsScan ||= scan;
    if(!frame&&!hidden())frame=requestAnimationFrame(update);
  };
  const ro=new ResizeObserver(()=>schedule(true));
  function discard(record){
    clearTimeout(record.timer);
    for(const target of record.observed)ro.unobserve(target);
    for(const axis of record.axes)axis.track.remove();
    record.el.removeAttribute('data-magic-scrollbar');records.delete(record.el);
    if(drag?.record===record)drag=null;
  }
  function reveal(record){
    clearTimeout(record.timer);record.active=true;
    for(const axis of record.axes)axis.track.classList.add('is-visible');
    record.timer=setTimeout(()=>{
      record.active=false;
      if(!record.hover&&drag?.record!==record)
        for(const axis of record.axes)axis.track.classList.remove('is-visible');
    },1100);
  }
  function makeAxis(record,horizontal){
    const track=document.createElement('div'),thumb=document.createElement('div');
    track.className='magic-scrollbar-track '+(horizontal?'is-horizontal':'is-vertical');
    thumb.className='magic-scrollbar-thumb';track.append(thumb);host.append(track);
    // Native scrolling/keyboard/accessibility stays on the original scroll container.
    track.setAttribute('aria-hidden','true');
    const axis={track,thumb,horizontal,metrics:null,start:0};
    const getOffset=()=>horizontal?Math.abs(record.el.scrollLeft):record.el.scrollTop;
    const setOffset=value=>{
      if(horizontal)record.el.scrollLeft=(getComputedStyle(record.el).direction==='rtl'?-1:1)*value;
      else record.el.scrollTop=value;
      schedule();
    };
    track.addEventListener('pointerdown',event=>{
      if(event.button!==0||!axis.metrics)return;
      event.preventDefault();event.stopPropagation();reveal(record);
      const point=horizontal?event.clientX:event.clientY;
      const inside=event.target===thumb;
      drag={record,axis,id:event.pointerId,grab:inside?point-axis.start-axis.metrics.position:axis.metrics.length/2};
      track.classList.add('is-dragging');track.setPointerCapture(event.pointerId);
      if(!inside)setOffset(offsetAt(point-axis.start-drag.grab,axis.metrics));
    });
    track.addEventListener('pointermove',event=>{
      if(drag?.axis!==axis||drag.id!==event.pointerId||!axis.metrics)return;
      setOffset(offsetAt((horizontal?event.clientX:event.clientY)-axis.start-drag.grab,axis.metrics));
    });
    const finish=()=>{if(drag?.axis===axis){drag=null;track.classList.remove('is-dragging');reveal(record);}};
    track.addEventListener('pointerup',finish);track.addEventListener('pointercancel',finish);track.addEventListener('lostpointercapture',finish);
    track.addEventListener('pointerenter',()=>{record.hover=true;reveal(record);});
    track.addEventListener('pointerleave',()=>{record.hover=record.el.matches(':hover');if(!record.hover)reveal(record);});
    track.addEventListener('wheel',event=>{
      const factor=event.deltaMode===1?16:event.deltaMode===2?(horizontal?record.el.clientWidth:record.el.clientHeight):1;
      const delta=(horizontal?(event.deltaX||event.deltaY):event.deltaY)*factor;
      if(delta&&axis.metrics?.range){event.preventDefault();setOffset(getOffset()+delta);reveal(record);}
    },{passive:false});
    return axis;
  }
  function register(el){
    const record={el,axes:[],observed:[],hover:false,active:false,timer:0};
    record.axes=[makeAxis(record,false),makeAxis(record,true)];records.set(el,record);
    // Hide native gutters only after the overlay implementation is attached.
    el.setAttribute('data-magic-scrollbar','');
    record.observed=[el,...el.children];for(const target of record.observed)ro.observe(target);
  }
  function clippedBounds(el){
    const rect=el.getBoundingClientRect();
    let top=Math.max(0,rect.top),bottom=Math.min(innerHeight,rect.bottom),left=Math.max(0,rect.left),right=Math.min(innerWidth,rect.right);
    for(let parent=el.parentElement;parent;parent=parent.parentElement){
      const style=getComputedStyle(parent),bounds=parent.getBoundingClientRect();
      if(/hidden|clip|auto|scroll/.test(style.overflowY)){top=Math.max(top,bounds.top);bottom=Math.min(bottom,bounds.bottom);}
      if(/hidden|clip|auto|scroll/.test(style.overflowX)){left=Math.max(left,bounds.left);right=Math.min(right,bounds.right);}
    }
    return {top,bottom,left,right,width:right-left,height:bottom-top};
  }
  function update(){
    frame=0;if(hidden())return;
    if(!host.isConnected)document.body.append(host);
    if(forced.matches){for(const record of [...records.values()])discard(record);needsScan=true;return;}
    if(needsScan){
      needsScan=false;
      for(const el of document.body.querySelectorAll('*')){
        if(host.contains(el)||records.has(el)||el.clientHeight===0||el.clientWidth===0)continue;
        const style=getComputedStyle(el);
        if((/auto|scroll/.test(style.overflowY)&&el.scrollHeight>el.clientHeight+1)||(/auto|scroll/.test(style.overflowX)&&el.scrollWidth>el.clientWidth+1))register(el);
      }
    }
    for(const record of [...records.values()]){
      const {el}=record;
      if(!el.isConnected){discard(record);continue;}
      const bounds=clippedBounds(el),style=getComputedStyle(el);
      const y=/auto|scroll/.test(style.overflowY)&&el.scrollHeight>el.clientHeight+1;
      const x=/auto|scroll/.test(style.overflowX)&&el.scrollWidth>el.clientWidth+1;
      for(const axis of record.axes){
        const {track,thumb,horizontal}=axis;
        const enabled=(horizontal?x:y)&&bounds.height>24&&bounds.width>24&&el.getClientRects().length>0;
        track.hidden=!enabled;if(!enabled)continue;
        const length=Math.max(0,(horizontal?bounds.width:bounds.height)-12-((horizontal?y:x)?12:0));
        axis.metrics=thumbMetrics(horizontal?el.clientWidth:el.clientHeight,horizontal?el.scrollWidth:el.scrollHeight,length,horizontal?Math.abs(el.scrollLeft):el.scrollTop);
        axis.start=(horizontal?bounds.left:bounds.top)+6;
        track.style.cssText=horizontal?`left:${axis.start}px;top:${bounds.bottom-14}px;width:${length}px;`:`left:${bounds.right-14}px;top:${axis.start}px;height:${length}px;`;
        thumb.style.cssText=horizontal?`width:${axis.metrics.length}px;transform:translateX(${axis.metrics.position}px);`:`height:${axis.metrics.length}px;transform:translateY(${axis.metrics.position}px);`;
        track.classList.toggle('is-visible',record.hover||record.active||drag?.record===record);
      }
    }
  }
  function start(){
    document.body.append(host);
    const mutations=new MutationObserver(entries=>{
      const relevant=entries.some(entry=>{
        if(host.contains(entry.target)||entry.target===host)return false;
        if(entry.type==='attributes')return entry.oldValue!==entry.target.getAttribute(entry.attributeName);
        // Clock/status text changes outside a scroll container must stay idle.
        const elementChange=[...entry.addedNodes,...entry.removedNodes].some(node=>node.nodeType===1);
        return elementChange||(entry.target instanceof Element&&!!entry.target.closest('[data-magic-scrollbar]'));
      });
      if(relevant)schedule(true);
    });
    mutations.observe(document.body,{subtree:true,childList:true,attributes:true,attributeOldValue:true,attributeFilter:['class','style','hidden','open']});
    const visibility=new MutationObserver(()=>{
      if(hidden()){
        cancelAnimationFrame(frame);frame=0;
        for(const record of records.values()){clearTimeout(record.timer);record.hover=false;record.active=false;for(const axis of record.axes)axis.track.classList.remove('is-visible','is-dragging');}
        drag=null;
      }else schedule(true);
    });
    visibility.observe(document.documentElement,{attributes:true,attributeFilter:['data-widget-hidden']});
    document.addEventListener('scroll',event=>{const record=records.get(event.target);if(record)reveal(record);schedule(!record);},{capture:true,passive:true});
    document.addEventListener('input',()=>schedule(true),{capture:true,passive:true});
    document.addEventListener('pointerover',event=>{
      for(const record of records.values())if(record.el.contains(event.target)){record.hover=true;reveal(record);}
      schedule();
    },{passive:true});
    document.addEventListener('pointerout',event=>{
      for(const record of records.values())if(record.hover&&!record.el.contains(event.relatedTarget)&&!host.contains(event.relatedTarget)){record.hover=false;if(!record.active)for(const axis of record.axes)axis.track.classList.remove('is-visible');}
    },{passive:true});
    document.addEventListener('load',()=>schedule(true),true);
    window.addEventListener('resize',()=>schedule(true),{passive:true});
    forced.addEventListener('change',()=>schedule(true));
    window.addEventListener('pagehide',()=>{mutations.disconnect();visibility.disconnect();ro.disconnect();for(const record of [...records.values()])discard(record);cancelAnimationFrame(frame);},{once:true});
    schedule(true);
  }
  if(document.body)start();else document.addEventListener('DOMContentLoaded',start,{once:true});
}
