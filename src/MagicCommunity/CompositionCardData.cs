using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicCommunity;
// Resource polling is demanded only by visible status/storage cards. One shared
// background loop, two-second cadence, no provider for a desktop without them.
internal sealed class CompositionCardData : IDisposable
{
    private readonly Action<object> emit;
    private CancellationTokenSource? running;
    private readonly object gate=new();
    private volatile bool status,storage,disposed;
    private ulong oldIdle,oldKernel,oldUser;
    private IntPtr query,counter;
    public CompositionCardData(Action<object> emit)=>this.emit=emit;
    public void Demand(IEnumerable<string> types)
    {
        var requested=types.ToHashSet(StringComparer.Ordinal);
        lock(gate){status=requested.Contains("system.status");storage=requested.Contains("system.storage");
            if(!status&&!storage){running?.Cancel();return;}
            if(running is null&&!disposed)Start();}
    }
    private void Start(){var owner=new CancellationTokenSource();running=owner;_=Run(owner);}
    private async Task Run(CancellationTokenSource owner)
    {
        var token=owner.Token;
        try{
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
            do{
                try{var value=await Task.Run(Sample,token);if(!token.IsCancellationRequested)emit(value);}
                catch(OperationCanceledException){throw;}
                catch(Exception error)when(error is not OutOfMemoryException){if(!token.IsCancellationRequested)emit(new{available=false});}
            }while(await timer.WaitForNextTickAsync(token));
        }catch(OperationCanceledException){}
        finally{CloseGpu();lock(gate){running=null;owner.Dispose();if(!disposed&&(status||storage))Start();}}
    }
    private object Sample()
    {
        double? cpu=null,gpu=null;object? memory=null,disks=null;
        if(status){
            if(GetSystemTimes(out ulong idle,out ulong kernel,out ulong user)){
                ulong total=kernel+user,prior=oldKernel+oldUser;
                if(prior>0&&total>prior&&idle>=oldIdle)cpu=100d*(total-prior-Math.Min(total-prior,idle-oldIdle))/(total-prior);
                oldIdle=idle;oldKernel=kernel;oldUser=user;
            }
            gpu=Gpu();
        }else CloseGpu();
        if(storage){
            var m=new Memory{Length=(uint)Marshal.SizeOf<Memory>()};if(GlobalMemoryStatusEx(ref m))memory=new{total=m.TotalPhysical,free=m.AvailablePhysical};
            try{disks=DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed&&d.IsReady).Select(d=>new{mount=d.Name,total=d.TotalSize,free=d.AvailableFreeSpace}).ToArray();}catch(IOException){}
        }
        return new{at=DateTimeOffset.Now,cpu,gpu,memory,disks};
    }
    private double? Gpu()
    {
        if(query==IntPtr.Zero){
            if(PdhOpenQuery(null,0,out query)!=0){query=IntPtr.Zero;return null;}
            if(PdhAddEnglishCounter(query,@"\GPU Engine(*)\Utilization Percentage",0,out counter)!=0){CloseGpu();return null;}
            PdhCollectQueryData(query);return null;
        }
        if(PdhCollectQueryData(query)!=0)return null;
        uint bytes=0,count=0;const uint format=0x200|0x8000;
        uint result=PdhGetFormattedCounterArray(counter,format,ref bytes,out count,IntPtr.Zero);
        if(result!=0x800007d2||bytes is 0 or >1048576||count>65536)return null;
        IntPtr buffer=Marshal.AllocHGlobal((int)bytes);
        try{
            if(PdhGetFormattedCounterArray(counter,format,ref bytes,out count,buffer)!=0)return null;
            var sums=new Dictionary<string,double>(StringComparer.Ordinal);int step=Marshal.SizeOf<GpuValue>();
            for(int i=0;i<count;i++){
                var row=Marshal.PtrToStructure<GpuValue>(buffer+i*step);if(row.Status is not (0 or 1)||!double.IsFinite(row.Value))continue;
                string name=Marshal.PtrToStringUni(row.Name)??"";int luid=name.IndexOf("luid_",StringComparison.Ordinal);
                if(luid<0||!name.EndsWith("engtype_3D",StringComparison.OrdinalIgnoreCase))continue;
                string key=name[luid..];sums[key]=sums.GetValueOrDefault(key)+Math.Max(0,row.Value);
            }
            return sums.Count>0?Math.Clamp(sums.Values.Max(),0,100):null;
        }finally{Marshal.FreeHGlobal(buffer);}
    }
    private void CloseGpu(){if(query!=IntPtr.Zero)PdhCloseQuery(query);query=counter=IntPtr.Zero;}
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;running?.Cancel();}}
    [StructLayout(LayoutKind.Sequential)]private struct Memory{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [StructLayout(LayoutKind.Sequential)]private struct GpuValue{public IntPtr Name;public uint Status;public double Value;}
    [DllImport("kernel32.dll")]private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")]private static extern bool GlobalMemoryStatusEx(ref Memory value);
    [DllImport("pdh.dll",EntryPoint="PdhOpenQueryW",CharSet=CharSet.Unicode)]private static extern uint PdhOpenQuery(string? source,IntPtr data,out IntPtr query);
    [DllImport("pdh.dll",EntryPoint="PdhAddEnglishCounterW",CharSet=CharSet.Unicode)]private static extern uint PdhAddEnglishCounter(IntPtr query,string path,IntPtr data,out IntPtr counter);
    [DllImport("pdh.dll")]private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll",EntryPoint="PdhGetFormattedCounterArrayW")]private static extern uint PdhGetFormattedCounterArray(IntPtr counter,uint format,ref uint bytes,out uint count,IntPtr values);
    [DllImport("pdh.dll")]private static extern uint PdhCloseQuery(IntPtr query);
}
