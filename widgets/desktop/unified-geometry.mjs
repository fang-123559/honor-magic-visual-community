export function frame(rect,p) {
  return {left:p.originX+rect.x*p.stepX,top:p.originY+rect.y*p.stepY,width:rect.w*p.stepX-p.inset,height:rect.h*p.stepY-p.inset};
}
export function position(x,y,offset,p,scrollX=0,scrollY=0) {
  return {x:Math.round((x-p.originX+scrollX-offset.x)/p.stepX),y:Math.round((y-p.originY+scrollY-offset.y)/p.stepY)};
}
export function placementError(rect,board,ignore) {
  if(rect.x<0||rect.y<0||rect.x+rect.w>board.columns||rect.y+rect.h>board.rows)return '目标超出网格，原位置已保留。';
  for(const [key,other] of Object.entries(board.placements))if(key!==ignore&&rect.x<other.x+other.w&&rect.x+rect.w>other.x&&rect.y<other.y+other.h&&rect.y+rect.h>other.y)return '目标网格已被占用，原位置已保留。';
  return null;
}
