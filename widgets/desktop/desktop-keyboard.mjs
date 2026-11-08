// Explicit user actions focus only this view. No global hook or idle polling.
export function desktopKeyboard({document,windowFocus,webviewFocus,verify,message,trace=()=>{}}) {
  let generation=0,disposed=false,acquisitions=0;
  const cancel=()=>{generation++;};
  // Moving focus from the frame to its WebView may itself emit DOM blur.
  // The OS verification decides the result of an in-progress handoff.
  const blur=()=>{trace(acquisitions?'transient-blur':'blur');if(!acquisitions)cancel();};
  const key=event=>{if(event.key==='Escape')cancel();};
  document.addEventListener('pointerdown',cancel,true);document.addEventListener('keydown',key);globalThis.addEventListener('blur',blur);
  async function take(anchor,{quiet=false,activate=true}={}) {
    const current=++generation,valid=()=>!disposed&&current===generation&&anchor?.isConnected;let started=false;
    try {
      if(!valid())return false;acquisitions++;started=true;trace('begin');
      async function step(name,operation){let timer;try{trace(name);return await Promise.race([operation(),new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('键盘焦点交接超时')),1500);})]);}finally{clearTimeout(timer);}}
      // A blank mouse click uses normal OS activation. Only an explicit edit
      // asks the host to activate again; never steal focus after a click-away.
      if(activate){
        await step('webview',webviewFocus);if(!valid())return false;
        await step('window',windowFocus);if(!valid())return false;
      }
      const result=await step('verify',verify);if(!valid())return false;trace(result.focused?'ready':'refused');
      if(!result.focused){if(!quiet)message('Windows 未将键盘焦点交给桌面，请重新选择图标后再试。',true);return false;}
      anchor.focus({preventScroll:true});return true;
    } catch(error) {trace('failed');if(valid()&&!quiet)message('桌面键盘焦点交接失败：'+error.message,true);return false;}
    finally{if(started)acquisitions--;}
  }
  return {take,cancel,dispose(){disposed=true;cancel();document.removeEventListener('pointerdown',cancel,true);document.removeEventListener('keydown',key);globalThis.removeEventListener('blur',blur);}};
}
