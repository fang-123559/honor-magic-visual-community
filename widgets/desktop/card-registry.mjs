// Only build-owned modules register here. Catalog JSON never imports code.
const factories=new Map();
export function registerDesktopCard(type,factory){if(!/^[a-z0-9._/-]{1,96}$/.test(type)||typeof factory!=='function'||factories.has(type))throw new Error('Invalid or duplicate card renderer');factories.set(type,factory);}
export const desktopCardRenderers={get:type=>factories.get(type)};
const dataProviders=new Map();
export function registerDesktopCardDataSource(name,provider){if(!/^[a-z0-9._/-]{1,96}$/.test(name)||typeof provider!=='function'||dataProviders.has(name))throw new Error('Invalid or duplicate card data source');dataProviders.set(name,provider);}
export const desktopCardDataSources={get:name=>dataProviders.get(name)};

// One provider instance for active subscribers; its last subscriber aborts it.
export function createCardDataHub(providers=desktopCardDataSources,onError=console.error){
 const running=new Map();
 function stop(name,entry){if(running.get(name)!==entry)return;running.delete(name);entry.abort.abort();try{entry.dispose?.();}catch(error){onError(error);}}
 return {
  subscribe(name,listener,signal){
   if(typeof listener!=='function'||signal?.aborted)return()=>{};
   const provider=providers.get(name);if(typeof provider!=='function')throw new Error('Card data source unavailable: '+name);
   let entry=running.get(name),start=!entry;
   if(start){entry={abort:new AbortController(),listeners:new Set(),dispose:null,hasValue:false};running.set(name,entry);}
   const callback=value=>listener(value);entry.listeners.add(callback);let removed=false;
   const remove=()=>{if(removed)return;removed=true;signal?.removeEventListener('abort',remove);entry.listeners.delete(callback);if(!entry.listeners.size)stop(name,entry);};signal?.addEventListener('abort',remove,{once:true});
   if(start){
    const emit=value=>{if(entry.abort.signal.aborted)return;entry.value=value;entry.hasValue=true;for(const callback of [...entry.listeners])try{callback(value);}catch(error){onError(error);}};
    try{const result=provider(emit,entry.abort.signal);Promise.resolve(result).then(dispose=>{if(dispose!==undefined&&typeof dispose!=='function')throw new Error('Provider must return a disposer');if(entry.abort.signal.aborted)dispose?.();else entry.dispose=dispose;}).catch(error=>{onError(error);stop(name,entry);});}catch(error){onError(error);stop(name,entry);}
   }else if(entry.hasValue)try{callback(entry.value);}catch(error){onError(error);}
   return remove;
  },
  dispose(){for(const [name,entry] of [...running])stop(name,entry);},
  get activeProviders(){return running.size;}
 };
}
