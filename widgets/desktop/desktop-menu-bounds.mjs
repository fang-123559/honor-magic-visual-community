// The desktop HWND already covers the Windows work area. Intersect it with
// Seelen's configured bar strips too: an auto-hiding Dock reserves no AppBar
// work area, but can still cover a menu when the pointer reaches that edge.
export function desktopMenuBounds({width,height,frame,settings,monitorId}) {
  const fallback={left:0,top:0,right:width,bottom:height};
  if(!frame?.monitorRect||!frame.rect||!settings)return fallback;
  const safe={...frame.monitorRect}, scale=frame.scale;
  const config=id=>{
    const value=settings.byWidget?.[id];
    return value?.enabled&&settings.monitorsV3?.[monitorId]?.byWidget?.[id]?.enabled!==false?value:null;
  };
  const inset=(side,size)=>{
    if(!Number.isFinite(size)||size<=0)return;
    if(side==='Top')safe.top+=size;
    else if(side==='Bottom')safe.bottom-=size;
    else if(side==='Left')safe.left+=size;
    else if(side==='Right')safe.right-=size;
  };
  const toolbar=config('@seelen/fancy-toolbar');
  if(toolbar)inset(toolbar.position,Math.round((toolbar.itemSize+toolbar.padding*2+toolbar.margin*2)*scale));
  const dock=config('@seelen/weg');
  if(dock)inset(dock.position,Math.round((dock.size+dock.padding*2+dock.margin*2)*scale));
  const rect=frame.rect,sx=width/(rect.right-rect.left),sy=height/(rect.bottom-rect.top);
  const result={left:Math.max(0,(safe.left-rect.left)*sx),top:Math.max(0,(safe.top-rect.top)*sy),right:Math.min(width,(safe.right-rect.left)*sx),bottom:Math.min(height,(safe.bottom-rect.top)*sy)};
  return result.right>result.left+12&&result.bottom>result.top+12?result:fallback;
}
