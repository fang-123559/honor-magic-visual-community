using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using P = MagicCommunity.SecondaryWallpaperPolicy;

namespace MagicCommunity;

// Owned by DesktopBridge. Bounded transient STA work per event batch; no idle worker,
// no new observer window and no wallpaper API/store initialization on one screen.
public sealed class SecondaryWallpaperGuard : IDisposable
{
    public sealed record Target(string DevicePath, string Label, string CurrentImage, string SavedImage, long Generation, long Revision, bool CanRestore);
    public sealed record View(string Status, IReadOnlyList<Target> Targets);
    private sealed record Saved(P.Baseline Baseline, Dictionary<string,string> Fingerprints);
    private sealed record Envelope(int Schema, string Payload, string Sha256);
    private sealed record Sample(P.Monitor[] Monitors, P.Observation Observation);
    private readonly AppController controller;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer debounce;
    private long generation;
    private bool disposed, running, pending, storeLoaded, storeBlocked;
    private P.Trigger pendingTrigger = P.Trigger.Startup;
    private Saved saved = new(new(0,true,[]),new(StringComparer.OrdinalIgnoreCase));
    private View view = new("正在读取副屏壁纸…",[]);
    private string StorePath => Path.Combine(controller.Root,"data","secondary-wallpapers.json");
    public View Current => view;
    public event Action? Changed;

    public SecondaryWallpaperGuard(AppController controller)
    {
        if(controller.Screens.Count<2)throw new InvalidOperationException("No secondary display.");
        this.controller=controller;dispatcher=Dispatcher.CurrentDispatcher;
        debounce=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=TimeSpan.FromMilliseconds(450)};
        debounce.Tick+=Tick;Signal(P.Trigger.Startup);
    }
    public void Signal(P.Trigger trigger)
    {
        dispatcher.VerifyAccess();if(disposed)return;
        Interlocked.Increment(ref generation);
        static int Priority(P.Trigger value)=>value==P.Trigger.TopologyChanged?3:value==P.Trigger.WindowsSettingsObserved?2:value==P.Trigger.Startup?1:0;
        // Refresh/read-only signals must not erase a queued wallpaper change.
        if(!pending||Priority(trigger)>Priority(pendingTrigger))pendingTrigger=trigger;
        pending=true;
        debounce.Stop();debounce.Start();
    }
    private async void Tick(object? sender,EventArgs args)
    {
        debounce.Stop();if(disposed||running||!pending)return;
        pending=false;var trigger=pendingTrigger;pendingTrigger=P.Trigger.ReadOnlyCheck;
        await ObserveAsync(trigger);
    }
    public async Task RefreshAsync()
    {
        dispatcher.VerifyAccess();if(disposed)return;
        if(running){Signal(P.Trigger.ReadOnlyCheck);return;}
        Interlocked.Increment(ref generation);debounce.Stop();var trigger=pending?pendingTrigger:P.Trigger.ReadOnlyCheck;pending=false;pendingTrigger=P.Trigger.ReadOnlyCheck;
        await ObserveAsync(trigger);
    }
    private async Task ObserveAsync(P.Trigger trigger)
    {
        if(disposed||running||controller.Screens.Count<2)return;
        running=true;long stamp=Interlocked.Read(ref generation);
        try
        {
            var result=await Sta(()=>{var state=storeLoaded?saved:Load();return (state,sample:Read(stamp));});
            if(!Fresh(stamp))return;
            saved=result.state;storeLoaded=true;
            var input=new P.Input(stamp,result.sample.Monitors,trigger,result.sample.Observation,saved.Baseline);
            var decision=P.Evaluate(input);
            if(decision.BaselineUpdates.Count>0&&!controller.Store.ReadOnly&&!storeBlocked)
            {
                // Fingerprints bind recovery to the saved file bytes, never just a filename.
                var updates=await Sta(()=>decision.BaselineUpdates.Select(e=>(Entry:e,Hash:Fingerprint(e.ImagePath))).Where(e=>e.Hash is not null).ToArray());
                if(!Fresh(stamp))return;
                var entries=saved.Baseline.Entries.ToDictionary(e=>e.DevicePath,StringComparer.OrdinalIgnoreCase);
                var hashes=new Dictionary<string,string>(saved.Fingerprints,StringComparer.OrdinalIgnoreCase);
                foreach(var entry in updates){entries[entry.Entry.DevicePath]=entry.Entry;hashes[entry.Entry.DevicePath]=entry.Hash!;}
                if(updates.Length>0){
                    if(entries.Count>64)throw new InvalidDataException("Too many wallpaper records.");
                    var next=new Saved(new(saved.Baseline.Revision+1,true,entries.Values.ToArray()),hashes);
                    await Sta(()=>{if(Fresh(stamp))Save(next);return true;});
                    if(!Fresh(stamp))return;saved=next;
                }
            }
            Publish(result.sample,decision.Reason);
        }
        catch(Exception error){Fail(error);}
        finally{running=false;if(pending&&!disposed){debounce.Stop();debounce.Start();}}
    }
    private bool Fresh(long stamp)=>!Volatile.Read(ref disposed)&&Interlocked.Read(ref generation)==stamp;
    private void Publish(Sample sample,string reason)
    {
        var readings=sample.Observation.Readings.ToDictionary(r=>r.DevicePath,StringComparer.OrdinalIgnoreCase);
        var rows=new List<Target>();
        foreach(var monitor in sample.Monitors.Where(m=>!m.Primary)){
            var entry=saved.Baseline.Entries.FirstOrDefault(e=>StringComparer.OrdinalIgnoreCase.Equals(e.DevicePath,monitor.DevicePath));
            var current=readings.GetValueOrDefault(monitor.DevicePath)?.ImagePath??"";
            var approval=new P.RestoreApproval(monitor.DevicePath,sample.Observation.Generation,saved.Baseline.Revision,true);
            var decision=P.Evaluate(new(sample.Observation.Generation,sample.Monitors,P.Trigger.ExplicitRestore,sample.Observation,saved.Baseline,approval));
            rows.Add(new(monitor.DevicePath,controller.Screens.FirstOrDefault(s=>StringComparer.OrdinalIgnoreCase.Equals(s.Id,monitor.DevicePath))?.Label??"副屏",current,entry?.ImagePath??"",sample.Observation.Generation,saved.Baseline.Revision,decision.Restore is not null&&!controller.Store.ReadOnly&&!storeBlocked));
        }
        view=new(storeBlocked?"壁纸记录无效，已停止保存与恢复，请保留记录后排查。":controller.Store.ReadOnly?"当前配置只读，壁纸保存与恢复已暂停。":sample.Observation.Mode!=P.Mode.StaticImages?"当前为非静态壁纸或无法确认模式，保留 Windows 设置。":!sample.Observation.Complete?"副屏读取不完整，保留已有记录。":rows.Any(r=>string.IsNullOrEmpty(r.SavedImage))?"部分图片未保存：仅记录可读取、大小不超过 64 MiB 的静态图片。":"已记录副屏壁纸。启动与重连时保留 Windows 当前选择，可手动恢复。",rows);Changed?.Invoke();
        controller.Log("secondary_wallpaper_observed",new{reason,mode=sample.Observation.Mode.ToString(),count=rows.Count,revision=saved.Baseline.Revision});
    }
    public async Task<string> RestoreAsync(Target target)
    {
        dispatcher.VerifyAccess();
        if(disposed||running||controller.Store.ReadOnly||storeBlocked||!target.CanRestore||!Fresh(target.Generation)||target.Revision!=saved.Baseline.Revision)
            return "状态已变化或正在读取，请刷新后重新选择。";
        running=true;var state=saved;long stamp=target.Generation;
        try
        {
            string result=await Sta(()=>{
                if(!Fresh(stamp))return "恢复已取消：显示或壁纸状态发生变化。";
                if(!state.Fingerprints.TryGetValue(target.DevicePath,out string? expected)||Fingerprint(target.SavedImage)!=expected)
                    return "保存的图片已更改或不可读取，未恢复。";
                // First read validates the approval, second read immediately before Set
                // catches intervening mode/topology/path changes. COM is not atomic with Windows.
                var first=Read(stamp);
                if(!StringComparer.OrdinalIgnoreCase.Equals(first.Observation.Readings.FirstOrDefault(r=>StringComparer.OrdinalIgnoreCase.Equals(r.DevicePath,target.DevicePath))?.ImagePath,target.CurrentImage))return "当前壁纸已变化，请刷新后重新选择。";
                var approval=new P.RestoreApproval(target.DevicePath,stamp,state.Baseline.Revision,true);
                var intent=P.Evaluate(new(stamp,first.Monitors,P.Trigger.ExplicitRestore,first.Observation,state.Baseline,approval)).Restore;
                if(intent is null)return "当前模式、屏幕或图片不满足恢复条件。";
                using var api=new WallpaperApi();var fresh=Read(stamp,api);
                if(!Fresh(stamp)||!P.CanApply(intent,new(stamp,fresh.Monitors,P.Trigger.ExplicitRestore,fresh.Observation,state.Baseline,approval))||Fingerprint(intent.ImagePath)!=expected)
                    return "恢复已取消：当前状态或保存的图片已变化。";
                if(string.IsNullOrWhiteSpace(intent.DevicePath)||fresh.Monitors.Single(m=>StringComparer.OrdinalIgnoreCase.Equals(m.DevicePath,intent.DevicePath)).Primary)
                    return "不能恢复主屏或不明确的目标。";
                if(!Fresh(stamp))return "恢复已取消。";
                Marshal.ThrowExceptionForHR(api.Com.SetWallpaper(intent.DevicePath,intent.ImagePath));
                int hr=api.Com.GetWallpaper(intent.DevicePath,out string actual);
                return hr==0&&StringComparer.OrdinalIgnoreCase.Equals(P.NormalizeImagePath(actual),intent.ImagePath)?"已恢复所选副屏壁纸。":"Windows 已接收恢复请求，但回读未确认，请检查屏幕。";
            });
            controller.Log("secondary_wallpaper_restore",new{generation=stamp,revision=target.Revision,result});return result;
        }
        catch(Exception error){Fail(error);return "恢复未完成："+error.GetType().Name;}
        finally{running=false;if(!disposed)Signal(P.Trigger.ReadOnlyCheck);}
    }
    private void Fail(Exception error){if(disposed)return;view=new("壁纸保护暂不可用，保留 Windows 当前设置。",[]);controller.Log("secondary_wallpaper_unavailable",new{error=error.GetType().Name});Changed?.Invoke();}
    private Saved Load()
    {
        if(!File.Exists(StorePath))return saved;
        try{
            if(new FileInfo(StorePath).Length>131072)throw new InvalidDataException();
            var envelope=JsonSerializer.Deserialize<Envelope>(File.ReadAllText(StorePath))??throw new InvalidDataException();
            if(envelope.Schema!=1||envelope.Sha256!=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload))))throw new InvalidDataException();
            var state=JsonSerializer.Deserialize<Saved>(envelope.Payload)??throw new InvalidDataException();
            if(state.Baseline is null||state.Baseline.Entries is null||state.Fingerprints is null||!state.Baseline.Trusted||state.Baseline.Revision<0||state.Baseline.Entries.Count>64)throw new InvalidDataException();
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in state.Baseline.Entries)if(entry is null||string.IsNullOrWhiteSpace(entry.DevicePath)||entry.DevicePath.StartsWith("temporary:",StringComparison.OrdinalIgnoreCase)||!ids.Add(entry.DevicePath)||P.NormalizeImagePath(entry.ImagePath)!=entry.ImagePath||!state.Fingerprints.TryGetValue(entry.DevicePath,out string? hash)||hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new InvalidDataException();
            return new(state.Baseline,new(state.Fingerprints,StringComparer.OrdinalIgnoreCase));
        }catch(Exception e)when(e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NullReferenceException){storeBlocked=true;return saved;}
    }
    private void Save(Saved state)
    {
        string payload=JsonSerializer.Serialize(state);var envelope=new Envelope(1,payload,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        StateStore.AtomicWrite(StorePath,JsonSerializer.Serialize(envelope),null);
    }
    private static string? Fingerprint(string path)
    {
        try{var file=new FileInfo(path);if(!file.Exists||file.Length is <=0 or >64*1024*1024)return null;using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(stream));}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return null;}
    }
    private static Sample Read(long stamp,WallpaperApi? existing=null)
    {
        var displays=Displays.Read();if(displays.Count<2)return new([],new(stamp,P.Mode.Unknown,false,false,[]));
        var monitors=displays.Select(d=>new P.Monitor(d.Id,d.Primary,!d.Id.StartsWith("temporary:",StringComparison.OrdinalIgnoreCase))).ToArray();
        if(monitors.Count(m=>m.Primary)!=1||monitors.Any(m=>!m.ReliableIdentity)||monitors.Select(m=>m.DevicePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=monitors.Length)
            return new(monitors,new(stamp,P.Mode.Unknown,false,false,[]));
        using var owned=existing is null?new WallpaperApi():null;var api=existing??owned!;
        Marshal.ThrowExceptionForHR(api.Com.GetMonitorDevicePathCount(out uint count));if(count>64)throw new InvalidDataException();
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(uint i=0;i<count;i++){Marshal.ThrowExceptionForHR(api.Com.GetMonitorDevicePathAt(i,out string id));if(!ids.Add(id))throw new InvalidDataException();}
        if(monitors.Any(m=>!ids.Contains(m.DevicePath)))return new(monitors,new(stamp,P.Mode.Unknown,false,false,[]));
        Marshal.ThrowExceptionForHR(api.Com.GetStatus(out uint status));Marshal.ThrowExceptionForHR(api.Com.GetPosition(out uint position));
        var readings=new List<P.Reading>();foreach(var monitor in monitors){int hr=api.Com.GetWallpaper(monitor.DevicePath,out string image);readings.Add(new(monitor.DevicePath,hr,image,hr==0&&File.Exists(image)));}
        var mode=(status&6)!=0?P.Mode.Slideshow:position==5?P.Mode.Span:readings.Any(r=>r.HResult!=0)?P.Mode.Unknown:readings.Any(r=>string.IsNullOrWhiteSpace(r.ImagePath))?P.Mode.SolidColor:P.Mode.StaticImages;
        // Spotlight is a changing Windows-owned source even if GetWallpaper returns a static cache file.
        if((int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers","BackgroundType",null)==3)mode=P.Mode.Unknown;
        var after=Displays.Read();bool stable=displays.Count==after.Count&&displays.All(d=>after.Any(a=>StringComparer.OrdinalIgnoreCase.Equals(a.Id,d.Id)&&a.Primary==d.Primary));
        return new(monitors,new(stamp,mode,stable,stable&&readings.All(r=>r.HResult==0&&r.ImageAvailable),readings));
    }
    private static Task<T> Sta<T>(Func<T> action)
    {
        var source=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{source.SetResult(action());}catch(Exception e){source.SetException(e);}}){IsBackground=true,Name="Magic secondary wallpaper"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return source.Task;
    }
    public void Dispose(){dispatcher.VerifyAccess();if(disposed)return;disposed=true;Interlocked.Increment(ref generation);pending=false;debounce.Stop();debounce.Tick-=Tick;saved=new(new(0,true,[]),new(StringComparer.OrdinalIgnoreCase));view=new("未连接副屏，保护已暂停。",[]);Changed?.Invoke();Changed=null;}

    private sealed class WallpaperApi:IDisposable
    {
        public IDesktopWallpaper Com{get;}
        public WallpaperApi(){Com=(IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"),true)!)!;}
        public void Dispose(){Marshal.FinalReleaseComObject(Com);}
    }
    [ComImport,Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig]int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string monitor,[MarshalAs(UnmanagedType.LPWStr)]string image);
        [PreserveSig]int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)]string monitor,[MarshalAs(UnmanagedType.LPWStr)]out string image);
        [PreserveSig]int GetMonitorDevicePathAt(uint index,[MarshalAs(UnmanagedType.LPWStr)]out string id);
        [PreserveSig]int GetMonitorDevicePathCount(out uint count);
        [PreserveSig]int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)]string id,out NativeRect rect);
        [PreserveSig]int SetBackgroundColor(uint color);
        [PreserveSig]int GetBackgroundColor(out uint color);
        [PreserveSig]int SetPosition(uint position);
        [PreserveSig]int GetPosition(out uint position);
        [PreserveSig]int SetSlideshow(IntPtr items);
        [PreserveSig]int GetSlideshow(out IntPtr items);
        [PreserveSig]int SetSlideshowOptions(uint options,uint tick);
        [PreserveSig]int GetSlideshowOptions(out uint options,out uint tick);
        [PreserveSig]int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)]string id,uint direction);
        [PreserveSig]int GetStatus(out uint state);
        [PreserveSig]int Enable([MarshalAs(UnmanagedType.Bool)]bool enable);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
}
