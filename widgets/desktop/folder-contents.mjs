// A bounded, view-local cache. Closed dialogs retain their last materialized
// items; scroll/resize are the only sources of virtualization work. No timers.
export function createFolderContents({dialog,host,item,create,patch,focused=()=>{},blocked=()=>false}) {
  const document=host.ownerDocument,window=document.defaultView;
  const maximumFolders=2,maximumNodes=256,materializedLimit=96,rowHeight=112,gap=15;
  const entries=new Map();let current=null,shown=false,frame=0,columns=5,width=0,disposed=false;
  const stats={created:0,patched:0,reused:0,evicted:0};
  host.setAttribute('role','listbox');host.setAttribute('aria-label','文件夹项目');host.setAttribute('aria-multiselectable','true');
  function evict() {
    let count=[...entries.values()].reduce((sum,entry)=>sum+entry.nodes.size,0);
    for(const [id,entry] of entries){
      if(entries.size<=maximumFolders&&count<=maximumNodes)break;
      if(entry===current)continue;
      entries.delete(id);count-=entry.nodes.size;stats.evicted+=entry.nodes.size;entry.nodes.clear();
    }
  }
  function dimensions() {
    const next=host.clientWidth||width||512;
    width=next;columns=Math.max(1,Math.floor((next+gap)/(85+gap)));
  }
  function trim(entry,visible) {
    // A large folder keeps at most one materialized window, including overscan.
    // Small folders keep their complete view within the same hard bound.
    for(const [id,node] of entry.nodes)if(!visible.has(id)){node.remove();entry.nodes.delete(id);stats.evicted++;}
  }
  function render() {
    if(!current||disposed||blocked())return;
    const entry=current,ids=entry.ids,virtual=ids.length>materializedLimit;
    let first=0,last=ids.length;
    if(virtual){
      const rows=Math.ceil(ids.length/columns),stride=rowHeight+gap;
      host.dataset.virtual='true';host.style.height=Math.max(0,rows*stride-gap)+'px';
      const top=Math.max(0,dialog.scrollTop-host.offsetTop);
      const startRow=Math.max(0,Math.floor(top/stride)-1);
      const desiredRows=Math.ceil((dialog.clientHeight||600)/stride)+3;
      const rowLimit=Math.max(1,Math.floor(Math.min(maximumNodes,Math.max(materializedLimit,desiredRows*columns))/columns));
      first=Math.min(Math.max(0,rows-1),startRow)*columns;
      last=Math.min(ids.length,first+Math.min(rowLimit,desiredRows)*columns);
    }else{delete host.dataset.virtual;host.style.height='';}
    const wanted=new Set(ids.slice(first,last));trim(entry,wanted);
    // Remove the prior folder's children without discarding its bounded cache.
    for(const child of [...host.children])if(!wanted.has(child.dataset.item)||entry.nodes.get(child.dataset.item)!==child)child.remove();
    let cursor=host.firstElementChild;
    for(let index=first;index<last;index++){
      const id=ids[index],value=item(id);if(!value)continue;
      let node=entry.nodes.get(id);
      if(!node){node=create(id,entry.id);entry.nodes.set(id,node);stats.created++;}else stats.reused++;
      patch(node,value,entry.id);stats.patched++;
      node.setAttribute('role','option');node.setAttribute('aria-posinset',String(index+1));node.setAttribute('aria-setsize',String(ids.length));
      if(virtual){
        const cellWidth=(width-gap*(columns-1))/columns;
        node.style.position='absolute';node.style.left=(index%columns)*(cellWidth+gap)+'px';node.style.top=Math.floor(index/columns)*(rowHeight+gap)+'px';node.style.width=cellWidth+'px';node.style.height=rowHeight+'px';
      }else for(const name of ['position','left','top','width','height'])node.style[name]='';
      if(node===cursor)cursor=cursor.nextElementSibling;else host.insertBefore(node,cursor);
    }
    entry.first=first;entry.last=last;evict();
  }
  function schedule(){if(shown&&!disposed&&!blocked()&&!frame)frame=window.requestAnimationFrame(()=>{frame=0;render();});}
  const scroll=()=>{if(shown){if(current)current.scroll=dialog.scrollTop;schedule();}};
  const resize=()=>{if(shown&&!blocked()){dimensions();schedule();}};
  const observer=new window.ResizeObserver(resize);observer.observe(host);
  dialog.addEventListener('scroll',scroll,{passive:true});
  function focusIndex(index,choose=true){
    if(!shown||!current||blocked()||index<0||index>=current.ids.length)return;
    if(current.ids.length>materializedLimit){
      const top=host.offsetTop+Math.floor(index/columns)*(rowHeight+gap);
      if(top<dialog.scrollTop)dialog.scrollTop=top;
      else if(top+rowHeight>dialog.scrollTop+dialog.clientHeight)dialog.scrollTop=top+rowHeight-dialog.clientHeight;
    }
    if(choose)focused(current.ids[index],current.id);render();const node=current.nodes.get(current.ids[index]);node?.focus({preventScroll:true});
    if(current.ids.length<=materializedLimit)node?.scrollIntoView({block:'nearest'});
  }
  const key=event=>{
    if(!shown||blocked()||event.defaultPrevented||event.ctrlKey||event.altKey||event.isComposing)return;
    const node=event.target.closest('[data-item]');if(!node||!host.contains(node))return;
    const index=current.ids.indexOf(node.dataset.item),steps={ArrowLeft:-1,ArrowRight:1,ArrowUp:-columns,ArrowDown:columns,PageUp:-columns*4,PageDown:columns*4};
    let next=event.key==='Home'?0:event.key==='End'?current.ids.length-1:event.key in steps?index+steps[event.key]:null;
    // Keep sequential keyboard access across virtual windows. The first/last
    // item still lets Tab leave the list for the dialog's other controls.
    if(event.key==='Tab'){const candidate=index+(event.shiftKey?-1:1);if(candidate>=0&&candidate<current.ids.length)next=candidate;}
    if(next!==null){event.preventDefault();focusIndex(Math.max(0,Math.min(current.ids.length-1,next)));}
  };
  host.addEventListener('keydown',key);
  return {
    update(folder){
      const switched=current?.id!==folder.id;
      if(switched&&current&&shown&&dialog.open)current.scroll=dialog.scrollTop;
      let entry=entries.get(folder.id);
      if(!entry)entry={id:folder.id,nodes:new Map(),ids:[],scroll:0};
      entries.delete(folder.id);entries.set(folder.id,entry);current=entry;
      const ids=folder.itemIds.filter(id=>item(id)),valid=new Set(ids);
      for(const [id,node] of entry.nodes)if(!valid.has(id)){node.remove();entry.nodes.delete(id);stats.evicted++;}
      entry.ids=ids;dimensions();render();
      if(switched&&shown){dialog.scrollTop=entry.scroll;render();}
      evict();
    },
    reveal(){shown=true;dimensions();if(current)dialog.scrollTop=current.scroll;render();},
    hide(){if(current&&dialog.open)current.scroll=dialog.scrollTop;shown=false;if(frame)window.cancelAnimationFrame(frame);frame=0;},
    prune(folders){
      const valid=new Set(folders.map(folder=>folder.id));
      for(const [id,entry] of entries)if(!valid.has(id)){if(current===entry)current=null;for(const node of entry.nodes.values())node.remove();entries.delete(id);entry.nodes.clear();}
    },
    anchor(id){if(!current)return null;const index=current.ids.indexOf(id);if(index<0)return null;focusIndex(index,false);return current.nodes.get(id)??null;},
    get snapshot(){return {...stats,folders:entries.size,retainedNodes:[...entries.values()].reduce((sum,entry)=>sum+entry.nodes.size,0),materializedNodes:host.childElementCount,virtual:host.dataset.virtual==='true'};},
    dispose(){disposed=true;shown=false;if(frame)window.cancelAnimationFrame(frame);frame=0;observer.disconnect();dialog.removeEventListener('scroll',scroll);host.removeEventListener('keydown',key);host.replaceChildren();entries.clear();current=null;}
  };
}
