// New items use the menu's original desktop point, not the submenu click.
// Keep the complete footprint in the visible viewport; occupancy is decided
// again against authoritative state on the backend.
export function creationPosition(point, p, board, width, height, scrollX=0, scrollY=0, w=1, h=1) {
  if(!point||!Number.isFinite(point.x)||!Number.isFinite(point.y))return {};
  const axis=(client,origin,step,scroll,viewport,cells,size)=>{
    const min=Math.max(0,Math.ceil((scroll-origin)/step));
    const max=Math.min(cells-size,Math.floor((scroll+viewport-origin+p.inset)/step)-size);
    return max<min?null:Math.max(min,Math.min(max,Math.round((client-origin+scroll)/step)));
  };
  const x=axis(point.x,p.originX,p.stepX,scrollX,width,board.columns,w);
  const y=axis(point.y,p.originY,p.stepY,scrollY,height,board.rows,h);
  return x===null||y===null?{}:{x,y};
}
