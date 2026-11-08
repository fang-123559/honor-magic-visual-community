// Fixed DOM templates only. Package text is never markup, CSS or executable code.
export function countdownFrame(content,now=new Date()){
 const daily=/^\d{2}:\d{2}$/.test(content.target),parts=content.target.match(/\d+/g).map(Number);
 const target=daily?new Date(now.getFullYear(),now.getMonth(),now.getDate(),parts[0],parts[1]):new Date(parts[0],parts[1]-1,parts[2],parts[3]||0,parts[4]||0);
 const remaining=target.getTime()-now.getTime();
 if(remaining<=0){const midnight=new Date(now.getFullYear(),now.getMonth(),now.getDate()+1);return {text:content.doneText||'已到时间',delay:daily?midnight-now:null};}
 const unit=content.format==='hms'?1000:content.format==='hm'?60000:86400000,value=Math.ceil(remaining/unit);
 const pad=v=>String(v).padStart(2,'0');let text=content.format==='d'?value+' 天':content.format==='hm'?pad(Math.floor(value/60))+':'+pad(value%60):pad(Math.floor(value/3600))+':'+pad(Math.floor(value/60)%60)+':'+pad(value%60);
 return {text,delay:Math.max(16,Math.min(86400000,remaining%unit||unit))};
}
function clockFormatter(content){return new Intl.DateTimeFormat(undefined,{hour:'2-digit',minute:'2-digit',...(content.showSeconds?{second:'2-digit'}:{}),hour12:content.hour12});}
export function renderClock(root,content,signal){
 const formatter=clockFormatter(content);
 return schedule(root,()=>{const now=new Date(),unit=content.showSeconds?1000:60000;return {text:formatter.format(now),delay:unit-now.getTime()%unit};},signal);
}
function schedule(root,read,signal){
 let timer=null,disposed=false;
 const stop=()=>{if(disposed)return;disposed=true;clearTimeout(timer);signal.removeEventListener('abort',stop);};
 const tick=()=>{if(disposed||signal.aborted)return;const frame=read();root.textContent=frame.text;if(frame.delay!==null)timer=setTimeout(tick,frame.delay);};
 signal.addEventListener('abort',stop,{once:true});tick();return stop;
}
export function renderDeclarativeCard(root,context){
 const {custom}=context.definition,c=custom.content,document=root.ownerDocument;
 root.classList.add('declarative-card');root.dataset.tone=custom.tone;root.dataset.align=custom.align;
 const node=(tag,cls,text)=>{const el=document.createElement(tag);el.className=cls;if(text!==undefined)el.textContent=text;root.append(el);return el;};
 if(custom.kind==='text'){if(c.title)node('div','card-small-title',c.title);const body=node('div','card-text-body',c.body);body.dataset.size=c.bodySize;return;}
 if(custom.kind==='clock'){
  const value=node('div','card-time-value');
  if(context.preview){value.textContent=clockFormatter(c).format(context.now??new Date());return;}
  return renderClock(value,c,context.signal);
 }
 if(custom.kind==='countdown'){
  if(c.label)node('div','card-small-title',c.label);const value=node('div','card-time-value');
  if(context.preview){value.textContent=countdownFrame(c,context.now??new Date()).text;return;}
  return schedule(value,()=>countdownFrame(c),context.signal);
 }
 if(custom.kind==='shortcut'){
  // A preview uses the same labels and styles without opening URLs or scheduling work.
  if(context.preview){c.items.forEach(item=>node('span','card-shortcut',item.label));return;}
  const actions=new AbortController();c.items.forEach((item,index)=>{const button=node('button','card-shortcut',item.label);button.type='button';button.addEventListener('click',event=>{event.stopPropagation();button.disabled=true;void context.openShortcut(index).catch(error=>{let status=root.querySelector('.card-inline-error');if(!status)status=node('div','card-inline-error');status.textContent=error.message;}).finally(()=>{button.disabled=false;});},{signal:actions.signal});});return()=>actions.abort();
 }
}
