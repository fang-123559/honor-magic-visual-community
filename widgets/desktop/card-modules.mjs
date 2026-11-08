import {registerDesktopCard,registerDesktopCardDataSource} from './card-registry.mjs';
import {renderSystemCard} from './system-cards.mjs';
import {renderTodoCard} from './todo-card.mjs';
import {dateSource,resourceSource} from './system-card-data.mjs';

for(const type of ['system.clock','system.calendar','system.status','system.storage']){
 const render=(root,context)=>renderSystemCard(root,context);render.appearance='widget';registerDesktopCard(type,render);
}
registerDesktopCardDataSource('system.minute',dateSource('minute'));
registerDesktopCardDataSource('system.day',dateSource('day'));
for(const [name,command,event] of [
 ['cores','GetSystemCores','SystemCoresChanged'],
 ['gpus','GetSystemGpus','SystemGpusChanged'],
 ['memory','GetSystemMemory','SystemMemoryChanged'],
 ['disks','GetSystemDisks','SystemDisksChanged'],
])registerDesktopCardDataSource('system.'+name,async(emit,signal)=>{
 const sdk=await import('../../experiments/native-host-patch/libs/core/src/handlers/mod.ts');
 if(signal.aborted)return;
 return resourceSource(sdk.invoke,sdk.subscribe,sdk.SeelenCommand[command],sdk.SeelenEvent[event])(emit,signal);
});

const todoRenderer=(root,context)=>renderTodoCard(root,context);todoRenderer.appearance='widget';registerDesktopCard('system.todo',todoRenderer);
