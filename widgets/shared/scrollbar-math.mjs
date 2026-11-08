export function thumbMetrics(viewport, extent, track, offset, minimum=32) {
  const range=Math.max(0,extent-viewport);
  const length=Math.min(track,Math.max(minimum,extent>0?track*viewport/extent:track));
  const travel=Math.max(0,track-length);
  return {range,length,travel,position:range>0?Math.max(0,Math.min(range,offset))/range*travel:0};
}
export function offsetAt(position, metrics) {
  return metrics.travel>0?Math.max(0,Math.min(metrics.travel,position))/metrics.travel*metrics.range:0;
}
