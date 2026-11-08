import type {Battery, PowerMode, PowerStatus} from '@seelen-ui/lib/types';

export class PowerSnapshot {
  constructor(
    readonly status:PowerStatus|null=null,
    readonly mode:PowerMode|null=null,
    readonly batteries:Battery[]|null=null,
    readonly statusRevision=0,
    readonly modeRevision=0,
    readonly batteriesRevision=0,
  ){}
  get revision(){return {status:this.statusRevision,mode:this.modeRevision,batteries:this.batteriesRevision};}
  withStatus(value:PowerStatus){return new PowerSnapshot(value,this.mode,this.batteries,this.statusRevision+1,this.modeRevision,this.batteriesRevision);}
  withMode(value:PowerMode){return new PowerSnapshot(this.status,value,this.batteries,this.statusRevision,this.modeRevision+1,this.batteriesRevision);}
  withBatteries(value:Battery[]){return new PowerSnapshot(this.status,this.mode,value,this.statusRevision,this.modeRevision,this.batteriesRevision+1);}
  get percent():number|null{
    if(this.hasBattery===false)return null;
    const values=(this.batteries??[]).map(b=>b.percentage).filter(v=>Number.isFinite(v)&&v>=0&&v<=100);
    if(values.length)return Math.round(values.reduce((a,b)=>a+b,0)/values.length);
    const value=this.status?.batteryLifePercent;
    return value!=null&&value>=0&&value<=100?Math.round(value):null;
  }
  get hasBattery():boolean|null{
    if(this.batteries?.length)return true;
    if(this.status&&this.status.batteryFlag!==255)return (this.status.batteryFlag&128)===0;
    return this.batteries===null?null:false;
  }
  get supply():string{
    if(this.hasBattery===false)return '未检测到电池';
    if(this.batteries?.some(b=>b.state==='charging'))return '充电中';
    if(this.batteries?.length&&this.batteries.every(b=>b.state==='full'))return '已充满';
    if(this.status?.acLineStatus===1)return '已接通电源';
    if(this.status?.acLineStatus===0)return '使用电池';
    return '状态暂不可用';
  }
  get saver():string{return this.status?.systemStatusFlag===1?'已开启':this.status?.systemStatusFlag===0?'已关闭':'暂不可用';}
  get modeText():string{
    const names:Record<string,string>={BatterySaver:'节能',BetterBattery:'更长续航',Balanced:'平衡',HighPerformance:'高性能',MaxPerformance:'最佳性能',GameMode:'游戏',MixedReality:'混合现实'};
    return this.mode?names[this.mode]??'暂不可用':'正在读取';
  }
  health(battery:Battery):string{
    // Vendor capacity is a ratio, not a percentage or the current charge.
    return Number.isFinite(battery.capacity)&&battery.capacity>0&&battery.capacity<=1.5?`${Math.round(battery.capacity*100)}%`:'暂不可用';
  }
}
