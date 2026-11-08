using System.IO;
using System.Windows.Threading;

namespace MagicCommunity;

// Root-directory subscriptions, one event-triggered debounce. No scanning
// timer or recursive cache watch. Watcher errors request a guarded refresh.
internal sealed class DesktopDirectoryWatch : IDisposable
{
    private readonly List<FileSystemWatcher> watches=[];
    private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer debounce;
    private readonly Action notify;
    private int queued;
    private volatile bool disposed;
    public DesktopDirectoryWatch(Action notify,Action<string>? unavailable=null)
    {
        this.notify=notify;
        debounce=new(DispatcherPriority.Background,dispatcher){Interval=TimeSpan.FromMilliseconds(200)};
        debounce.Tick+=Tick;
        foreach(string path in ManagedDesktopFiles.DesktopRoots().Distinct(StringComparer.OrdinalIgnoreCase)){
            if(!Directory.Exists(path))continue;
            FileSystemWatcher? watch=null;
            try{
                watch=new(path){IncludeSubdirectories=false,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.Attributes};
                watch.Created+=Changed;watch.Deleted+=Changed;watch.Renamed+=Renamed;watch.Changed+=Changed;watch.Error+=Error;watch.EnableRaisingEvents=true;watches.Add(watch);
            }catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){watch?.Dispose();unavailable?.Invoke(error.GetType().Name);}
        }
    }
    private void Changed(object sender,FileSystemEventArgs args)=>Queue();
    private void Renamed(object sender,RenamedEventArgs args)=>Queue();
    private void Error(object sender,ErrorEventArgs args)=>Queue();
    private void Queue()
    {
        if(disposed||dispatcher.HasShutdownStarted||Interlocked.Exchange(ref queued,1)!=0)return;
        dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
            Interlocked.Exchange(ref queued,0);if(disposed)return;debounce.Stop();debounce.Start();
        }));
    }
    private void Tick(object? sender,EventArgs args){debounce.Stop();if(!disposed)notify();}
    public void Dispose()
    {
        if(disposed)return;disposed=true;debounce.Stop();debounce.Tick-=Tick;
        foreach(var watch in watches){watch.EnableRaisingEvents=false;watch.Created-=Changed;watch.Deleted-=Changed;watch.Renamed-=Renamed;watch.Changed-=Changed;watch.Error-=Error;watch.Dispose();}watches.Clear();
    }
}
