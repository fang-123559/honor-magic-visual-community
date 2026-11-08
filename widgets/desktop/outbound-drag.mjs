// Probe only during an actual pointer drag. No idle timer or global mouse hook.
export function outboundDrag({current,probe,handoff,message,interval=80}) {
  let pending=false,timer=0,last=0,generation=0,disposed=false,resample=false;
  const eligible=value=>value?.active&&value.kind==='item'&&!value.virtual;
  async function check(value) {
    if(disposed||!eligible(value)||current()!==value)return;
    if(pending){resample=true;return;}
    const elapsed=performance.now()-last;
    if(elapsed<interval){if(!timer)timer=setTimeout(()=>{timer=0;void check(value);},interval-elapsed);return;}
    const token=generation;pending=true;last=performance.now();
    try {
      const result=await probe();
      if(!disposed&&token===generation&&current()===value&&eligible(value)&&result.eligible){cancel();handoff(value);}
    } catch(error) {if(!disposed&&token===generation&&current()===value)message('无法交接 Windows 拖放：'+error.message,true);}
    finally{
      pending=false;
      if(resample){resample=false;void check(current());}
    }
  }
  // Called after the caller has cleared its pointer gesture, before committing a
  // layout drop. A fast release can beat the async handoff probe. Do not then
  // reinterpret a drop over an application as moving a desktop grid icon.
  // We cannot synthesize a completed mouse drag, so leave data/layout intact.
  async function guardDrop(value){
    if(disposed)return true;
    if(!eligible(value))return false;
    const token=generation;
    try{
      const target=await probe();
      if(disposed||token!==generation||current())return true;
      if(target.external){message('拖动已结束，未改变文件或桌面位置；请重新按住拖入应用的文件接收区域。',true);return true;}
      return false;
    }catch(error){
      if(!disposed&&token===generation&&!current())message('无法确认拖放目标，已保留原位置：'+error.message,true);
      return true;
    }
  }
  function cancel(){generation++;resample=false;clearTimeout(timer);timer=0;}
  return {check,cancel,guardDrop,dispose(){disposed=true;cancel();}};
}
