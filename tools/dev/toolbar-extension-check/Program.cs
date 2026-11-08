using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.ToolbarExtension;
using MagicCommunity;

var output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var directory=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var checks=new List<object>();
void Check(bool pass,string name){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
bool Refused(Action action){try{action();return false;}catch(Exception e)when(e is InvalidDataException or ArgumentException){return true;}}
async Task Refuses(Func<Task> action,string name){bool refused=false;try{await action();}catch(Exception e)when(e is InvalidDataException or IOException or OperationCanceledException or TimeoutException){refused=true;}Check(refused,name);}
async Task Wait(Func<bool> predicate,string name,int ms=4000){var watch=Stopwatch.StartNew();while(!predicate()){if(watch.ElapsedMilliseconds>ms)throw new TimeoutException(name);await Task.Delay(20);}Check(true,name);}
ToolbarContent Content(int value)=>new([new("build",[new("Build "),new(value.ToString(),true)],new("positive"),"open-build","open-context")]);
void Atomic(string path,string text,string? backup){Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+".fixture-new";File.WriteAllText(temporary,text);if(File.Exists(path)){if(backup is not null)File.Copy(path,backup,true);File.Move(temporary,path,true);}else File.Move(temporary,path);}
try{
    if(args.Length>1){
        string documentation=Path.GetFullPath(args[1]);
        foreach(string file in new[]{"manifest","toolbar-content","snapshot","envelope"}){
            using var schema=JsonDocument.Parse(File.ReadAllText(Path.Combine(documentation,"schemas",file+".schema.json")));
            Check(schema.RootElement.GetProperty("$schema").GetString()=="https://json-schema.org/draft/2020-12/schema","structured schema parsed: "+file);
        }
        foreach(string file in new[]{"build-monitor","meeting"}){
            var sample=JsonSerializer.Deserialize<ToolbarSnapshot>(File.ReadAllText(Path.Combine(documentation,"examples",file+".snapshot.json")),ToolbarProtocol.Json)!;
            ToolbarProtocol.Validate(sample.Content);Check(sample.Revision>0,"distributable example validated by actual wire contract: "+file);
        }
        using var api=JsonDocument.Parse(File.ReadAllText(Path.Combine(documentation,"api.json")));
        Check(api.RootElement.GetProperty("protocol").GetString()==ToolbarProtocol.Name&&api.RootElement.GetProperty("transport").GetProperty("frame").GetProperty("maxBodyBytes").GetInt32()==ToolbarProtocol.MaximumFrameBytes,"machine-readable protocol agrees with codec");
    }
    Check(ToolbarProtocol.ValidId("vendor.build-monitor"),"generic identifier accepted");
    Check(!ToolbarProtocol.ValidId("vendor.build\n"),"final-newline identifier rejected");
    Check(!ToolbarProtocol.ValidId("Vendor.App")&&!ToolbarProtocol.ValidId("../app"),"uppercase and path identifiers rejected");
    var ordinary=Content(27);ToolbarProtocol.Validate(ordinary);
    Check(ToolbarProtocol.Actions(ordinary).SetEquals(["open-build","open-context"]),"actions derived only from declared generic content");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{Gap=double.NaN})),"non-finite gap rejected");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{MaxWidth=2000})),"oversized width rejected");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{Segments=[ordinary.Segments[0],ordinary.Segments[0]]})),"duplicate segment identity rejected");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{Overlay=new("source","line\nbreak")})),"control characters rejected");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{Segments=[new("build",[new(new string('x',257))])]})),"oversized run rejected");
    Check(Refused(()=>ToolbarProtocol.Validate(ordinary with{Segments=[new("build",[new("x")],new("fake"))]})),"unknown indicator semantic rejected");
    foreach(int length in new[]{-1,0,65537}){using var stream=new MemoryStream();var prefix=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(prefix,length);stream.Write(prefix);stream.Position=0;await Refuses(async()=>{await ToolbarProtocol.ReadAsync(stream);},"frame length rejected before body "+length);}
    using(var stream=new MemoryStream()){await ToolbarProtocol.WriteAsync(stream,ToolbarProtocol.Message("ping",new{},"test"));stream.Position=0;Check((await ToolbarProtocol.ReadAsync(stream)).Type=="ping","real frame codec round trip");}
    string registry=Path.Combine(directory,"transport");int calls=0;var states=new ConcurrentQueue<HostState>();
    await using(var provider=new ToolbarProviderServer(registry,"vendor.build-monitor","Build monitor","1.0",state=>{states.Enqueue(state);return Task.FromResult(true);},_=>{Interlocked.Increment(ref calls);return Task.CompletedTask;},pid=>pid==Environment.ProcessId)){
        provider.Publish(ordinary);
        await using(var host=await ToolbarHostClient.ConnectAsync(provider.ManifestPath)){
            await Wait(()=>host.Latest is not null,"first snapshot delivered");
            Check(!await host.InvokeAsync(host.Latest!.Revision,"open-build"),"action before presentation grant refused");
            Check(await host.SetStateAsync(true,true),"mounted grant acknowledged");
            Check(await host.InvokeAsync(host.Latest.Revision,"open-build")&&calls==1,"declared action delivered exactly once");
            Check(!await host.InvokeAsync(host.Latest.Revision,"not-declared")&&calls==1,"unoffered action refused");
            long old=host.Latest.Revision;provider.Publish(Content(28));await Wait(()=>host.Latest!.Revision>old,"state change pushed without polling");
            Check(!await host.InvokeAsync(old,"open-build")&&calls==1,"stale revision refused");
            var rejected=await host.Request("arbitrary-command",new{command="not executed"});Check(!rejected.GetProperty("ok").GetBoolean()&&rejected.GetProperty("reason").GetString()=="unsupported-message","unrecognized command cannot execute a shell action");
            Check(await host.SetStateAsync(false,false)&&!await host.InvokeAsync(host.Latest!.Revision,"open-build"),"unmount revokes actions");
        }
        await Wait(()=>states.TryPeek(out _)&&states.Last() is {Mounted:false,Visible:false},"EOF releases presentation");
        await using(var reconnected=await ToolbarHostClient.ConnectAsync(provider.ManifestPath)){await Wait(()=>reconnected.Latest is not null,"new connection receives latest revision");}
        string badRoot=Path.Combine(directory,"wrong-token","vendor.build-monitor");Directory.CreateDirectory(badRoot);string bad=Path.Combine(badRoot,"manifest.json");
        File.WriteAllText(bad,JsonSerializer.Serialize(provider.Manifest with{Token=new string('0',64)},ToolbarProtocol.Json));
        await Refuses(async()=>{await using var invalid=await ToolbarHostClient.ConnectAsync(bad);},"wrong session token rejected");
        File.WriteAllText(bad,JsonSerializer.Serialize(provider.Manifest with{ProviderPid=Environment.ProcessId+100000},ToolbarProtocol.Json));
        await Refuses(async()=>{await using var invalid=await ToolbarHostClient.ConnectAsync(bad);},"actual pipe server PID mismatch rejected");
    }
    string brokerRegistry=Path.Combine(directory,"broker-registry"),prefs=Path.Combine(directory,"prefs");
    int connectionsA=0,connectionsB=0;var brokerStatesA=new ConcurrentQueue<HostState>();var brokerStatesB=new ConcurrentQueue<HostState>();var events=new ConcurrentQueue<string>();
    await using var a=new ToolbarProviderServer(brokerRegistry,"vendor.build-monitor","Build monitor","1.0",s=>{brokerStatesA.Enqueue(s);return Task.FromResult(true);},_=>Task.CompletedTask,pid=>pid==Environment.ProcessId);
    await using var b=new ToolbarProviderServer(brokerRegistry,"vendor.meeting","Meeting status","1.0",s=>{brokerStatesB.Enqueue(s);return Task.FromResult(true);},_=>Task.CompletedTask,pid=>pid==Environment.ProcessId);
    a.ConnectionChanged+=s=>{if(s=="connected")Interlocked.Increment(ref connectionsA);};b.ConnectionChanged+=s=>{if(s=="connected")Interlocked.Increment(ref connectionsB);};
    a.Publish(Content(1));b.Publish(new([new("meeting",[new("Meeting "),new("10:00",true)],new("busy"),"open-meeting")]));
    // Actual scanner diagnostics: no peer connections and no preferences are created by probes.
    var beforeProbe=Directory.GetFileSystemEntries(brokerRegistry,"*",SearchOption.AllDirectories).Order().ToArray();
    var probe=ToolbarExtensions.ProbeDiscovery(brokerRegistry);
    Check(probe.Accepted==2&&probe.Enumerated==2&&probe.Status=="available"&&probe.Error is null,"read-only scanner accepts two generic providers");
    Check(connectionsA==0&&connectionsB==0&&!Directory.Exists(prefs),"read-only scanner does not connect or create preferences");
    Check(beforeProbe.SequenceEqual(Directory.GetFileSystemEntries(brokerRegistry,"*",SearchOption.AllDirectories).Order()),"read-only scanner leaves discovery files unchanged");
    string empty=Path.Combine(directory,"empty");Directory.CreateDirectory(empty);
    Check(ToolbarExtensions.ProbeDiscovery(empty) is{Status:"empty",Enumerated:0,Rejected:0},"empty registry differs from rejected manifests");
    string missing=Path.Combine(directory,"missing");
    Check(ToolbarExtensions.ProbeDiscovery(missing) is{Status:"unavailable",Error:"missing"}&&!Directory.Exists(missing),"missing registry is reported without creating it");
    string malformed=Path.Combine(directory,"malformed");Directory.CreateDirectory(malformed);
    void Manifest(string id,string text){string folder=Path.Combine(malformed,id);Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"manifest.json"),text);}
    Manifest("vendor.json","{");Manifest("vendor.invalid","{}");Directory.CreateDirectory(Path.Combine(malformed,"vendor.missing"));Directory.CreateDirectory(Path.Combine(malformed,"BAD"));
    var badProbe=ToolbarExtensions.ProbeDiscovery(malformed);
    Check(badProbe.Rejected==4&&badProbe.Accepted==0&&badProbe.Status=="rejected","manifest failures remain isolated per entry");
    Check(badProbe.Issues.Any(i=>i.Code=="json-invalid")&&badProbe.Issues.Any(i=>i.Code=="manifest-invalid")&&badProbe.Issues.Any(i=>i.Code=="missing")&&badProbe.Issues.Any(i=>i.Code=="directory-id-invalid"),"fixed rejection categories distinguish failure stages");
    string json=JsonSerializer.Serialize(a.Manifest with{Id="vendor.locked"},ToolbarProtocol.Json);Manifest("vendor.locked",json);
    using(var locked=new FileStream(Path.Combine(malformed,"vendor.locked","manifest.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        Check(ToolbarExtensions.ProbeDiscovery(malformed).Issues.Any(i=>i.Id=="vendor.locked"&&i.Code=="sharing-conflict"),"sharing conflict is diagnosed separately");
    for(int n=0;n<70;n++)Manifest("vendor.bad"+n,"{");
    var bounded=ToolbarExtensions.ProbeDiscovery(malformed);
    Check(bounded.Enumerated==65&&bounded.LimitReached&&bounded.Issues.Length==16,"directory scan and issue list stay bounded");
    string diagnostic=JsonSerializer.Serialize(bounded);
    Check(!diagnostic.Contains(a.Manifest.Token)&&!diagnostic.Contains(a.Manifest.PipeName)&&!diagnostic.Contains(malformed),"diagnostics contain no credentials or raw filesystem paths");
    string brokenPrefs=Path.Combine(directory,"broken-prefs");Directory.CreateDirectory(brokenPrefs);File.WriteAllText(Path.Combine(brokenPrefs,"extensions-v1.json"),"{");
    await using(var broken=new ToolbarExtensions(brokenPrefs,()=>{},(_,_)=>{},Atomic,brokerRegistry)){
        Check(broken.Discovery is{Status:"unavailable",Error:"preferences:json-invalid"},"startup preference failure is diagnosed before worker start");
        Check(File.ReadAllText(Path.Combine(brokenPrefs,"extensions-v1.json"))=="{","invalid preferences are retained without rewriting");
    }
    JsonObject View()=>new(){
        ["monitors"]=new JsonArray(new JsonObject{["toolbar"]=true}),["warnings"]=new JsonArray(),["toolbarModules"]=new JsonArray(),
        ["toolbar"]=new JsonObject{["left"]=new JsonArray(),["center"]=new JsonArray(
            new JsonObject{["id"]=ToolbarExtensions.RowId(a.Manifest.Id),["plugin"]=ToolbarExtensions.SourcePrefix+a.Manifest.Id,["type"]="Plugin"},
            new JsonObject{["id"]=ToolbarExtensions.RowId(b.Manifest.Id),["plugin"]=ToolbarExtensions.SourcePrefix+b.Manifest.Id,["type"]="Plugin"}),["right"]=new JsonArray()}};
    var broker=new ToolbarExtensions(prefs,()=>{},(name,detail)=>events.Enqueue(name+" "+JsonSerializer.Serialize(detail)),Atomic,brokerRegistry);
    try{
        var baseline=View();broker.Configure(baseline);
        JsonObject Read(){var v=View();broker.Project(v);return v;}
        JsonObject Row(string id)=>Read()["toolbar"]!["center"]!.AsArray().OfType<JsonObject>().Single(v=>v["id"]!.GetValue<string>()==ToolbarExtensions.RowId(id));
        await Wait(()=>Read()["toolbarModules"]!.AsArray().Count==2,"one discovery scan finds two distinct generic providers");
        Check(connectionsA==0&&connectionsB==0&&!broker.Enabled(a.Manifest.Id),"unknown providers default disabled and unconnected");
        Check(broker.Discovery is{Sequence:>0,Accepted:2,LastSuccessfulScanUtc:not null},"live broker retains last scan summary");
        Check(Read()["toolbarExtensionDiscovery"]?["accepted"]?.GetValue<int>()==2,"existing projection exposes bounded discovery summary");
        int discoveryLogs=events.Count(e=>e.StartsWith("toolbar_extension_discovery "));
        File.SetLastWriteTimeUtc(a.ManifestPath,DateTime.UtcNow);
        await Wait(()=>broker.Discovery.Sequence>1,"file event updates discovery without polling");
        Check(events.Count(e=>e.StartsWith("toolbar_extension_discovery "))==discoveryLogs,"unchanged repeated scan does not spam diagnostics");

        broker.SetEnabled(a.Manifest.Id,true);await Wait(()=>Row(a.Manifest.Id)["extensionReady"]!.GetValue<bool>(),"enabled provider delivers a typed snapshot");
        var first=Row(a.Manifest.Id);Check(!first["extensionGranted"]!.GetValue<bool>(),"handshake alone does not display content");
        broker.Report(a.Manifest.Id,a.Manifest.SessionId,long.Parse(first["extensionRevision"]!.GetValue<string>()),1001,true,true);
        await Wait(()=>Row(a.Manifest.Id)["extensionGranted"]!.GetValue<bool>(),"actual prepared slot model grants presentation");
        var ready=Row(a.Manifest.Id);await broker.Invoke(a.Manifest.Id,a.Manifest.SessionId,long.Parse(ready["extensionRevision"]!.GetValue<string>()),"open-build");Check(true,"broker invokes an offered current action");
        broker.Report(a.Manifest.Id,a.Manifest.SessionId,long.Parse(ready["extensionRevision"]!.GetValue<string>()),1002,true,false);
        broker.Report(a.Manifest.Id,a.Manifest.SessionId,long.Parse(ready["extensionRevision"]!.GetValue<string>()),1001,false,false);
        await Wait(()=>brokerStatesA.LastOrDefault() is {Mounted:true,Visible:false},"one screen exit retains another hidden mount");
        Check(Row(a.Manifest.Id)["extensionGranted"]!.GetValue<bool>(),"hidden mount retains grant without making content visible");
        broker.Report(a.Manifest.Id,a.Manifest.SessionId,long.Parse(ready["extensionRevision"]!.GetValue<string>()),1002,false,false);
        await Wait(()=>!Row(a.Manifest.Id)["extensionGranted"]!.GetValue<bool>()&&brokerStatesA.LastOrDefault() is {Mounted:false,Visible:false},"last surface unmount releases provider lease");
        int prior=connectionsA;await Task.Delay(300);Check(connectionsA==prior,"no immediate reconnect without a prepared slot");
        broker.Report(a.Manifest.Id,a.Manifest.SessionId,long.Parse(ready["extensionRevision"]!.GetValue<string>()),1003,true,true);
        await Wait(()=>connectionsA>prior&&Row(a.Manifest.Id)["extensionGranted"]!.GetValue<bool>(),"new prepared slot wakes dormant connection");
        broker.SetEnabled(b.Manifest.Id,true);await Wait(()=>Row(b.Manifest.Id)["extensionReady"]!.GetValue<bool>(),"second provider connects independently");
        var second=Row(b.Manifest.Id);broker.Report(b.Manifest.Id,b.Manifest.SessionId,long.Parse(second["extensionRevision"]!.GetValue<string>()),1004,true,true);
        await Wait(()=>Row(b.Manifest.Id)["extensionGranted"]!.GetValue<bool>(),"two providers can be mounted");
        broker.SetEnabled(a.Manifest.Id,false);await Wait(()=>!Row(a.Manifest.Id)["extensionReady"]!.GetValue<bool>(),"disable removes only its live content");
        Check(Row(b.Manifest.Id)["extensionGranted"]!.GetValue<bool>()&&Read()["toolbar"]!["center"]!.AsArray().Count==2,"disable preserves saved row identity and other provider");
        using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(prefs,"extensions-v1.json")));
        Check(saved.RootElement.GetProperty("entries").EnumerateArray().Any(p=>p.GetProperty("id").GetString()==a.Manifest.Id&&!p.GetProperty("enabled").GetBoolean()),"disabled state remains persisted");
        Check(events.All(e=>!e.Contains(a.Manifest.Token,StringComparison.Ordinal)&&!e.Contains(b.Manifest.Token,StringComparison.Ordinal)),"runtime tokens absent from logs");
        Check(Refused(()=>ToolbarExtensions.ReadManifest(brokerRegistry,Path.Combine(directory,"outside","manifest.json"))),"discovery root escape rejected");
        Check(ToolbarExtensions.FromSource(ToolbarExtensions.SourcePrefix+"vendor.meeting")=="vendor.meeting"&&ToolbarExtensions.FromSource("@seelen/plugin")=="","legacy plugin identifiers cannot become executable extensions");
    }finally{var watch=Stopwatch.StartNew();await broker.DisposeAsync();Check(watch.ElapsedMilliseconds<3000,"broker disposal bounded and awaited");}
    var report=new{allPass=true,checks,scope="Owned named pipes, modelled renderer surfaces, fixture preferences only. No live desktop/provider activation, window hiding, startup or certificate changes.",notRun=new[]{"actual cross-user/cross-session Windows logons","real native toolbar input and mixed DPI","production provider presentation and restart","reparse-point creation without Developer Mode"}};
    File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(new{report.allPass,count=checks.Count}));return 0;
}catch(Exception e){File.WriteAllText(Path.Combine(output,"failure.txt"),e.ToString());Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
