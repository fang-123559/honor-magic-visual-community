using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MagicCommunity;

// Original EncoDeck adapter. Reads only the documented per-user JSON shape;
// observes file changes, never modifies/loads EncoDeck or polls Bluetooth.
internal sealed class OriginalEncoDeckCardData : IDisposable
{
    private readonly EncoDeckIntegration app;
    private readonly Func<JsonElement,Task> emit;
    private readonly Func<EncoDeckIntegration.Location,string,string,string,CancellationToken,Task<JsonElement>> probe;
    private readonly string directory;
    private readonly Func<EncoDeckIntegration.Location,bool> hasSurface;
    private readonly object gate=new();
    private readonly SemaphoreSlim actions=new(1,1);
    private CancellationTokenSource? lifetime;
    private Channel<byte>? updates;
    private FileSystemWatcher? watcher;
    private EncoDeckWindowEvents? windows;
    private JsonElement current;
    private EncoDeckIntegration.Location? currentSource;private string? currentSelection;
    private bool demanded,disposed;
    private long generation;
    internal OriginalEncoDeckCardData(EncoDeckIntegration app,Func<JsonElement,Task> emit,string? dataDirectory=null,
        Func<EncoDeckIntegration.Location,string,string,string,CancellationToken,Task<JsonElement>>? probe=null,Func<EncoDeckIntegration.Location,bool>? hasSurface=null)
    {this.app=app;this.emit=emit;directory=dataDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EncoDeck");this.probe=probe??Probe;this.hasSurface=hasSurface??OriginalEncoDeckAccessibility.HasVisibleSurface;}
    internal bool IsProgramRunning {get{lock(gate)return demanded&&current.ValueKind==JsonValueKind.Object&&current.TryGetProperty("programRunning",out var v)&&v.GetBoolean();}}
    internal object Diagnostics {get{lock(gate)return new{demanded,watching=watcher is not null,generation,active=lifetime is not null};}}
    public void Demand(bool value)
    {
        lock(gate)
        {
            if(disposed||demanded==value)return;
            demanded=value;++generation;
            lifetime?.Cancel();lifetime=null;watcher?.Dispose();watcher=null;windows?.Dispose();windows=null;updates?.Writer.TryComplete();updates=null;
            currentSource=null;currentSelection=null;
            if(!value){current=default;return;}
            var stop=new CancellationTokenSource();lifetime=stop;long epoch=generation;
            var queue=Channel.CreateBounded<byte>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropOldest});updates=queue;
            if(Directory.Exists(directory)){
                watcher=new(directory,"*.json"){NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName|NotifyFilters.Size,IncludeSubdirectories=false,InternalBufferSize=4096};
                watcher.Changed+=(_,e)=>{if(KnownFile(e.Name))queue.Writer.TryWrite(0);};watcher.Created+=(_,e)=>{if(KnownFile(e.Name))queue.Writer.TryWrite(0);};
                watcher.Deleted+=(_,e)=>{if(KnownFile(e.Name))queue.Writer.TryWrite(0);};watcher.Renamed+=(_,e)=>{if(KnownFile(e.Name)||KnownFile(e.OldName))queue.Writer.TryWrite(0);};
                watcher.Error+=(_,_)=>queue.Writer.TryWrite(0);watcher.EnableRaisingEvents=true;
            }
            windows=new(()=>queue.Writer.TryWrite(0));
            queue.Writer.TryWrite(0);_ = Task.Run(()=>Pump(queue,stop,epoch));
        }
    }
    private static bool KnownFile(string? name)=>name is "settings.json" or "device-cache.json";
    internal void Retry(){lock(gate){if(!demanded||disposed)return;if(watcher is null&&Directory.Exists(directory)){Demand(false);Demand(true);}else updates?.Writer.TryWrite(0);}}
    private async Task Pump(Channel<byte> queue,CancellationTokenSource stop,long epoch)
    {
        try{
            string? previous=null;long nextRead=0;
            while(await queue.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false)){
                await Task.Delay((int)Math.Clamp(nextRead-Environment.TickCount64,180,750),stop.Token).ConfigureAwait(false);while(queue.Reader.TryRead(out _)){}
                nextRead=Environment.TickCount64+750;
                var location=app.Locate();string selection=SelectionStamp();var packet=Read(directory,location);JsonElement panel=default;
                lock(gate){if(generation!=epoch)return;windows?.Track(location.ProcessId);}
                // Initial read and coalesced file/window events only. Read-only
                // snapshots never open EncoDeck; a missing/hidden provider clears
                // stale ready controls instead of keeping the last successful UIA.
                await actions.WaitAsync(stop.Token).ConfigureAwait(false);
                try{
                    if(location.Running&&hasSurface(location))try{panel=await probe(location,"snapshot","","",stop.Token).ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException&&e is not OperationCanceledException){}
                    if(selection!=SelectionStamp()){panel=default;packet=Read(directory,location);}
                    packet=Combine(packet,panel);
                    string json=packet.GetRawText();
                    lock(gate){if(disposed||!demanded||generation!=epoch)return;current=packet;currentSource=location;currentSelection=selection;}
                    if(json==previous)continue;
                    await emit(packet).ConfigureAwait(false);previous=json;
                }finally{actions.Release();}
            }
        }catch(Exception e)when(e is not OutOfMemoryException){}
        finally{lock(gate){if(lifetime==stop){lifetime=null;watcher?.Dispose();watcher=null;windows?.Dispose();windows=null;updates=null;demanded=false;}}stop.Dispose();}
    }
    internal static JsonElement Read(string directory,EncoDeckIntegration.Location location)
    {
        if(!location.Running)return JsonSerializer.SerializeToElement(new{available=false,programRunning=false,programInstalled=location.Installed,providerStatus=location.Status});
        try{
            JsonElement cache=Load(Path.Combine(directory,"device-cache.json"),131072);
            if(cache.ValueKind!=JsonValueKind.Object||cache.EnumerateObject().Take(65).Count()>64)throw new InvalidDataException();
            string? key=null;string settingsPath=Path.Combine(directory,"settings.json");
            if(File.Exists(settingsPath)){
                var settings=Load(settingsPath,32768);
                if(settings.TryGetProperty("preferredDeviceId",out var device)&&device.ValueKind==JsonValueKind.String){string id=device.GetString()!;if(id.Length>2048)throw new InvalidDataException();if(id.Length>0)key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("encodeck-cache|"+id)).AsSpan(0,12));}
            }
            JsonElement entry;
            if(key is not null){if(!cache.TryGetProperty(key,out entry))throw new InvalidDataException();}
            else{var values=cache.EnumerateObject().Take(2).ToArray();if(values.Length!=1)throw new InvalidDataException();entry=values[0].Value;}
            object Battery(string name){int? level=null;bool charging=false;if(entry.TryGetProperty(name,out var item)&&item.ValueKind!=JsonValueKind.Null){
                if(item.ValueKind!=JsonValueKind.Object)throw new InvalidDataException();
                if(item.TryGetProperty("level",out var v)&&v.ValueKind!=JsonValueKind.Null){if(!v.TryGetInt32(out int n)||n< -1||n>100)throw new InvalidDataException();level=n<0?null:n;}
                if(item.TryGetProperty("charging",out var c)&&c.ValueKind!=JsonValueKind.Null){if(c.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException();charging=c.GetBoolean();}
                if(item.TryGetProperty("timestamp",out var t)&&(!t.TryGetDateTimeOffset(out var at)||at>DateTimeOffset.UtcNow.AddMinutes(5)))throw new InvalidDataException();
            }return new{level,charging,stale=true};}
            return JsonSerializer.SerializeToElement(new{available=true,programRunning=true,programInstalled=true,providerStatus="original-compatible",deviceName="EncoDeck 耳机",deviceConnected=false,controlReady=false,canSetNoise=false,noiseMode="",noiseStale=true,left=Battery("left"),right=Battery("right"),@case=Battery("case"),modes=Array.Empty<object>(),levels=Array.Empty<object>()});
        }catch(Exception e)when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException){
            return JsonSerializer.SerializeToElement(new{available=false,programRunning=true,programInstalled=true,providerStatus="original-no-cache"});
        }
    }
    private static JsonElement Load(string path,int limit){
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>limit)throw new InvalidDataException();var bytes=new byte[limit+1];int count=0,read;
        while(count<bytes.Length&&(read=file.Read(bytes,count,bytes.Length-count))>0)count+=read;
        if(count>limit)throw new InvalidDataException();using var doc=JsonDocument.Parse(bytes.AsMemory(0,count),new JsonDocumentOptions{MaxDepth=12});
        void Unique(JsonElement value){if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException();Unique(p.Value);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var v in value.EnumerateArray())Unique(v);}
        Unique(doc.RootElement);return doc.RootElement.Clone();
    }
    internal static JsonElement Combine(JsonElement data,JsonElement panel)
    {
        if(panel.ValueKind!=JsonValueKind.Object||!panel.TryGetProperty("available",out var active)||!active.GetBoolean()||!data.GetProperty("programRunning").GetBoolean())return data;
        var node=JsonNode.Parse(data.GetRawText())!.AsObject();
        node["available"]=true;node["deviceName"]=panel.GetProperty("deviceName").GetString();
        var modes=panel.GetProperty("modes");var levels=panel.GetProperty("levels");
        bool connected=panel.TryGetProperty("deviceConnected",out var linked)&&linked.GetBoolean();
        foreach(string key in new[]{"left","right","case"})if(panel.TryGetProperty(key,out var battery)&&battery.ValueKind==JsonValueKind.Object){
            var reading=JsonNode.Parse(battery.GetRawText())!.AsObject();if(!connected){reading["stale"]=true;reading["charging"]=false;}node[key]=reading;
        }
        bool ready=connected&&modes.EnumerateArray().Any(m=>m.GetProperty("enabled").GetBoolean());
        node["modes"]=JsonNode.Parse(modes.GetRawText());node["levels"]=JsonNode.Parse(levels.GetRawText());node["noiseMode"]=panel.GetProperty("noiseMode").GetString();
        node["noiseStale"]=!connected;node["canSetNoise"]=ready;node["controlReady"]=ready;node["deviceConnected"]=connected;node["providerStatus"]="original-compatible";
        // Normalize only a known projection before it enters the renderer.
        var clean=JsonNode.Parse(CompositionEncoDeckCardData.Normalize(JsonSerializer.SerializeToElement(node)).GetRawText())!.AsObject();
        clean["programRunning"]=true;clean["programInstalled"]=true;clean["providerStatus"]="original-compatible";
        return JsonSerializer.SerializeToElement(clean);
    }
    private async Task<bool> Publish(EncoDeckIntegration.Location location,JsonElement panel,long epoch){
        var packet=Combine(Read(directory,location),panel);lock(gate){if(disposed||!demanded||generation!=epoch)return false;current=packet;currentSource=location;currentSelection=SelectionStamp();}
        await emit(packet).ConfigureAwait(false);return true;
    }
    private string SelectionStamp(){try{string path=Path.Combine(directory,"settings.json");if(!File.Exists(path))return "none";var data=Load(path,32768);return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException){return "unavailable";}}
    internal async Task<bool> SendActionAsync(string action,int? choice,CancellationToken cancellation)
    {
        if(!await actions.WaitAsync(0,cancellation).ConfigureAwait(false))return false;
        CancellationToken token;long epoch;JsonElement prior;EncoDeckIntegration.Location? priorSource;string? selection;
        try{
            lock(gate){if(disposed||!demanded||lifetime is null)return false;token=lifetime.Token;epoch=generation;prior=current;priorSource=currentSource;selection=currentSelection;}
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,cancellation);linked.CancelAfter(TimeSpan.FromSeconds(10));
            var location=app.Locate();if(!location.Running){await Publish(location,default,epoch).ConfigureAwait(false);return false;}
            string kind="snapshot",label="",expected="";
            if(action!="refresh"){
                if(priorSource!=location||selection!=SelectionStamp()||!CompositionEncoDeckCardData.Allowed(prior,action,choice))return false;
                kind=action=="setNoiseMode"?"mode":"level";expected=prior.GetProperty("deviceName").GetString()??"";
                var selected=kind=="mode"?prior.GetProperty("modes")[choice!.Value]:prior.GetProperty("levels").EnumerateArray().Single(x=>x.GetProperty("index").GetInt32()==choice);
                label=selected.GetProperty("label").GetString()??"";
            }
            var state=hasSurface(location)?await probe(location,kind,expected,label,linked.Token).ConfigureAwait(false):JsonSerializer.SerializeToElement(new{available=false});
            bool Offered(JsonElement value)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("available",out var a)&&a.GetBoolean();
            if(!Offered(state)){
                await Publish(location,default,epoch).ConfigureAwait(false);
                // Only an explicit card action may open the original panel. No
                // hidden launch/activation or preference changes on demand.
                if(!await app.ShowPanel(linked.Token).ConfigureAwait(false))return false;
                await Task.Delay(180,linked.Token).ConfigureAwait(false);
                state=await probe(location,kind,expected,label,linked.Token).ConfigureAwait(false);
            }
            if(!Offered(state))return false;
            if(kind!="snapshot"){
                await Task.Delay(200,linked.Token).ConfigureAwait(false);
                state=await probe(location,"snapshot","","",linked.Token).ConfigureAwait(false);
                if(!Offered(state)||state.GetProperty("deviceName").GetString()!=expected){await Publish(location,state,epoch).ConfigureAwait(false);return false;}
                var values=state.GetProperty(kind=="mode"?"modes":"levels");
                if(!values.EnumerateArray().Any(x=>x.GetProperty("label").GetString()==label&&x.GetProperty("selected").GetBoolean())){await Publish(location,state,epoch).ConfigureAwait(false);return false;}
            }
            return await Publish(location,state,epoch).ConfigureAwait(false);
        }catch(Exception e)when(e is not OutOfMemoryException){return false;}
        finally{actions.Release();}
    }
    private static async Task<JsonElement> Probe(EncoDeckIntegration.Location location,string action,string device,string label,CancellationToken cancellation)
    {
        using var verified=EncoDeckIntegration.Recheck(location);
        string executable=Path.Combine(AppContext.BaseDirectory,"MagicCommunity.exe");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string arg in new[]{"--encodeck-accessibility",location.ProcessId.ToString(),location.Created.ToString(),location.Path!,action,device,label})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try{
            string? line=await new CompositionEncoDeckCardData.BoundedLines(process.StandardOutput).Read(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if(line is null)throw new IOException();using var json=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=12});return json.RootElement.Clone();
        }finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync().ConfigureAwait(false);}}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;demanded=false;++generation;lifetime?.Cancel();lifetime=null;watcher?.Dispose();watcher=null;windows?.Dispose();windows=null;updates?.Writer.TryComplete();updates=null;}}
}
