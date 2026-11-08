using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MagicCommunity;

// A tiny native process owns the hide/show operation. The WPF process cannot
// restore Explorer after it has itself been killed, so an in-process finally is
// insufficient. The guard waits on process handles and window events, not a timer.
public sealed class NativeDesktopLease : IDisposable
{
    private readonly EventWaitHandle restore;
    private readonly Process guard;
    private bool disposed;
    private readonly string recordPath;
    private readonly HashSet<long> windows;
    public bool IsAlive=>!disposed&&!guard.HasExited;
    public bool Owns(long window)=>windows.Contains(window);
    public static bool IconsVisible()=>IsWindowVisible(NativeDesktop.FindDesktopListView());
    public NativeDesktopLease(string root,IReadOnlyList<long> windows,Action onUnexpectedExit,int expectedHost=0)
    {
        this.windows=windows.ToHashSet();
        if(windows.Count is <1 or >64)throw new InvalidDataException("完整桌面窗口尚未就绪。");
        foreach(long hwnd in windows)NativeRegion.Verify(hwnd,root);
        GetWindowThreadProcessId(new(windows[0]),out uint hostId);
        if(expectedHost!=0&&hostId!=expectedHost)throw new InvalidDataException("桌面窗口不属于当前认证的渲染器。");
        foreach(long hwnd in windows){GetWindowThreadProcessId(new(hwnd),out uint windowPid);if(windowPid!=hostId)throw new InvalidDataException("桌面窗口不属于同一宿主。");}
        var list=NativeDesktop.FindDesktopListView();if(list==IntPtr.Zero)throw new Win32Exception();
        GetWindowThreadProcessId(list,out uint explorerId);
        using var adapter=Process.GetCurrentProcess();using var host=Process.GetProcessById((int)hostId);using var explorer=Process.GetProcessById((int)explorerId);
        var original=new NativeDesktopLeaseRecord(list.ToInt64(),(int)explorerId,explorer.StartTime.ToFileTimeUtc(),IsWindowVisible(list));
        recordPath=Path.Combine(root,"runtime/desktop/native-icon-lease.json");
        StateStore.AtomicWrite(recordPath,JsonSerializer.Serialize(original,StateStore.Json),null);
        string id="Local\\MagicDesktop-"+Guid.NewGuid().ToString("N");
        restore=new(false,EventResetMode.ManualReset,id+"-restore");
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,id+"-ready");
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"MagicDesktopGuard.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var value in new[]{adapter.Id.ToString(),adapter.StartTime.ToFileTimeUtc().ToString(),host.Id.ToString(),host.StartTime.ToFileTimeUtc().ToString(),explorer.Id.ToString(),explorer.StartTime.ToFileTimeUtc().ToString(),list.ToInt64().ToString(),original.Visible?"1":"0",id+"-restore",id+"-ready"}.Concat(windows.Select(hwnd=>hwnd.ToString(CultureInfo.InvariantCulture))))start.ArgumentList.Add(value);
        start.ArgumentList.Add("--diagnostics");start.ArgumentList.Add(Path.Combine(root,"logs","desktop-guard.log"));
        int adapterPid=adapter.Id,rendererPid=host.Id;
        void Trace(string phase,object detail){try{File.AppendAllText(Path.Combine(root,"logs","desktop-guard-adapter.jsonl"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,phase,adapterPid,hostPid=rendererPid,windows,detail})+Environment.NewLine);}catch(Exception error){Debug.WriteLine(error);}}
        try
        {
            Trace("launch",new{adapterCreated=adapter.StartTime.ToFileTimeUtc(),hostCreated=host.StartTime.ToFileTimeUtc(),explorerPid=explorer.Id,explorerCreated=explorer.StartTime.ToFileTimeUtc()});
            guard=Process.Start(start)??throw new InvalidOperationException("桌面恢复守护未启动。");
            if(!ready.WaitOne(3000)||guard.HasExited||IsWindowVisible(list))throw new InvalidOperationException("桌面恢复守护未就绪，保留 Windows 原图标。Guard="+(guard.HasExited?guard.ExitCode:-1));
            guard.Exited+=(_,_)=>{if(!disposed)Application.Current.Dispatcher.BeginInvoke(()=>{if(!disposed){Trace("unexpected-exit",new{guardPid=guard.Id,exitCode=guard.ExitCode});RestoreRecord(root);onUnexpectedExit();}});};
            guard.EnableRaisingEvents=true;
            if(guard.HasExited)throw new InvalidOperationException("桌面守护在就绪交接时退出："+guard.ExitCode);
            Trace("ready",new{guardPid=guard.Id});
        }
        catch(Exception error){disposed=true;Trace("failed",new{error=error.Message});restore.Set();RestoreRecord(root);restore.Dispose();guard?.Dispose();throw;}
    }
    public static bool RestoreRecord(string root)
    {
        string path=Path.Combine(root,"runtime/desktop/native-icon-lease.json");if(!File.Exists(path))return true;
        var saved=JsonSerializer.Deserialize<NativeDesktopLeaseRecord>(File.ReadAllBytes(path),StateStore.Json)??throw new InvalidDataException();
        var hwnd=new IntPtr(saved.Window);if(!IsWindow(hwnd))return true; // Explorer recreation starts with its own saved icon setting.
        GetWindowThreadProcessId(hwnd,out uint pid);if(pid!=saved.ExplorerPid)throw new InvalidDataException("原生图标窗口身份已变化。");
        using var explorer=Process.GetProcessById((int)pid);
        if(explorer.StartTime.ToFileTimeUtc()!=saved.ExplorerCreated||!StringComparer.OrdinalIgnoreCase.Equals(explorer.MainModule?.FileName,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")))throw new InvalidDataException("原生桌面进程身份已变化。");
        var cls=new StringBuilder(80);GetClassName(hwnd,cls,cls.Capacity);if(cls.ToString()!="SysListView32")throw new InvalidDataException("原生图标窗口类型已变化。");
        ShowWindow(hwnd,saved.Visible?8:0);return IsWindowVisible(hwnd)==saved.Visible;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        restore.Set();guard.WaitForExit(1000);
        RestoreRecord(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(recordPath)!,"../..")));
        restore.Dispose();guard.Dispose();
    }
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr hwnd,StringBuilder value,int size);
}
public sealed record NativeDesktopLeaseRecord(long Window,int ExplorerPid,long ExplorerCreated,bool Visible);
