using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Magic.ToolbarExtension;
namespace MagicCommunity;

// Current-user, push-only extension broker. It never executes provider commands,
// owns windows, changes app layout on discovery, or shares private runtime IPC.
internal sealed class ToolbarExtensions : IAsyncDisposable
{
    internal const string SourcePrefix="Magic.ToolbarExtension/1:";
    internal const int MaximumDiscovered=64,MaximumEnabled=8;
    private sealed record Preference(string Id,string DisplayName,bool Enabled,string Image);
    internal sealed record DiscoveryIssue(string Id,string Code);
    internal sealed record DiscoveryReport(long Sequence,DateTimeOffset StartedUtc,DateTimeOffset CompletedUtc,
        double ElapsedMs,int Enumerated,int Accepted,int Rejected,bool LimitReached,string Status,string? Error,
        DiscoveryIssue[] Issues,DateTimeOffset? LastSuccessfulScanUtc);
    private DiscoveryReport discovery=new(0,default,default,0,0,0,0,false,"pending",null,[],null);
    private string lastDiscoveryLog="";private long lastDiscoveryLogAt;private int suppressedDiscoveryLogs;
    private sealed record Descriptor(ExtensionManifest Manifest,string Path);
    private sealed record Surface(string Session,long Revision,bool Mounted,bool Visible);
    private sealed class Slot(string id,string path,string session,string image){
        internal readonly string Id=id,Path=path,Session=session,Image=image;
        internal readonly CancellationTokenSource Stop=new();
        internal readonly Channel<bool> States=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
        internal readonly Dictionary<long,Surface> Surfaces=[];
        internal Task Work=Task.CompletedTask;
        internal ToolbarHostClient? Client;internal ToolbarSnapshot? Snapshot;
        internal bool Granted,Dormant;internal string Status="connecting";
        internal long RateStart;internal int RateCount,ActionPending;
    }
    private readonly object gate=new();
    private readonly string root,preferencesPath;
    private readonly Action changed;
    private readonly Action<string,string,string?> writePreferences;
    private readonly Action<string,object> log;
    private readonly CancellationTokenSource stop=new();
    private readonly SemaphoreSlim connecting=new(4,4);
    private readonly Channel<bool> changes=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private readonly Dictionary<string,Preference> preferences=new(StringComparer.Ordinal);
    private Dictionary<string,Descriptor> catalog=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Slot> slots=new(StringComparer.Ordinal);
    private HashSet<string> configured=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> statuses=new(StringComparer.Ordinal);
    private readonly List<Task> retiring=[];
    private bool toolbarPresent,preferencesReadOnly;
    private FileSystemWatcher? watcher;private Task worker=Task.CompletedTask;private int closed;
    internal ToolbarExtensions(string dataDirectory,Action changed,Action<string,object> log,Action<string,string,string?> writePreferences,string? discoveryRoot=null){
        root=Path.GetFullPath(discoveryRoot??DefaultDiscoveryRoot());
        preferencesPath=Path.Combine(dataDirectory,"extensions-v1.json");this.changed=changed;this.log=log;this.writePreferences=writePreferences;
        string setupStage="preferences";
        try{
            LoadPreferences();setupStage="directory";Directory.CreateDirectory(root);NoLink(root);
            setupStage="watcher";watcher=new(root,"manifest.json"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size};
            watcher.Created+=Watch;watcher.Changed+=Watch;watcher.Deleted+=Watch;watcher.Renamed+=Watch;watcher.Error+=(_,_)=>Pulse();watcher.EnableRaisingEvents=true;
            worker=Task.Run(Work);Pulse();
        }catch(Exception e)when(e is not OutOfMemoryException){
            var now=DateTimeOffset.UtcNow;discovery=new(0,now,now,0,0,0,0,false,"unavailable",setupStage+":"+DiscoveryError(e),[],null);
            log("toolbar_extensions_unavailable",new{stage=setupStage,code=discovery.Error});watcher?.Dispose();watcher=null;preferencesReadOnly=true;
        }
    }
    internal static string FromSource(string source)=>source.StartsWith(SourcePrefix,StringComparison.Ordinal)&&ToolbarProtocol.ValidId(source[SourcePrefix.Length..])?source[SourcePrefix.Length..]:"";
    internal static string RowId(string id)=>"extension:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24].ToLowerInvariant();
    private static void NoLink(string path){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Extension discovery link refused.");}
    internal static ExtensionManifest ReadManifest(string registryRoot,string path){
        string basePath=Path.GetFullPath(registryRoot).TrimEnd(Path.DirectorySeparatorChar),full=Path.GetFullPath(path);
        if(!full.StartsWith(basePath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="manifest.json")throw new InvalidDataException("Manifest outside discovery root.");
        var parent=Directory.GetParent(full)??throw new InvalidDataException();if(!string.Equals(parent.Parent?.FullName,basePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Manifest depth differs.");
        NoLink(basePath);NoLink(parent.FullName);NoLink(full);
        using var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(file.Length is <1 or >8192)throw new InvalidDataException("Manifest exceeds bound.");
        var manifest=JsonSerializer.Deserialize<ExtensionManifest>(file,new JsonSerializerOptions(ToolbarProtocol.Json){MaxDepth=8})??throw new InvalidDataException();
        ToolbarHostClient.ValidateManifest(manifest);
        if(parent.Name!=manifest.Id||manifest.DisplayName.Any(char.IsControl)||manifest.Version.Any(char.IsControl))throw new InvalidDataException("Manifest name differs.");
        return manifest;
    }
    internal static string ProviderImage(ExtensionManifest manifest){
        using var process=Process.GetProcessById(manifest.ProviderPid);using var current=Process.GetCurrentProcess();
        if(process.SessionId!=current.SessionId||process.StartTime.ToFileTimeUtc()!=manifest.ProviderCreated)throw new InvalidDataException("Provider session or creation time differs.");
        string image=process.MainModule?.FileName??throw new InvalidDataException("Provider image unavailable.");
        if(image.Length>4096||!Path.IsPathFullyQualified(image)||image.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidDataException("Provider image is not local.");
        using var file=File.OpenHandle(image,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var path=new StringBuilder(4096);uint count=GetFinalPathNameByHandle(file,path,(uint)path.Capacity,0);if(count==0||count>=path.Capacity)throw new InvalidDataException("Canonical provider image unavailable.");
        string final=path.ToString();if(final.StartsWith(@"\\?\",StringComparison.Ordinal))final=final[4..];
        if(!Path.IsPathFullyQualified(final)||final.StartsWith(@"UNC\",StringComparison.OrdinalIgnoreCase)||final.IndexOf(':',2)>=0)throw new InvalidDataException("Provider image escaped local file.");
        return final;
    }
    private void LoadPreferences(){
        if(!File.Exists(preferencesPath))return;NoLink(preferencesPath);if(new FileInfo(preferencesPath).Length>524288)throw new InvalidDataException("Extension preferences exceed bound.");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(preferencesPath),new JsonDocumentOptions{MaxDepth=8});if(doc.RootElement.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Extension preference version differs.");
        foreach(var p in doc.RootElement.GetProperty("entries").Deserialize<Preference[]>(ToolbarProtocol.Json)??[]){
            if(preferences.Count>=MaximumDiscovered||!ToolbarProtocol.ValidId(p.Id)||p.DisplayName.Length>128||p.DisplayName.Any(char.IsControl)||p.Image.Length>4096||!preferences.TryAdd(p.Id,p))throw new InvalidDataException("Extension preferences invalid.");
        }
        if(preferences.Values.Count(p=>p.Enabled)>MaximumEnabled)throw new InvalidDataException("Enabled extension bound.");
    }
    internal bool Enabled(string id){lock(gate)return preferences.TryGetValue(id,out var p)&&p.Enabled;}
    internal string Label(string id){lock(gate)return catalog.TryGetValue(id,out var d)?d.Manifest.DisplayName:preferences.GetValueOrDefault(id)?.DisplayName??id;}
    internal void SetEnabled(string id,bool enabled){
        if(!ToolbarProtocol.ValidId(id)||preferencesReadOnly)throw new InvalidOperationException("扩展配置不可用，原文件已保留。");
        Descriptor? descriptor;Preference? previous;lock(gate){descriptor=catalog.GetValueOrDefault(id);previous=preferences.GetValueOrDefault(id);if(enabled&&preferences.Values.Count(p=>p.Enabled&&p.Id!=id)>=MaximumEnabled)throw new InvalidOperationException("最多同时启用 8 个外部扩展。");}
        string image=previous?.Image??"",name=previous?.DisplayName??id;
        if(enabled){if(descriptor is null)throw new InvalidOperationException("扩展提供方尚未运行。");var manifest=ReadManifest(root,descriptor.Path);if(manifest.Id!=id)throw new InvalidDataException();image=ProviderImage(manifest);name=manifest.DisplayName;}
        lock(gate){
            var next=new Dictionary<string,Preference>(preferences,StringComparer.Ordinal){[id]=new(id,name,enabled,image)};
            if(next.Count>MaximumDiscovered)throw new InvalidOperationException("外部扩展已达到登记上限。");
            string json=JsonSerializer.Serialize(new{schemaVersion=1,entries=next.Values.OrderBy(p=>p.Id,StringComparer.Ordinal)},ToolbarProtocol.Json);
            if(Encoding.UTF8.GetByteCount(json)>524288)throw new InvalidDataException("Extension preferences exceed bound.");
            if(File.Exists(preferencesPath))NoLink(preferencesPath);writePreferences(preferencesPath,json,preferencesPath+".bak");
            preferences.Clear();foreach(var pair in next)preferences.Add(pair.Key,pair.Value);
        }Pulse();Emit();
    }
    internal void Configure(JsonObject projected){
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(string section in new[]{"left","center","right"})foreach(var value in projected["toolbar"]![section]!.AsArray().OfType<JsonObject>()){
            string id=FromSource(value["plugin"]?.GetValue<string>()??"");if(id.Length==0)id=value["nativeExtension"]?.GetValue<string>()??"";if(ToolbarProtocol.ValidId(id))ids.Add(id);
        }
        bool present=projected["monitors"]!.AsArray().OfType<JsonObject>().Any(m=>m["toolbar"]?.GetValue<bool>()==true);
        bool changed;lock(gate){changed=present!=toolbarPresent||!configured.SetEquals(ids);configured=ids;toolbarPresent=present;}if(changed)Pulse();
    }
    internal void Project(JsonObject projected){
        lock(gate){
            projected["toolbarExtensionDiscovery"]=JsonSerializer.SerializeToNode(discovery,ToolbarProtocol.Json);
            var menu=projected["toolbarModules"]!.AsArray();for(int i=menu.Count-1;i>=0;i--)if(menu[i]?["external"]?.GetValue<bool>()==true)menu.RemoveAt(i);
            foreach(string id in preferences.Keys.Concat(catalog.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)){
                var p=preferences.GetValueOrDefault(id);var d=catalog.GetValueOrDefault(id);string name=d?.Manifest.DisplayName??p?.DisplayName??id;
                menu.Add(new JsonObject{["id"]="extension:"+id,["extensionId"]=id,["label"]=name+" · 外部扩展",["icon"]="CgExtensionAdd",["external"]=true,["extensionEnabled"]=p?.Enabled==true,["status"]=slots.GetValueOrDefault(id)?.Status??statuses.GetValueOrDefault(id,"disabled")});
            }
            foreach(string section in new[]{"left","center","right"})foreach(var row in projected["toolbar"]![section]!.AsArray().OfType<JsonObject>()){
                string id=FromSource(row["plugin"]?.GetValue<string>()??"");if(id.Length==0)id=row["nativeExtension"]?.GetValue<string>()??"";if(!ToolbarProtocol.ValidId(id))continue;
                row["nativeExtension"]=id;row["type"]="Extension";row["label"]=catalog.GetValueOrDefault(id)?.Manifest.DisplayName??preferences.GetValueOrDefault(id)?.DisplayName??id;
                row.Remove("compatibilityMetadataOnly");row.Remove("extensionView");row.Remove("extensionSession");row.Remove("extensionRevision");row["extensionReady"]=false;row["extensionGranted"]=false;row["enabled"]=false;
                if(!slots.TryGetValue(id,out var slot)||slot.Snapshot is not {} snapshot)continue;
                row["extensionReady"]=true;row["extensionGranted"]=slot.Granted;row["enabled"]=slot.Granted;row["extensionSession"]=slot.Session;
                row["extensionRevision"]=snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);row["extensionView"]=JsonSerializer.SerializeToNode(snapshot.Content,ToolbarProtocol.Json);
            }
            if(projected["warnings"] is JsonArray warnings)for(int i=warnings.Count-1;i>=0;i--){string id=warnings[i]?["item"]?.GetValue<string>()??"";if(id.StartsWith("extension:",StringComparison.Ordinal)&&warnings[i]?["reason"]?.GetValue<string>()=="compatibility-execution-deferred")warnings.RemoveAt(i);}
        }
    }
    internal void Report(string id,string session,long revision,long window,bool mounted,bool visible){
        lock(gate){if(!slots.TryGetValue(id,out var slot)||slot.Session!=session||slot.Snapshot is not {} snapshot||revision<=0||revision>snapshot.Revision)return;
            if(!mounted)slot.Surfaces.Remove(window);else{if(slot.Surfaces.Count>=64&&!slot.Surfaces.ContainsKey(window))return;slot.Surfaces[window]=new(session,revision,true,visible);}
            slot.States.Writer.TryWrite(true);
        }
    }
    internal async Task Invoke(string id,string session,long revision,string action){
        ToolbarHostClient client;Slot selected;lock(gate){
            if(!slots.TryGetValue(id,out var slot)||!slot.Granted||slot.Session!=session||slot.Client is null||slot.Snapshot is not {} snapshot||snapshot.Revision!=revision||!ToolbarProtocol.Actions(snapshot.Content).Contains(action))throw new InvalidOperationException("扩展内容已变化，请重新点击。");
            client=slot.Client;selected=slot;if(Interlocked.CompareExchange(ref selected.ActionPending,1,0)!=0)throw new InvalidOperationException("该扩展正在处理上一次点击，请稍候。");
        }
        try{if(!await client.InvokeAsync(revision,action,stop.Token))throw new InvalidOperationException("扩展未接受本次操作，请查看提供方状态。");}finally{Volatile.Write(ref selected.ActionPending,0);}
    }
    private void Watch(object sender,FileSystemEventArgs args)=>Pulse();
    private void Pulse(){if(!stop.IsCancellationRequested)changes.Writer.TryWrite(true);}
    private void Emit(){try{changed();}catch(Exception e)when(e is not OutOfMemoryException){log("toolbar_extension_callback_failed",new{type=e.GetType().Name});}}
    // Public diagnostics never include paths, tokens, pipe names, raw JSON or exception messages.
    internal DiscoveryReport Discovery {get{lock(gate)return discovery;}}
    internal static string DefaultDiscoveryRoot()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MagicCommunity","Extensions","v1");
    internal static DiscoveryReport ProbeDiscovery(string registryRoot)=>InspectDiscovery(Path.GetFullPath(registryRoot)).Report;
    private static string DiscoveryError(Exception e)=>e switch {
        FileNotFoundException or DirectoryNotFoundException=>"missing",
        UnauthorizedAccessException=>"access-denied",
        JsonException=>"json-invalid",
        InvalidDataException when e.Message=="Extension discovery link refused."=>"link-refused",
        InvalidDataException=>"manifest-invalid",
        IOException when (e.HResult&65535) is 32 or 33=>"sharing-conflict",
        IOException=>"io-error",
        _=>"validation-error"
    };
    private static (Dictionary<string,Descriptor> Catalog,DiscoveryReport Report) InspectDiscovery(string registryRoot){
        var watch=Stopwatch.StartNew();var started=DateTimeOffset.UtcNow;
        var next=new Dictionary<string,Descriptor>(StringComparer.Ordinal);var issues=new List<DiscoveryIssue>();
        int enumerated=0,rejected=0;bool limited=false;string? error=null;
        void Reject(string id,string code){rejected++;if(issues.Count<16)issues.Add(new(id,code));}
        try{
            NoLink(registryRoot);
            var directories=Directory.EnumerateDirectories(registryRoot).Take(MaximumDiscovered+1).ToArray();
            enumerated=directories.Length;limited=enumerated>MaximumDiscovered;
            foreach(string directory in directories.Take(MaximumDiscovered)){
                string id=Path.GetFileName(directory);
                if(!ToolbarProtocol.ValidId(id)){Reject("<invalid-id>","directory-id-invalid");continue;}
                string path=Path.Combine(directory,"manifest.json");
                try{var manifest=ReadManifest(registryRoot,path);next.TryAdd(id,new(manifest,path));}
                catch(Exception e)when(e is not OutOfMemoryException){Reject(id,DiscoveryError(e));}
            }
        }catch(Exception e)when(e is not OutOfMemoryException){error=DiscoveryError(e);}
        var completed=DateTimeOffset.UtcNow;
        return(next,new(0,started,completed,watch.Elapsed.TotalMilliseconds,enumerated,next.Count,rejected,limited,
            error is not null?"unavailable":enumerated==0?"empty":next.Count==0?"rejected":"available",error,issues.ToArray(),error is null?completed:null));
    }
    private Dictionary<string,Descriptor> Scan(){
        var result=InspectDiscovery(root);DiscoveryReport report;
        lock(gate){report=result.Report with{Sequence=discovery.Sequence+1,LastSuccessfulScanUtc=result.Report.LastSuccessfulScanUtc??discovery.LastSuccessfulScanUtc};discovery=report;}
        string signature=JsonSerializer.Serialize(new{report.Enumerated,report.Accepted,report.Rejected,report.LimitReached,report.Status,report.Error,report.Issues});
        if(signature!=lastDiscoveryLog){
            long now=Environment.TickCount64;
            if(lastDiscoveryLog.Length==0||now-lastDiscoveryLogAt>=5000){
                log("toolbar_extension_discovery",new{summary=report,suppressed=suppressedDiscoveryLogs});
                lastDiscoveryLog=signature;lastDiscoveryLogAt=now;suppressedDiscoveryLogs=0;
            }else if(suppressedDiscoveryLogs<int.MaxValue)suppressedDiscoveryLogs++;
        }
        return result.Catalog;
    }
    private async Task Work(){
        try{await foreach(bool signal in changes.Reader.ReadAllAsync(stop.Token)){
            await Task.Delay(160,stop.Token);while(changes.Reader.TryRead(out _)){}
            Dictionary<string,Descriptor> found;try{found=Scan();}catch(Exception e)when(e is not OutOfMemoryException){log("toolbar_extension_discovery_unavailable",new{type=e.GetType().Name});found=new(StringComparer.Ordinal);}
            List<Slot> removed=[];List<(Slot Slot,Descriptor Descriptor)> added=[];
            lock(gate){
                catalog=found;
                var wanted=preferences.Values.Where(p=>p.Enabled&&configured.Contains(p.Id)&&toolbarPresent&&found.ContainsKey(p.Id)).ToDictionary(p=>p.Id,StringComparer.Ordinal);
                foreach(var pair in slots.ToArray())if(!wanted.ContainsKey(pair.Key)||(pair.Value.Work.IsCompleted&&pair.Value.Status=="offline")||found[pair.Key].Manifest.SessionId!=pair.Value.Session||!string.Equals(wanted[pair.Key].Image,pair.Value.Image,StringComparison.OrdinalIgnoreCase)){removed.Add(pair.Value);slots.Remove(pair.Key);}
                foreach(var pair in wanted)if(!slots.ContainsKey(pair.Key)){
                    var d=found[pair.Key];try{if(!string.Equals(ProviderImage(d.Manifest),pair.Value.Image,StringComparison.OrdinalIgnoreCase)){statuses[pair.Key]="unapproved-image";continue;}
                        var slot=new Slot(pair.Key,d.Path,d.Manifest.SessionId,pair.Value.Image);slots.Add(pair.Key,slot);added.Add((slot,d));}catch(Exception e)when(e is not OutOfMemoryException){statuses[pair.Key]="offline";}
                }
            }
            foreach(var slot in removed){slot.Stop.Cancel();lock(gate){retiring.RemoveAll(t=>t.IsCompleted);retiring.Add(Release(slot));}}
            foreach(var pair in added)pair.Slot.Work=Run(pair.Slot,pair.Descriptor);
            Emit();
        }}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    }
    private async Task Release(Slot slot){try{await slot.Work;}catch(Exception e)when(e is not OutOfMemoryException){log("toolbar_extension_release_failed",new{id=slot.Id,type=e.GetType().Name});}finally{slot.Stop.Dispose();}}
    private async Task Run(Slot slot,Descriptor expected){
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,slot.Stop.Token);var cancellation=lifetime.Token;int failures=0;
        while(failures<5&&!cancellation.IsCancellationRequested){
            ToolbarHostClient? client=null;bool entered=false,dormant=false;
            try{
                if(slot.Dormant){
                    while(!cancellation.IsCancellationRequested){
                        bool wanted;lock(gate)wanted=slot.Surfaces.Values.Any(s=>s.Mounted);if(wanted)break;
                        if(!await slot.States.Reader.WaitToReadAsync(cancellation))break;while(slot.States.Reader.TryRead(out _)){}
                    }
                    cancellation.ThrowIfCancellationRequested();slot.Dormant=false;
                }
                if(failures>0)await Task.Delay(new[]{1,2,5,15}[failures-1]*1000,cancellation);
                await connecting.WaitAsync(cancellation);entered=true;
                var manifest=ReadManifest(root,slot.Path);if(manifest.SessionId!=expected.Manifest.SessionId||!string.Equals(ProviderImage(manifest),slot.Image,StringComparison.OrdinalIgnoreCase))break;
                client=await ToolbarHostClient.ConnectAsync(slot.Path,cancellation);if(client.Manifest.SessionId!=slot.Session)throw new InvalidDataException("Provider session changed during connect.");
                connecting.Release();entered=false;
                var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected+=()=>ended.TrySetResult();
                void Snapshot(ToolbarSnapshot view){
                    long now=Environment.TickCount64;lock(gate){if(now-slot.RateStart>=1000){slot.RateStart=now;slot.RateCount=0;}if(++slot.RateCount>12)throw new InvalidDataException("Extension snapshot rate exceeds bound.");slot.Snapshot=view;slot.Status=slot.Granted?"active":"waiting-for-slot";slot.States.Writer.TryWrite(true);}Emit();
                }
                client.SnapshotReceived+=Snapshot;
                lock(gate)slot.Client=client;if(client.Latest is {} initial)Snapshot(initial);Emit();
                (bool Mounted,bool Visible)? last=null;bool wasMounted=false;using var mountDeadline=new CancellationTokenSource();var initialMount=Task.Delay(15000,mountDeadline.Token);
                while(!cancellation.IsCancellationRequested&&!ended.Task.IsCompleted){
                    using var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation);var pulse=slot.States.Reader.WaitToReadAsync(wait.Token).AsTask();
                    if(!wasMounted)await Task.WhenAny(ended.Task,pulse,initialMount);else await Task.WhenAny(ended.Task,pulse);
                    if(ended.Task.IsCompleted){wait.Cancel();break;}
                    if(!wasMounted&&initialMount.IsCompleted){wait.Cancel();dormant=true;break;}
                    if(!await pulse)break;while(slot.States.Reader.TryRead(out _)){}
                    (bool Mounted,bool Visible) state;lock(gate)state=(slot.Surfaces.Values.Any(s=>s.Mounted),slot.Surfaces.Values.Any(s=>s.Mounted&&s.Visible));
                    if(last==state&&(slot.Granted||!state.Mounted))continue;
                    bool granted=await client.SetStateAsync(state.Mounted,state.Visible,cancellation);
                    lock(gate){slot.Granted=state.Mounted&&granted&&!cancellation.IsCancellationRequested;slot.Status=slot.Granted?"active":state.Mounted?"lease-denied":"waiting-for-slot";}
                    last=state;Emit();wasMounted|=state.Mounted;
                    if(wasMounted&&!state.Mounted){dormant=true;break;}
                }
                mountDeadline.Cancel();
            }catch(Exception e)when(e is not OutOfMemoryException){if(!cancellation.IsCancellationRequested)log("toolbar_extension_connection_failed",new{id=slot.Id,type=e.GetType().Name});}
            finally{
                if(entered)connecting.Release();
                lock(gate){slot.Granted=false;slot.Client=null;slot.Dormant=dormant;slot.Status=dormant?"waiting-for-slot":"offline";if(!dormant){slot.Snapshot=null;slot.Surfaces.Clear();}}
                Emit();
                if(client is not null){try{using var deadline=new CancellationTokenSource(1000);await client.SetStateAsync(false,false,deadline.Token);}catch{}try{await client.DisposeAsync();}catch{}}
            }
            if(dormant)failures=0;else failures++;
        }
    }
    public async ValueTask DisposeAsync(){
        if(Interlocked.Exchange(ref closed,1)!=0)return;watcher?.Dispose();stop.Cancel();changes.Writer.TryComplete();Slot[] active;lock(gate){active=slots.Values.ToArray();slots.Clear();}foreach(var slot in active)slot.Stop.Cancel();
        try{await worker;Task[] old;lock(gate)old=retiring.ToArray();await Task.WhenAll(active.Select(Release).Concat(old));}finally{connecting.Dispose();stop.Dispose();}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,StringBuilder path,uint size,uint flags);
}
