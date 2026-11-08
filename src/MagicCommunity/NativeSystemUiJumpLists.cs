using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession{
    private sealed record JumpCache(string Fingerprint,long At,long Version,NativeJumpLists.Result Data);
    private readonly object jumpGate=new();private readonly Dictionary<string,JumpCache> jumpLists=new(StringComparer.Ordinal);private long jumpVersion;private int jumpPending,jumpAction;
    private static string JumpExecutable(JsonElement row)=>Text(row,"iconExecutable",Text(Object(row,"nativeLaunch"),"executable",Text(row,"path")));
    private static string JumpFingerprint(JsonElement row)=>string.Join('\n',Text(row,"path"),Text(row,"umid"),JumpExecutable(row));
    private static bool JumpCanOpen(JsonElement row)=>Text(row,"umid").Contains('!')||NativeJumpLists.LocalPath(JumpExecutable(row))&&Path.GetExtension(JumpExecutable(row)).Equals(".exe",StringComparison.OrdinalIgnoreCase);
    private static JsonElement JumpRequest(JsonElement row,string? aumid=null)=>JsonSerializer.SerializeToElement(new{requestId=Guid.NewGuid().ToString("N"),aumid=aumid??Text(row,"umid"),shortcut=Text(row,"path"),executable=JumpExecutable(row),window=(long)Number(Array(row,"windows").FirstOrDefault(),"hwnd")});
    private void ProjectJumpLists(JsonObject view){
        lock(jumpGate)foreach(string surface in new[]{"toolbar","dock"})foreach(string section in new[]{"left","center","right"})foreach(var row in view[surface]![section]!.AsArray().OfType<JsonObject>()){
            row.Remove("jumpRecent");row.Remove("jumpTasks");row.Remove("jumpPinned");row.Remove("jumpFrequent");string id=row["id"]?.GetValue<string>()??"";if(!jumpLists.TryGetValue(id,out var cached)||Environment.TickCount64-cached.At>60000)continue;
            var value=JsonSerializer.SerializeToElement(row);if(Text(value,"type")!="AppOrFile"||JumpFingerprint(value)!=cached.Fingerprint||!JumpCanOpen(value))continue;
            if(cached.Data.Tasks.Length>0)row["jumpTasks"]=new JsonArray(cached.Data.Tasks.Select(x=>(JsonNode)new JsonObject{["id"]=cached.Version+":"+x.Id,["label"]=x.Label}).ToArray());
            if(cached.Data.Recent.Length>0)row["jumpRecent"]=new JsonArray(cached.Data.Recent.Select(x=>(JsonNode)new JsonObject{["id"]=cached.Version+":"+x.Id,["label"]=x.Label}).ToArray());
            if(cached.Data.Pinned.Length>0)row["jumpPinned"]=new JsonArray(cached.Data.Pinned.Select(x=>(JsonNode)new JsonObject{["id"]=cached.Version+":"+x.Id,["label"]=x.Label}).ToArray());
            if(cached.Data.Frequent.Length>0)row["jumpFrequent"]=new JsonArray(cached.Data.Frequent.Select(x=>(JsonNode)new JsonObject{["id"]=cached.Version+":"+x.Id,["label"]=x.Label}).ToArray());
        }
    }
    private async Task<NativeJumpLists.Result> QueryJump(JsonElement body,bool openPackage=false,string? destination=null){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var helper=new DesktopMenuHost(deployment.Root,reuseQueries:false);
        JsonElement result;try{result=JsonSerializer.SerializeToElement(await helper.Route(openPackage?"/desktop-jump-open":"/desktop-jump-list",body,timeout.Token,destination:destination));}catch(OperationCanceledException)when(openPackage&&!stop.IsCancellationRequested){throw new InvalidOperationException("Windows 未在限时内确认打开；请先检查目标应用，避免重复操作。");}
        if(openPackage)return new("",[],0);
        return result.Deserialize<NativeJumpLists.Result>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException();
    }
    private async Task PrefetchJump(JsonElement row){
        if(!JumpCanOpen(row))return;string id=Text(row,"id"),fingerprint=JumpFingerprint(row);
        lock(jumpGate)if(jumpLists.TryGetValue(id,out var cached)&&cached.Fingerprint==fingerprint&&Environment.TickCount64-cached.At<60000)return;
        if(Interlocked.CompareExchange(ref jumpPending,1,0)!=0)return;
        var clock=Stopwatch.StartNew();try{
            NativeJumpLists.Result result;
            try{result=await QueryJump(JumpRequest(row));}
            catch(Exception e)when(e is not OutOfMemoryException){if(stop.IsCancellationRequested)return;result=new("",[],0);log("independent_jump_list_unavailable",new{type=e.GetType().Name});}
            if(stop.IsCancellationRequested||Offered(id) is not {} current||JumpFingerprint(current)!=fingerprint)return;
            lock(jumpGate){if(!jumpLists.ContainsKey(id)&&jumpLists.Count>=8)jumpLists.Remove(jumpLists.MinBy(p=>p.Value.At).Key);jumpLists[id]=new(fingerprint,Environment.TickCount64,++jumpVersion,result);}
            log("independent_jump_list_read",new{count=result.Recent.Length,tasks=result.Tasks.Length,pinned=result.Pinned.Length,frequent=result.Frequent.Length,shellMs=result.Milliseconds,totalMs=clock.Elapsed.TotalMilliseconds,scope=result.Source});
            if(CurrentView.ValueKind==JsonValueKind.Object)await Publish(JsonNode.Parse(CurrentView.GetRawText())!.AsObject(),stop.Token);
        }catch(Exception e)when(e is not OutOfMemoryException){if(!stop.IsCancellationRequested)log("independent_jump_projection_failed",new{type=e.GetType().Name});}finally{Volatile.Write(ref jumpPending,0);}
    }
    private async Task OpenJump(JsonElement row,string offeredId){
        if(Interlocked.CompareExchange(ref jumpAction,1,0)!=0){await Notice(new InvalidOperationException("上一项跳转操作正在执行。"));return;}
        try{
            JumpCache cache;NativeJumpLists.Entry? document;NativeJumpLists.TaskEntry? task;string id=Text(row,"id");
            lock(jumpGate){cache=jumpLists.GetValueOrDefault(id)??throw new InvalidDataException("最近项目菜单已过期。");if(cache.Fingerprint!=JumpFingerprint(row)||Environment.TickCount64-cache.At>60000||!JumpCanOpen(row))throw new InvalidDataException("应用入口已变化。");task=cache.Data.Tasks.SingleOrDefault(x=>cache.Version+":"+x.Id==offeredId);document=cache.Data.Recent.Concat(cache.Data.Pinned).Concat(cache.Data.Frequent).SingleOrDefault(x=>cache.Version+":"+x.Id==offeredId);}
            if(task is not null){await QueryJump(JumpRequest(row,cache.Data.Aumid),true,task.Id);return;}
            if(document is null)throw new InvalidDataException("此项目不在当前菜单中。");
            if(cache.Data.Aumid==NativeJumpLists.ExplorerId){await QueryJump(JumpRequest(row,cache.Data.Aumid),true,document.Id);return;}
            // Re-read public Windows data at click time. Renderer never supplies a path.
            var fresh=await QueryJump(JumpRequest(row,cache.Data.Aumid));var current=Offered(id);
            if(current is null||JumpFingerprint(current.Value)!=cache.Fingerprint||!fresh.Recent.Any(x=>x.Id==document.Id&&StringComparer.OrdinalIgnoreCase.Equals(x.Path,document.Path)))throw new InvalidDataException("最近项目已变化，请重新打开菜单。");
            if(cache.Data.Aumid.Contains('!'))await QueryJump(JumpRequest(row,cache.Data.Aumid),true,document.Id);
            else{
                string executable=JumpExecutable(row);if(!NativeJumpLists.LocalPath(executable)||!File.Exists(executable))throw new FileNotFoundException("应用已移动。");
                var start=new ProcessStartInfo(executable){UseShellExecute=true};var launch=Object(row,"nativeLaunch");
                if(launch.ValueKind==JsonValueKind.Object&&launch.TryGetProperty("arguments",out var arguments)){if(arguments.ValueKind==JsonValueKind.Array)foreach(var arg in arguments.EnumerateArray())start.ArgumentList.Add(arg.GetString()??"");else if(arguments.ValueKind==JsonValueKind.String)foreach(string arg in SplitArguments(arguments.GetString()??""))start.ArgumentList.Add(arg);}
                start.ArgumentList.Add(document.Path);string working=Text(launch,"workingDirectory");if(Directory.Exists(working))start.WorkingDirectory=working;Process.Start(start)?.Dispose();
            }
        }catch(Exception e)when(e is not OutOfMemoryException){if(!stop.IsCancellationRequested)await Notice(e);}
        finally{Volatile.Write(ref jumpAction,0);}
    }
    internal static string[] SplitArguments(string arguments){
        if(arguments.Length>32760||arguments.Contains('\0'))throw new InvalidDataException("启动参数无效。");if(arguments.Length==0)return [];
        IntPtr list=CommandLineToArgvW("Magic.exe "+arguments,out int count);if(list==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        try{return Enumerable.Range(1,count-1).Select(n=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(list,n*IntPtr.Size))??"").ToArray();}finally{LocalFree(list);}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CommandLineToArgvW(string line,out int count);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr value);
}
