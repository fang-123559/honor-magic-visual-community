import {invoke, SeelenCommand, SeelenEvent, subscribe} from '@seelen-ui/lib';
import type {Battery, PowerMode, PowerStatus} from '@seelen-ui/lib/types';
import {PowerSnapshot} from './power-snapshot';

// Only existing cached getters and events; no timer, WMI call or extra webview.
class PowerPanelState {
  snapshot=$state.raw(new PowerSnapshot());
  refreshing=$state(false);
  private started=false;
  private pending: Promise<void> | null=null;
  async start():Promise<void>{
    if(this.started)return;
    this.started=true;
    const registrations=await Promise.allSettled([
      subscribe(SeelenEvent.PowerStatus,({payload})=>{this.snapshot=this.snapshot.withStatus(payload);}),
      subscribe(SeelenEvent.PowerMode,({payload})=>{this.snapshot=this.snapshot.withMode(payload);}),
      subscribe(SeelenEvent.BatteriesStatus,({payload})=>{this.snapshot=this.snapshot.withBatteries(payload);}),
    ]);
    for(const result of registrations)if(result.status==='rejected')console.warn('MAGIC_POWER_SUBSCRIPTION',result.reason);
    await this.refresh();
  }
  refresh():Promise<void>{
    if(this.pending)return this.pending;
    this.refreshing=true;
    const revision=this.snapshot.revision;
    this.pending=Promise.allSettled([
      invoke(SeelenCommand.GetPowerStatus),invoke(SeelenCommand.GetPowerMode),invoke(SeelenCommand.GetBatteries),
    ]).then(results=>{
      // Events received during IPC are newer than a getter snapshot.
      const [status,mode,batteries]=results;
      let next=this.snapshot;
      if(status.status==='fulfilled'&&next.statusRevision===revision.status)next=next.withStatus(status.value as PowerStatus);
      if(mode.status==='fulfilled'&&next.modeRevision===revision.mode)next=next.withMode(mode.value as PowerMode);
      if(batteries.status==='fulfilled'&&next.batteriesRevision===revision.batteries)next=next.withBatteries(batteries.value as Battery[]);
      this.snapshot=next;
      for(const result of results)if(result.status==='rejected')console.warn('MAGIC_POWER_REFRESH',result.reason);
    }).finally(()=>{this.refreshing=false;this.pending=null;});
    return this.pending;
  }
}
export const powerPanel=new PowerPanelState();
