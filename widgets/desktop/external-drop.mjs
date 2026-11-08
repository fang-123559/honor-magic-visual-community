// Some Windows WebView2 windows deliver HTML drag events instead of Tauri events.
// Keep both receivers, with a single commit path. No file contents are read here.
export function installExternalDrop({ document, nativeListen, folderAt, desktopAt=()=>false, importFiles, readOnly, commit, report, message, highlight, pixelRatio }) {
  let entered=false,inFlight=false,unlisten=null;
  const trace=(channel,phase)=>{void Promise.resolve(report(channel,phase)).catch(()=>{});};
  const destination=(x,y)=>readOnly()?null:folderAt(x,y);
  async function save(folder,kind,values,onDesktop=false){
    highlight(null);if(!folder&&(!onDesktop||!importFiles||kind!=="drop-files")){message("请将项目拖到大文件夹内，或桌面空白处。");return;}
    if(inFlight)return;inFlight=true;
    try{if(folder)await commit(kind,{folderId:folder,...values});else await importFiles(values.paths);}catch(error){message(error.message??String(error),true);}finally{inFlight=false;}
  }
  function dragenter(event){
    if(!entered){entered=true;trace("html","enter");}
    dragover(event);
  }
  function dragover(event){
    event.preventDefault();const folder=destination(event.clientX,event.clientY);highlight(folder);
    if(event.dataTransfer)event.dataTransfer.dropEffect=folder?"copy":"none";
  }
  function dragleave(event){if(!event.relatedTarget){entered=false;highlight(null);trace("html","leave");}}
  function drop(event){
    event.preventDefault();event.stopPropagation();entered=false;trace("html","drop");
    const folder=destination(event.clientX,event.clientY),names=[...(event.dataTransfer?.files??[])].map(file=>file.name);
    if(!names.length){highlight(null);message("此项目未提供可识别的文件引用，请在整理入口加入系统图标。",true);return;}
    void save(folder,"drop-desktop",{names});
  }
  for(const [name,listener]of Object.entries({dragenter,dragover,dragleave,drop}))document.addEventListener(name,listener);
  const ready=Promise.resolve().then(()=>nativeListen(event=>{
    const value=event.payload;if(value.type!=="over")trace("native",value.type);
    if(value.type==="leave"){highlight(null);return;}
    const ratio=pixelRatio(),x=value.position.x/ratio,y=value.position.y/ratio,folder=destination(x,y);highlight(folder);
    if(value.type==="drop")void save(folder,"drop-files",{paths:value.paths},!readOnly()&&desktopAt(x,y));
  })).then(remove=>{unlisten=remove;}).catch(()=>trace("native","unavailable"));
  return {ready,dispose(){for(const [name,listener]of Object.entries({dragenter,dragover,dragleave,drop}))document.removeEventListener(name,listener);void unlisten?.();highlight(null);}};
}
