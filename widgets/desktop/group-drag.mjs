import {placementError} from './unified-geometry.mjs';
export function dragMembers(value){return value.itemIds?.length?value.itemIds:[value.itemId];}
export function groupOffsets(value,board){
 const ids=dragMembers(value);if(ids.length===1)return [{dx:0,dy:0,w:1,h:1}];
 if(value.sourceFolder){const columns=Math.min(4,board.columns,Math.ceil(Math.sqrt(ids.length))),anchor=ids.indexOf(value.itemId);return ids.map((id,i)=>({dx:i%columns-anchor%columns,dy:Math.floor(i/columns)-Math.floor(anchor/columns),w:1,h:1}));}
 const anchor=board.placements['item:'+value.itemId];return ids.map(id=>{const r=board.placements['item:'+id];return {dx:r.x-anchor.x,dy:r.y-anchor.y,w:r.w,h:r.h};});
}
export function groupPlacement(anchor,offsets,board,exclude=[]){
 if(!offsets?.length||offsets.length>256||![anchor.x,anchor.y].every(Number.isSafeInteger)||!offsets.every(r=>[r.dx,r.dy,r.w,r.h].every(Number.isSafeInteger)&&r.w>0&&r.h>0))return {error:'拖动集合已变化，请重新选择。',rects:[],rect:{x:0,y:0,w:1,h:1}};
 const free={...board,placements:Object.fromEntries(Object.entries(board.placements).filter(([key])=>!exclude.includes(key)))};
 const rects=offsets.map(r=>({x:anchor.x+r.dx,y:anchor.y+r.dy,w:r.w,h:r.h}));
 const error=rects.map(rect=>placementError(rect,free,null)).find(Boolean)||null;
 const x=Math.min(...rects.map(r=>r.x)),y=Math.min(...rects.map(r=>r.y));return {rects,error,rect:{x,y,w:Math.max(...rects.map(r=>r.x+r.w))-x,h:Math.max(...rects.map(r=>r.y+r.h))-y}};
}
// Same distance and row/column tie-break as DesktopPages.Drop. Called only
// while dragging between pages; a failed fit keeps the source untouched.
export function nearestGroupPlacement(anchor,offsets,board,exclude=[]){
 let best=null,distance=Infinity;
 for(let y=0;y<board.rows;y++)for(let x=0;x<board.columns;x++){
  const next=(x-anchor.x)**2+(y-anchor.y)**2;if(next>=distance)continue;
  const placed=groupPlacement({x,y},offsets,board,exclude);
  if(!placed.error){best=placed;distance=next;if(next===0)return best;}
 }
 return best??groupPlacement(anchor,offsets,board,exclude);
}
