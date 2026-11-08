using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagicCommunity;

// A separate, opt-in renderer candidate. It cannot acquire the Explorer lease,
// load Seelen, import a live layout, or open/mutate a target outside its fixtures.
public sealed class NativeDesktopTrial : IDisposable
{
    public const string DragFormat="MagicCommunity.NativeDesktopTrial.v1";
    public sealed record DragPayload(string Session,long Revision,string Board,string? Folder,string[] Keys,string Anchor,string? Lease=null,string? Page=null);
    public sealed record Tile(string Key,string Name,DesktopItem? Item=null,Box? Folder=null,int Index=0,double Width=96);
    public sealed record Row(Tile[] Items);
    private readonly StateStore store;
    private readonly DesktopIcons icons;
    private readonly Dictionary<string,BitmapSource> decoded=new(StringComparer.Ordinal);
    private readonly Queue<string> decodedOrder=new();
    private long decodedBytes;
    private readonly object diagnosticGate=new();
    private bool busy,disposed;
    private readonly string renderer;
    private readonly int smallFolderItems;
    public string Root { get; }
    public string Session { get; }=Guid.NewGuid().ToString("N");
    public LayoutState State { get; private set; }
    public IReadOnlyList<Display> Screens { get; }
    public event Action? Changed;
    public event Action? IconsChanged;
    public bool Busy=>busy;
    public NativeDesktopTrial(string projectRoot,string? directory,string renderer="WPF retained drawing / virtualized rows",int smallFolderItems=8)
    {
        this.renderer=renderer;this.smallFolderItems=Math.Clamp(smallFolderItems,1,240);
        string allowed=Path.GetFullPath(Path.Combine(projectRoot,"artifacts","native-desktop-trial"));
        Root=Path.GetFullPath(directory??Path.Combine(allowed,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Session[..8]));
        if(!Root.StartsWith(allowed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("试点数据必须位于本工作树 artifacts/native-desktop-trial 的独立子目录。");
        CheckAncestors(projectRoot,Root);
        bool exists=Directory.Exists(Root),marked=File.Exists(Path.Combine(Root,"trial.json"));
        if(exists&&!marked&&Directory.EnumerateFileSystemEntries(Root).Any())throw new InvalidOperationException("目录不是本试点创建，拒绝写入。");
        Directory.CreateDirectory(Root);Directory.CreateDirectory(Path.Combine(Root,"files"));
        Screens=Displays.Read();store=new(Path.Combine(Root,"data"));
        if(!marked){
            State=Seed();store.Save(State);
            StateStore.AtomicWrite(Path.Combine(Root,"trial.json"),JsonSerializer.Serialize(new{schemaVersion=1,kind="native-desktop-isolated-fixtures",projectRoot=Path.GetFullPath(projectRoot),createdAt=DateTimeOffset.Now},StateStore.Json),null);
        }
        else{
            using var marker=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"trial.json")));
            if(marker.RootElement.GetProperty("kind").GetString()!="native-desktop-isolated-fixtures"||!string.Equals(marker.RootElement.GetProperty("projectRoot").GetString(),Path.GetFullPath(projectRoot),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("试点目录所属工作树不匹配。");
            State=store.Load();if(store.ReadOnly)throw new InvalidDataException(store.Problem);
        }
        GuardState(State);DesktopFileChanges.Recover(Root,store,State);State=store.Load();GuardState(State);
        icons=new DesktopIcons(()=>{
            if(disposed)return;
            Application.Current.Dispatcher.BeginInvoke(new Action(()=>{if(!disposed)IconsChanged?.Invoke();}));
        });
        Record("session",new{pid=Environment.ProcessId,renderer,renderTier=renderer.StartsWith("WPF",StringComparison.Ordinal)?RenderCapability.Tier>>16:(int?)null,webView=false,desktopTakeover=false,boards=State.Boards.Count});
    }
    private static void CheckAncestors(string boundary,string target)
    {
        string root=Path.GetFullPath(boundary);
        for(var part=new DirectoryInfo(target);part is not null;part=part.Parent){
            if(part.Exists&&(part.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("试点目录不能经过链接或重解析点。");
            if(string.Equals(part.FullName,root,StringComparison.OrdinalIgnoreCase))return;
        }
        throw new IOException("试点目录越界。");
    }
    public string GuardTarget(string target)
    {
        string path=Path.GetFullPath(target),files=Path.Combine(Root,"files");
        if(!path.StartsWith(files+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("试点仅操作自己的隔离文件。");
        CheckAncestors(Root,path);if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("试点拒绝链接文件。");return path;
    }
    private void GuardState(LayoutState state)
    {
        StateStore.Validate(state);
        foreach(var item in state.Boards.SelectMany(board=>board.RootItems.Concat(board.Boxes.SelectMany(box=>box.Items)))){
            if(item.IsVirtual)throw new InvalidDataException("试点不接受 Shell 虚拟目标。");
            GuardTarget(item.Target);
        }
    }
    private LayoutState Seed()
    {
        var state=new LayoutState{SoftwareRendering=false};
        var names=Screens.Select(screen=>(screen.Id,screen.Label)).ToList();
        if(names.Count==0)names.Add(("trial-area-1","隔离显示区 1"));
        if(names.Count==1)names.Add(("trial-area-2","隔离显示区 2（虚拟目标区）"));
        foreach(var (id,label) in names){
            var board=new Board{Id=id,Name=label};int number=state.Boards.Count+1;
            DesktopItem FileItem(string name){string folder=Path.Combine(Root,"files",number.ToString());Directory.CreateDirectory(folder);string path=Path.Combine(folder,name+".txt");File.WriteAllText(path,"Magic 原生桌面试点隔离文件。可在试点中重命名、打开及拖放。\n");return new(){Name=Path.GetFileName(path),Target=path};}
            board.RootItems=Enumerable.Range(1,6).Select(i=>FileItem($"独立项目 {i:00}")).ToList();
            board.Boxes=[new(){Name="常用",Items=Enumerable.Range(1,smallFolderItems).Select(i=>FileItem($"常用项目 {i:00}")).ToList()},new(){Name="大文件夹",Columns=4,Rows=2,Items=Enumerable.Range(1,240).Select(i=>FileItem($"大文件夹项目 {i:000}")).ToList()},new(){Name="空文件夹"}];
            DesktopGridActions.Initialize(board,1000,650);state.Boards.Add(board);
        }
        return state;
    }
    public Board Board(string id)=>State.Boards.Single(board=>board.Id==id);
    public static Tile[] DesktopTiles(Board board)=>board.RootItems.Select(item=>new Tile(DesktopGridActions.ItemKey(item),item.Name,item)).Concat(board.Boxes.Select(box=>new Tile(DesktopGridActions.FolderKey(box),box.Name,Folder:box))).ToArray();
    public static Tile[] FolderTiles(Box box)=>box.Items.Select((item,index)=>new Tile(DesktopGridActions.ItemKey(item),item.Name,item,Index:index)).ToArray();
    public void ValidateDrag(DragPayload drag)
    {
        if(drag.Session!=Session||drag.Revision!=State.Revision||drag.Keys is null||drag.Keys.Length is <1 or >256||drag.Keys.Any(string.IsNullOrEmpty)||drag.Keys.Distinct(StringComparer.Ordinal).Count()!=drag.Keys.Length||!drag.Keys.Contains(drag.Anchor))throw new LayoutEditException("拖放来源或版本已变化，请重试。");
        var board=Board(drag.Board);
        if(drag.Keys.Any(key=>key.StartsWith("folder:",StringComparison.Ordinal))){
            if(drag.Keys.Length!=1||drag.Folder is not null||!board.Boxes.Any(box=>DesktopGridActions.FolderKey(box)==drag.Anchor))throw new LayoutEditException("文件夹拖放来源无效。");
        }else _=DesktopGroupBridgeActions.Resolve(board,drag.Keys,drag.Folder);
    }
    public DesktopItem[] Resolve(DragPayload drag)
    {
        ValidateDrag(drag);
        return DesktopGroupBridgeActions.Resolve(Board(drag.Board),drag.Keys,drag.Folder);
    }
    public void Move(DragPayload drag,string destination,string? folder,int x,int y)
    {
        ValidateDrag(drag);
        Commit(next=>{
            var source=next.Boards.Single(board=>board.Id==drag.Board);var target=next.Boards.Single(board=>board.Id==destination);
            if(drag.Keys.Length==1&&drag.Anchor.StartsWith("folder:",StringComparison.Ordinal)){
                if(folder is not null||drag.Folder is not null)throw new LayoutEditException("大文件夹不能嵌套。");
                if(source.Id==target.Id)DesktopGridActions.Place(source,drag.Anchor,x,y);
                else UnifiedDesktopGrid.Transfer(next,source,target,drag.Anchor,null,x,y);
            }else{
                _=Resolve(drag);
                DesktopGroupBridgeActions.Reference(next,source,target,drag.Keys,drag.Anchor,drag.Folder,folder,x,y);
            }
        });
        Record("layout-move",new{members=drag.Keys.Length,crossBoard=drag.Board!=destination,intoFolder=folder is not null});
    }
    public void Reorder(DragPayload drag,string targetKey)
    {
        ValidateDrag(drag);
        if(drag.Folder is null)throw new LayoutEditException("交换位置仅用于文件夹内部。");
        var items=Board(drag.Board).Boxes.Single(box=>box.Id==drag.Folder).Items;
        int target=items.FindIndex(item=>DesktopGridActions.ItemKey(item)==targetKey);
        if(target<0)throw new LayoutEditException("目标项目已变化。");
        if(drag.Keys.Contains(targetKey))return;
        Commit(next=>{
            var list=next.Boards.Single(board=>board.Id==drag.Board).Boxes.Single(box=>box.Id==drag.Folder).Items;
            if(drag.Keys.Length==1){int source=list.FindIndex(item=>DesktopGridActions.ItemKey(item)==drag.Anchor);(list[source],list[target])=(list[target],list[source]);}
            else{
                var members=drag.Keys.ToHashSet(StringComparer.Ordinal);
                var selected=list.Where(item=>members.Contains(DesktopGridActions.ItemKey(item))).ToList();
                int insertion=list.Take(target).Count(item=>!members.Contains(DesktopGridActions.ItemKey(item)));
                list.RemoveAll(item=>members.Contains(DesktopGridActions.ItemKey(item)));list.InsertRange(insertion,selected);
            }
        });
        Record("layout-reorder",new{members=drag.Keys.Length,folder=drag.Folder});
    }
    public string Rename(string boardId,string? folder,Tile tile,string name)
    {
        if(busy)throw new LayoutEditException("上一项操作尚未完成。");
        name=name.Trim();if(name.Length is <1 or >200)throw new LayoutEditException("名称长度无效。");
        if(tile.Folder is not null){Commit(next=>next.Boards.Single(board=>board.Id==boardId).Boxes.Single(box=>box.Id==tile.Folder.Id).Name=name);return tile.Key;}
        var payload=new DragPayload(Session,State.Revision,boardId,folder,[tile.Key],tile.Key);var item=Resolve(payload).Single();string source=GuardTarget(item.Target);string renamed=tile.Key;busy=true;
        try{
            var intent=DesktopFileChanges.Prepare(Root,"rename",source,null,name);GuardTarget(intent.Destination!);
            DesktopFileChanges.Execute("rename",source,intent.Destination);renamed=DesktopGridActions.ItemKey(new(){Target=intent.Destination!});
            // Existing durable recovery applies completed Shell operations exactly
            // once to the isolated StateStore; it never repeats the file operation.
            DesktopFileChanges.Recover(Root,store,State,includeExternal:false);State=store.Load();GuardState(State);
        }finally{busy=false;Changed?.Invoke();}
        return renamed;
    }
    public void Resize(string boardId,string folderId,int columns,int rows)=>Commit(next=>DesktopGridActions.Resize(next.Boards.Single(board=>board.Id==boardId),folderId,columns,rows));
    public void Open(DesktopItem item)
    {
        string path=GuardTarget(item.Target);if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("隔离项目已移动。",path);
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    private void Commit(Action<LayoutState> action)
    {
        if(busy)throw new LayoutEditException("上一项操作尚未完成。");busy=true;
        try{var next=StateStore.Copy(State);action(next);GuardState(next);next.Revision=State.Revision+1;store.Save(next);State=next;}
        finally{busy=false;}
        Changed?.Invoke();
    }
    public string[] PrepareOutbound(DragPayload drag)
    {
        var items=Resolve(drag);return items.Select(item=>DesktopFileChanges.PrepareExternalDrag(Root,GuardTarget(item.Target)).Id).ToArray();
    }
    public void FinishOutbound(string[] intents)
    {
        var next=StateStore.Copy(State);DesktopFileChanges.FinishMany(Root,store,next,intents);GuardState(next);State=next;Changed?.Invoke();
    }
    public BitmapSource? Icon(DesktopItem item,double dpi)
    {
        string? uri=icons.Get(item);if(uri is null)return null;
        int pixels=Math.Clamp((int)Math.Ceiling(48*dpi),48,144);string key=DesktopGridActions.ItemId(item)+":"+pixels;
        if(decoded.TryGetValue(key,out var image))return image;
        using var stream=new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',')+1)..]));
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=pixels;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
        long bytes=bitmap.PixelWidth*(long)bitmap.PixelHeight*4;
        while(decodedOrder.Count>0&&(decoded.Count>=128||decodedBytes+bytes>8*1024*1024)){
            string old=decodedOrder.Dequeue();if(decoded.Remove(old,out var previous))decodedBytes-=previous.PixelWidth*(long)previous.PixelHeight*4;
        }
        decoded[key]=bitmap;decodedOrder.Enqueue(key);decodedBytes+=bytes;return bitmap;
    }
    public string? IconData(DesktopItem item,double dpi)
    {
        GuardTarget(item.Target);var image=Icon(item,dpi);if(image is null)return null;
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var buffer=new MemoryStream();encoder.Save(buffer);
        return buffer.Length<=65536?Convert.ToBase64String(buffer.ToArray()):null;
    }
    public void Record(string phase,object detail)
    {
        if(disposed)return;
        try{lock(diagnosticGate)File.AppendAllText(Path.Combine(Root,"interaction.jsonl"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,timestamp=Stopwatch.GetTimestamp(),phase,detail})+Environment.NewLine);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){Debug.WriteLine("Trial diagnostic write failed: "+error.Message);}
    }
    public string Snapshot()
    {
        using var process=Process.GetCurrentProcess();string path=Path.Combine(Root,"resource-snapshot.json");
        StateStore.AtomicWrite(path,JsonSerializer.Serialize(new{at=DateTimeOffset.Now,pid=process.Id,renderer="native-wpf-trial",privateCommitBytes=process.PrivateMemorySize64,workingSetBytesIncludingShared=process.WorkingSet64,decodedIconBytes=decodedBytes,decodedIcons=decoded.Count,revision=State.Revision,scope="Single uncontrolled snapshot; working set includes shared pages; no GPU trace or performance acceptance."},StateStore.Json),null);return path;
    }
    public void Dispose(){if(disposed)return;Record("session-end",new{pid=Environment.ProcessId});disposed=true;icons.Dispose();decoded.Clear();decodedOrder.Clear();}
    public static int Run(string projectRoot,string? directory,bool allDisplays)
    {
        directory??=Path.Combine(projectRoot,"artifacts","native-desktop-trial",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
        string identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..24];
        using var mutex=new Mutex(false,"Local\\MagicNativeTrial-"+identity);bool owns;
        try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
        if(!owns)throw new InvalidOperationException("这个隔离试点目录已被另一个进程打开。");
        try{
        var app=new Application{ShutdownMode=ShutdownMode.OnLastWindowClose};
        using var trial=new NativeDesktopTrial(projectRoot,directory);
        var boards=allDisplays?trial.State.Boards:trial.State.Boards.Take(1);
        foreach(var board in boards){var window=new NativeDesktopTrialWindow(trial,board.Id);window.Show();}
        app.DispatcherUnhandledException+=(_,eventArgs)=>{trial.Record("error",new{error=eventArgs.Exception.ToString()});MessageBox.Show(eventArgs.Exception.Message,"原生桌面隔离试点");eventArgs.Handled=true;};
        return app.Run();
        }finally{mutex.ReleaseMutex();}
    }
}
