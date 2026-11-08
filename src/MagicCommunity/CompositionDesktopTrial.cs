using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace MagicCommunity;

// Development-only broker. Native code receives fixture references, never a
// formal desktop bridge or arbitrary file-operation API. All mutations return
// through NativeDesktopTrial's existing revision, path and transaction checks.
public sealed class CompositionDesktopTrial : IDisposable
{
    private static readonly JsonSerializerOptions Wire=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly NativeDesktopTrial trial;
    private readonly NamedPipeServerStream pipe;
    private readonly SemaphoreSlim sendGate=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private readonly HashSet<string> pendingOutbound=new(StringComparer.Ordinal);
    private readonly Dispatcher dispatcher;
    private readonly bool synthetic;
    private Process? child;
    private bool disposed,connected;
    private const int MaxPacket=1024*1024;
    private CompositionDesktopTrial(string root,string directory,bool synthetic)
    {
        this.synthetic=synthetic;dispatcher=Dispatcher.CurrentDispatcher;
        trial=new(root,directory,"Win32 / D2D / DirectWrite / Windows.UI.Composition",40);
        pipe=new(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        trial.IconsChanged+=OnIconsReady;
    }
    private object Snapshot()=>new{
        session=trial.Session,revision=trial.State.Revision,syntheticIcons=synthetic,fixtureRoot=trial.Root,
        boards=trial.State.Boards.Select(board=>new{
            board.Id,board.Name,screen=trial.Screens.FirstOrDefault(screen=>screen.Id==board.Id),
            placements=board.Grid?.Placements.Select(pair=>new{key=pair.Key,pair.Value.X,pair.Value.Y,pair.Value.W,pair.Value.H}),
            tiles=NativeDesktopTrial.DesktopTiles(board).Select(tile=>new{
                tile.Key,tile.Name,target=tile.Item?.Target,folderId=tile.Folder?.Id,
                items=tile.Folder?.Items.Select(item=>new{key=DesktopGridActions.ItemKey(item),item.Name,item.Target})
            })
        })
    };
    private NativeDesktopTrial.Tile FindTile(string boardId,string? folderId,string key)
    {
        var board=trial.Board(boardId);
        var tiles=folderId is null?NativeDesktopTrial.DesktopTiles(board):NativeDesktopTrial.FolderTiles(board.Boxes.Single(box=>box.Id==folderId));
        return tiles.Single(tile=>tile.Key==key);
    }
    private static string Required(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw new InvalidDataException("Missing "+name);
    private static string? Optional(JsonElement value,string name)=>value.TryGetProperty(name,out var property)&&property.ValueKind==JsonValueKind.String?property.GetString():null;
    private NativeDesktopTrial.DragPayload Drag(JsonElement value)=>JsonSerializer.Deserialize<NativeDesktopTrial.DragPayload>(value.GetProperty("drag"),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Missing drag payload");
    private object Execute(JsonElement request)
    {
        string op=Required(request,"op");long id=request.GetProperty("id").GetInt64();object? result=null;
        long before=trial.State.Revision;
        try{
            if(op is not "snapshot" and not "telemetry" and not "finishOutbound"){
                if(Required(request,"session")!=trial.Session||request.GetProperty("revision").GetInt64()!=trial.State.Revision)throw new LayoutEditException("布局版本已变化，请重试。");
            }
            switch(op){
                case "snapshot":break;
                case "rename":{
                    string board=Required(request,"board"),key=Required(request,"key");string? folder=Optional(request,"folder");
                    result=new{key=trial.Rename(board,folder,FindTile(board,folder,key),Required(request,"name"))};break;
                }
                case "open":{
                    var tile=FindTile(Required(request,"board"),Optional(request,"folder"),Required(request,"key"));
                    if(tile.Item is null)throw new InvalidDataException("Folder opening belongs to the renderer");trial.Open(tile.Item);break;
                }
                case "moveAuto":{
                    var drag=Drag(request);trial.ValidateDrag(drag);string destination=Required(request,"destination");
                    var source=FindTile(drag.Board,drag.Folder,drag.Anchor);var slot=DesktopGridActions.ReserveSpace(trial.Board(destination),source.Folder?.Columns??1,source.Folder?.Rows??1);
                    trial.Move(drag,destination,null,slot.X,slot.Y);break;
                }
                case "reorder":trial.Reorder(Drag(request),Required(request,"target"));break;
                case "move":trial.Move(Drag(request),Required(request,"destination"),Optional(request,"destinationFolder"),request.GetProperty("x").GetInt32(),request.GetProperty("y").GetInt32());break;
                case "prepareOutbound":{
                    var intents=trial.PrepareOutbound(Drag(request));foreach(string item in intents)pendingOutbound.Add(item);result=new{intents};break;
                }
                case "finishOutbound":{
                    var intents=request.GetProperty("intents").EnumerateArray().Select(value=>value.GetString()??"").ToArray();
                    if(intents.Length>256||intents.Distinct().Count()!=intents.Length||intents.Any(value=>!pendingOutbound.Contains(value)))throw new InvalidDataException("Invalid outbound receipt");
                    trial.FinishOutbound(intents);foreach(string item in intents)pendingOutbound.Remove(item);break;
                }
                case "icons":{
                    string board=Required(request,"board");string? folder=Optional(request,"folder");double dpi=Math.Clamp(request.GetProperty("dpi").GetDouble(),1,3);
                    var keys=request.GetProperty("keys").EnumerateArray().Select(value=>value.GetString()??"").ToArray();if(keys.Length>16)throw new InvalidDataException("Icon batch bound exceeded");
                    result=new{icons=keys.Distinct().Select(key=>{var tile=FindTile(board,folder,key);return new{key,png=synthetic||tile.Item is null?null:trial.IconData(tile.Item,dpi)};}).ToArray()};break;
                }
                case "telemetry":{
                    string phase=Required(request,"phase");if(phase.Length>80)throw new InvalidDataException("Phase too long");
                    string detail=request.TryGetProperty("detail",out var data)?data.GetRawText():"{}";if(detail.Length>32768)throw new InvalidDataException("Telemetry too large");
                    trial.Record("composition-"+phase,JsonSerializer.Deserialize<JsonElement>(detail));
                    if(phase.StartsWith("benchmark-",StringComparison.Ordinal))StateStore.AtomicWrite(Path.Combine(trial.Root,"phase.json"),JsonSerializer.Serialize(new{phase,at=DateTimeOffset.Now},Wire),null);
                    break;
                }
                default:throw new InvalidDataException("Unsupported trial operation");
            }
            return new{id,ok=true,result,snapshot=op=="snapshot"||trial.State.Revision!=before?Snapshot():null};
        }catch(Exception error)when(error is not OutOfMemoryException){
            trial.Record("composition-operation-error",new{op,error=error.Message});return new{id,ok=false,error=error.Message,snapshot=Snapshot()};
        }
    }
    private void OnIconsReady(){if(connected&&!disposed)_=Send(new{type="icons-ready"});}
    private async Task Send(object value)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value,Wire);if(bytes.Length>MaxPacket)throw new InvalidDataException("Packet bound exceeded");
        await sendGate.WaitAsync(stop.Token);
        try{byte[] length=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(length,bytes.Length);await pipe.WriteAsync(length,stop.Token);await pipe.WriteAsync(bytes,stop.Token);await pipe.FlushAsync(stop.Token);}
        catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){if(!disposed)stop.Cancel();}
        finally{sendGate.Release();}
    }
    private async Task Serve()
    {
        try{
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);startup.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(startup.Token);
            if(child is null||!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid)||pid!=child.Id)throw new InvalidDataException("Pipe client is not the renderer child");
            connected=true;await Send(new{type="initial",snapshot=Snapshot()});
            while(!stop.IsCancellationRequested){
                byte[] header=new byte[4];await pipe.ReadExactlyAsync(header,stop.Token);int length=BinaryPrimitives.ReadInt32LittleEndian(header);
                if(length is <2 or >MaxPacket)throw new InvalidDataException("Invalid packet length");
                byte[] bytes=new byte[length];await pipe.ReadExactlyAsync(bytes,stop.Token);using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=32});var request=json.RootElement.Clone();
                object response=await dispatcher.InvokeAsync(()=>Execute(request));await Send(response);
            }
        }catch(Exception error)when(error is not OutOfMemoryException){if(!disposed)trial.Record("composition-pipe-end",new{error=error.Message});}
        finally{if(!disposed)await dispatcher.InvokeAsync(()=>{trial.Record("composition-server-stop",new{childExited=child?.HasExited,childExitCode=child?.HasExited==true?child.ExitCode:(int?)null});Application.Current.Shutdown();});}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;trial.IconsChanged-=OnIconsReady;stop.Cancel();pipe.Dispose();
        if(pendingOutbound.Count>0){try{trial.FinishOutbound(pendingOutbound.ToArray());}catch(Exception error){trial.Record("outbound-recovery-error",new{error=error.Message});}}
        trial.Dispose();child?.Dispose();
    }
    public static int Run(string root,string? directory,string executable,bool allDisplays,bool synthetic,int cycles)
    {
        root=Path.GetFullPath(root);executable=Path.GetFullPath(executable);
        if(!File.Exists(Path.Combine(root,".git")))throw new InvalidOperationException("Composition trial requires the isolated Git worktree");
        string builds=Path.Combine(root,"artifacts","composition-trial-build");
        if(!executable.StartsWith(builds+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(executable)||Path.GetFileName(executable)!="MagicCompositionTrial.exe")throw new InvalidOperationException("Unexpected native renderer path");
        string stamp=Path.Combine(Path.GetDirectoryName(executable)!,"build.json");using var receipt=JsonDocument.Parse(File.ReadAllText(stamp));
        if(receipt.RootElement.GetProperty("root").GetString()!=root||!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).Equals(receipt.RootElement.GetProperty("nativeSha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Native renderer hash or worktree mismatch");
        if(cycles!=0&&(cycles<2||cycles>100||allDisplays))throw new ArgumentException("Benchmark requires one window and 2-100 cycles");
        directory??=Path.Combine(root,"artifacts","native-desktop-trial",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-composition-"+Guid.NewGuid().ToString("N")[..8]);
        string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..24];
        using var mutex=new Mutex(false,"Local\\MagicNativeTrial-"+identity);bool owns;try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
        if(!owns)throw new InvalidOperationException("Trial directory already in use");
        try{
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};using var broker=new CompositionDesktopTrial(root,directory,synthetic);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!};
            foreach(string arg in new[]{"--pipe",broker.pipeName,"--parent",Environment.ProcessId.ToString(),"--parent-created",Process.GetCurrentProcess().StartTime.ToFileTimeUtc().ToString(),"--cycles",cycles.ToString()})start.ArgumentList.Add(arg);
            if(allDisplays)start.ArgumentList.Add("--all-displays");
            broker.child=Process.Start(start)??throw new IOException("Renderer failed to start");broker.child.EnableRaisingEvents=true;
            broker.child.Exited+=(_,_)=>app.Dispatcher.BeginInvoke(new Action(()=>{broker.trial.Record("composition-child-exit",new{exitCode=broker.child.ExitCode});app.Shutdown();}));
            StateStore.AtomicWrite(Path.Combine(broker.trial.Root,"composition-session.json"),JsonSerializer.Serialize(new{at=DateTimeOffset.Now,backendPid=Environment.ProcessId,rendererPid=broker.child.Id,renderer=executable,syntheticIcons=synthetic,cycles,scope="Isolated fixture windows; no formal desktop takeover"},Wire),null);
            _=broker.Serve();return app.Run();
        }finally{mutex.ReleaseMutex();}
    }
    public static int CheckFixtures(string root,string report)
    {
        root=Path.GetFullPath(root);report=Path.GetFullPath(report);
        if(!File.Exists(Path.Combine(root,".git"))||!report.StartsWith(Path.Combine(root,"artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||File.Exists(report))throw new InvalidOperationException("Fixture checks require a new report inside this worktree artifacts");
        string directory=Path.Combine(root,"artifacts","native-desktop-trial",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-composition-check-"+Guid.NewGuid().ToString("N")[..8]);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};using var broker=new CompositionDesktopTrial(root,directory,true);
        var checks=new List<object>();bool all=true;long requestId=0;
        void Check(string name,Func<bool> test){bool pass=false;string? error=null;try{pass=test();}catch(Exception e){error=e.ToString();}checks.Add(new{name,pass,error});all&=pass;}
        JsonElement Request(string op,Dictionary<string,object?>? data=null,long? revision=null){
            data??=new();data["op"]=op;data["id"]=++requestId;data["session"]=broker.trial.Session;data["revision"]=revision??broker.trial.State.Revision;
            return JsonSerializer.SerializeToElement(broker.Execute(JsonSerializer.SerializeToElement(data,Wire)),Wire);
        }
        bool Ok(JsonElement response)=>response.GetProperty("ok").GetBoolean();
        var first=broker.trial.State.Boards[0];string board=first.Id,other=broker.trial.State.Boards[1].Id;string original=first.RootItems[0].Target;string renamed="";
        Check("initial-240-and-40-fixtures",()=>first.Boxes.Any(box=>box.Items.Count==240)&&first.Boxes.Any(box=>box.Items.Count==40)&&first.RootItems.Count==6);
        Check("snapshot-shape",()=>Ok(Request("snapshot")));
        Check("unicode-file-rename",()=>{var response=Request("rename",new(){["board"]=board,["key"]=DesktopGridActions.ItemKey(first.RootItems[0]),["name"]="原生中文重命名.txt"});renamed=response.GetProperty("result").GetProperty("key").GetString()!;return Ok(response)&&!File.Exists(original)&&File.Exists(Path.Combine(Path.GetDirectoryName(original)!,"原生中文重命名.txt"));});
        Check("rename-collision-preserves-existing-and-chooses-unique-name",()=>{var sibling=broker.trial.Board(board).RootItems[1];string path=sibling.Target;byte[] before=File.ReadAllBytes(path);var response=Request("rename",new(){["board"]=board,["key"]=renamed,["name"]=sibling.Name});if(!Ok(response))return false;renamed=response.GetProperty("result").GetProperty("key").GetString()!;var changed=broker.trial.Board(board).RootItems.Single(item=>DesktopGridActions.ItemKey(item)==renamed);return File.Exists(path)&&before.SequenceEqual(File.ReadAllBytes(path))&&changed.Target!=path&&File.Exists(changed.Target)&&changed.Name.Contains(" (2)",StringComparison.Ordinal);});
        Check("path-traversal-name-rejected",()=>{long revision=broker.trial.State.Revision;var response=Request("rename",new(){["board"]=board,["key"]=renamed,["name"]="../outside.txt"});return !Ok(response)&&response.GetProperty("error").GetString()=="文件名无效。"&&revision==broker.trial.State.Revision;});
        Check("stale-revision-rejected",()=>!Ok(Request("rename",new(){["board"]=board,["key"]=renamed,["name"]="stale.txt"},broker.trial.State.Revision-1)));
        Check("outside-target-rejected",()=>{try{broker.trial.GuardTarget(Path.Combine(root,"app.current.json"));return false;}catch(IOException){return true;}});
        string destinationFolder=broker.trial.Board(board).Boxes.Single(box=>box.Items.Count==40).Id;
        Check("multi-item-move-into-folder",()=>{var b=broker.trial.Board(board);var keys=b.RootItems.Take(2).Select(DesktopGridActions.ItemKey).ToArray();var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,board,null,keys,keys[0]);return Ok(Request("move",new(){["drag"]=drag,["destination"]=board,["destinationFolder"]=destinationFolder,["x"]=0,["y"]=0}))&&broker.trial.Board(board).RootItems.Count==4&&broker.trial.Board(board).Boxes.Single(box=>box.Id==destinationFolder).Items.Count==42;});
        Check("folder-single-drop-swaps-only-two-slots",()=>{
            var box=broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder);var before=box.Items.Select(DesktopGridActions.ItemKey).ToArray();
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,board,destinationFolder,[before[0]],before[0]);
            var response=Request("reorder",new(){["drag"]=drag,["target"]=before[3]});(before[0],before[3])=(before[3],before[0]);
            return Ok(response)&&before.SequenceEqual(broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items.Select(DesktopGridActions.ItemKey));});
        Check("folder-drop-on-selection-does-not-change-revision-or-snapshot",()=>{
            var key=DesktopGridActions.ItemKey(broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items[0]);long revision=broker.trial.State.Revision;
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,revision,board,destinationFolder,[key],key);var response=Request("reorder",new(){["drag"]=drag,["target"]=key});
            return Ok(response)&&revision==broker.trial.State.Revision&&response.GetProperty("snapshot").ValueKind==JsonValueKind.Null;});
        Check("folder-drop-unknown-target-rejected-atomically",()=>{
            var key=DesktopGridActions.ItemKey(broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items[0]);long revision=broker.trial.State.Revision;
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,revision,board,destinationFolder,[key],key);
            return !Ok(Request("reorder",new(){["drag"]=drag,["target"]="item:missing"}))&&revision==broker.trial.State.Revision;});
        Check("folder-multiselect-drop-preserves-members-and-relative-order",()=>{
            var before=broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items.Select(DesktopGridActions.ItemKey).ToList();var keys=new[]{before[0],before[2]};string target=before[6];
            var expected=before.Where(k=>!keys.Contains(k)).ToList();expected.InsertRange(expected.IndexOf(target),keys);
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,board,destinationFolder,keys,keys[0]);
            return Ok(Request("reorder",new(){["drag"]=drag,["target"]=target}))&&expected.SequenceEqual(broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items.Select(DesktopGridActions.ItemKey));});
        Check("folder-stale-payload-reorder-rejected",()=>{
            var keys=broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items.Take(2).Select(DesktopGridActions.ItemKey).ToArray();long revision=broker.trial.State.Revision;
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,revision-1,board,destinationFolder,[keys[0]],keys[0]);
            return !Ok(Request("reorder",new(){["drag"]=drag,["target"]=keys[1]}))&&revision==broker.trial.State.Revision;});
        Check("folder-boundary-out-request-moves-selection-to-desktop",()=>{
            var box=broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder);int count=box.Items.Count,rootCount=broker.trial.Board(board).RootItems.Count;var keys=box.Items.Take(2).Select(DesktopGridActions.ItemKey).ToArray();
            var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,board,destinationFolder,keys,keys[0]);
            return Ok(Request("move",new(){["drag"]=drag,["destination"]=board,["x"]=6,["y"]=4}))&&broker.trial.Board(board).Boxes.Single(b=>b.Id==destinationFolder).Items.Count==count-2&&broker.trial.Board(board).RootItems.Count==rootCount+2&&keys.All(k=>broker.trial.Board(board).RootItems.Any(item=>DesktopGridActions.ItemKey(item)==k));});
        string crossKey="";
        Check("cross-board-move-reuses-layout-transaction",()=>{var item=broker.trial.Board(board).Boxes.Single(box=>box.Id==destinationFolder).Items[0];crossKey=DesktopGridActions.ItemKey(item);var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,board,destinationFolder,[crossKey],crossKey);return Ok(Request("moveAuto",new(){["drag"]=drag,["destination"]=other}))&&broker.trial.Board(other).RootItems.Any(row=>DesktopGridActions.ItemKey(row)==crossKey)&&!broker.trial.Board(board).Boxes.Single(box=>box.Id==destinationFolder).Items.Any(row=>DesktopGridActions.ItemKey(row)==crossKey);});
        Check("outbound-preparation-and-cancel-recovery",()=>{var drag=new NativeDesktopTrial.DragPayload(broker.trial.Session,broker.trial.State.Revision,other,null,[crossKey],crossKey);var response=Request("prepareOutbound",new(){["drag"]=drag});var intents=response.GetProperty("result").GetProperty("intents").EnumerateArray().Select(item=>item.GetString()!).ToArray();bool rejects=!Ok(Request("finishOutbound",new(){["intents"]=new[]{"unowned-receipt"}}));return Ok(response)&&response.GetProperty("snapshot").ValueKind==JsonValueKind.Null&&rejects&&Ok(Request("finishOutbound",new(){["intents"]=intents}))&&broker.trial.Board(other).RootItems.Any(row=>DesktopGridActions.ItemKey(row)==crossKey);});
        Check("virtual-folder-rename",()=>{var box=broker.trial.Board(board).Boxes.Single(box=>box.Id==destinationFolder);return Ok(Request("rename",new(){["board"]=board,["key"]=DesktopGridActions.FolderKey(box),["name"]="中文隔离文件夹"}))&&broker.trial.Board(board).Boxes.Single(row=>row.Id==destinationFolder).Name=="中文隔离文件夹";});
        Check("unexposed-operation-rejected",()=>!Ok(Request("delete")));
        Check("fixture-state-still-valid",()=>{StateStore.Validate(broker.trial.State);return broker.trial.State.Boards.SelectMany(b=>b.Boxes).Any(box=>box.Items.Count==240);});
        StateStore.AtomicWrite(report,JsonSerializer.Serialize(new{allPass=all,fixtureRoot=directory,checks,scope="Isolated broker/layout/file-transaction checks; no native input, screenshots, IME composition, actual OLE target, real multi-monitor or formal desktop acceptance"},StateStore.Json),null);
        app.Shutdown();return all?0:1;
    }
    private readonly string pipeName="MagicCompositionTrial-"+Guid.NewGuid().ToString("N");
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
}
