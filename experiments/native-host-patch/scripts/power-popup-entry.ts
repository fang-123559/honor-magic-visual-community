import {Widget} from '@seelen-ui/lib';
const widget=Widget.self;
const ready=widget.ready.bind(widget);
widget.ready=async(options)=>{await ready(options);console.info('MAGIC_POPUP_RESIDENT_READY',JSON.stringify({id:widget.id,windowId:widget.windowId,profile:'0.1.0-rc.15',sdkSource:'2.8.9',closeOnHide:false,showOnReady:false}));};
const base=document.createElement('base');base.href='/svelte/quick-settings/';document.head.append(base);
try{await import('../src/ui/svelte/quick-settings/index.ts');}catch(error){console.error('MAGIC_POPUP_FAILED',widget.id,error);}
