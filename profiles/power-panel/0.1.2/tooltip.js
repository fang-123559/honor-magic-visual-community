
if(!batteries.length)return "未检测到电池";
const values=batteries.map(b=>b.percentage).filter(v=>Number.isFinite(v)&&v>=0&&v<=100);
const percent=values.length?Math.round(values.reduce((a,b)=>a+b,0)/values.length):"—";
const charging=batteries.some(b=>b.state==="charging");
return (charging?"充电中":"剩余")+" · "+percent+"%\nWindows 状态："+powerMode+"\n点击查看电量与性能";
