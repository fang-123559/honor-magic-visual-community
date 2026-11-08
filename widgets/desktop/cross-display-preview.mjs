import {position,placementError} from './unified-geometry.mjs';

// Pure target-side projection. No mouse hook, DOM measurement, IPC or file IO.
export function remotePlacement(data,frame,projection,scroll,board) {
  if(data.type!=='preview'||!data.point||!data.offset||!data.span)return null;
  const values=[data.point.x,data.point.y,data.offset.x,data.offset.y,data.span.w,data.span.h];
  const size=value=>data.kind==='card'?Number.isInteger(value)&&value>=1&&value<=8:[1,2,4].includes(value);
  if(!values.every(Number.isFinite)||!size(data.span.w)||!size(data.span.h)||data.offset.x<0||data.offset.y<0||data.offset.x>4096||data.offset.y>4096)return null;
  const x=(data.point.x-frame.rect.left)/frame.scale,y=(data.point.y-frame.rect.top)/frame.scale;
  if(x<0||y<0||data.point.x>=frame.rect.right||data.point.y>=frame.rect.bottom)return null;
  const gx=(x+scroll.x-projection.originX)/projection.stepX,gy=(y+scroll.y-projection.originY)/projection.stepY;
  if(data.kind==='item')for(const folder of board.folders){
    const r=board.placements['folder:'+folder.id];
    if(gx>=r.x&&gy>=r.y&&gx<r.x+r.w-projection.inset/projection.stepX&&gy<r.y+r.h-projection.inset/projection.stepY)return {rect:r,valid:true,folderId:folder.id};
  }
  const rect={...position(x,y,data.offset,projection,scroll.x,scroll.y),...data.span};
  return {rect,valid:!placementError(rect,board,null),folderId:null};
}

export function crossPreviewChannel({sessionId,monitorId,receive,diagnostic,create=name=>new BroadcastChannel(name)}) {
  let channel,confirmed=false,closed=false;
  const confirm=()=>{if(!confirmed){confirmed=true;diagnostic('enter');}};
  const publish=data=>{if(!closed&&channel)channel.postMessage({...data,sessionId,source:monitorId});};
  try {
    channel=create('magic-desktop-preview-'+sessionId);
    channel.onmessage=({data})=>{
      if(!data||data.sessionId!==sessionId||typeof data.source!=='string'||data.source===monitorId)return;
      if(data.type==='hello'){confirm();publish({type:'ack',to:data.source});}
      else if(data.type==='ack'){if(data.to===monitorId)confirm();}
      else if(data.type==='preview'||data.type==='clear')receive(data);
    };
    publish({type:'hello'});
  } catch {diagnostic('unavailable');}
  return {publish,close(){if(closed)return;closed=true;channel?.close();}};
}
