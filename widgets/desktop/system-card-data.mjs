// Providers start only for a visible card, and the card hub shares each one.
export function dateSource(cadence){
 return (emit,signal)=>{
  let timer=null,stopped=false;
  const stop=()=>{stopped=true;clearTimeout(timer);signal.removeEventListener('abort',stop);};
  const tick=()=>{
   if(stopped||signal.aborted)return;
   const now=new Date();emit(now);
   const delay=cadence==='day'?new Date(now.getFullYear(),now.getMonth(),now.getDate()+1)-now:60000-now.getTime()%60000;
   timer=setTimeout(tick,Math.max(16,delay));
  };
  signal.addEventListener('abort',stop,{once:true});tick();return stop;
 };
}

// Use the current SDK's acknowledged websocket subscription: the legacy Tauri
// event transport is deliberately excluded from the host's sampling demand.
export function resourceSource(invoke,subscribe,command,event){
 return async(emit,signal)=>{
  let stopped=signal.aborted,unlisten=null,revision=0;
  const stop=()=>{stopped=true;unlisten?.();unlisten=null;signal.removeEventListener('abort',stop);};
  signal.addEventListener('abort',stop,{once:true});
  try{
   if(stopped)return stop;
   const release=await subscribe(event,message=>{revision++;if(!stopped)emit({available:true,data:message.payload});});
   if(stopped){release();return stop;}unlisten=release;
   const expected=revision;
   try{const data=await invoke(command);if(!stopped&&revision===expected)emit({available:true,data});}
   catch{if(!stopped&&revision===expected)emit({available:false,data:null});}
  }catch{if(!stopped)emit({available:false,data:null});stop();}
  return stop;
 };
}
