using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public sealed partial class DesktopMenuHost
{
    public static async Task<object> TestStagedQueries(string root)
    {
        string fixture=Path.Combine(root,"artifacts/menu-staged-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        string file=Path.Combine(fixture,"owned.txt"),other=Path.Combine(fixture,"other.txt");File.WriteAllText(file,"Owned staged menu fixture");File.WriteAllText(other,"Selection mismatch fixture");
        var checks=new List<object>();var metrics=new List<object>();
        void Check(string label,bool pass){checks.Add(new{label,pass});if(!pass)throw new InvalidDataException(label);}
        JsonElement Body(string id,int[]? path=null)=>JsonSerializer.SerializeToElement(new{requestId=id,path=path??[],fullFileMenu=true});
        async Task<JsonElement> First(DesktopMenuHost host,string id)=>JsonSerializer.SerializeToElement(await host.Route("/desktop-context-open",JsonSerializer.SerializeToElement(new{requestId=id}),CancellationToken.None,target:file),Wire);
        async Task Reject(Func<Task<object>> operation,string label)
        {
            bool rejected=false;try{await operation();}catch(Exception e)when(e is InvalidDataException or OperationCanceledException or ObjectDisposedException){rejected=true;}
            Check(label,rejected);
        }
        using var host=new DesktopMenuHost(root);
        string id=Guid.NewGuid().ToString("N");var initial=await First(host,id);int primary=host.worker!.Id;
        var firstRows=initial.GetProperty("compact").EnumerateArray().ToArray();
        Check("stock file operations remain enabled on first page",new[]{"open","openas","copyaspath","cut","copy","rename","delete","properties"}.All(k=>firstRows.Any(r=>r.GetProperty("key").GetString()==k&&!r.GetProperty("disabled").GetBoolean())));
        Check("installed providers are deferred from first page",firstRows.All(r=>r.GetProperty("key").GetString()!="modern"));
        await Reject(()=>host.Route("/desktop-context-prefetch",Body(id),CancellationToken.None,target:other),"prefetch cannot change the authenticated selection");
        await Reject(()=>host.Route("/desktop-context-prefetch",Body(Guid.NewGuid().ToString("N")),CancellationToken.None,target:file),"unopened lease cannot preload");
        host.idlePrefetch=new DesktopMenuHost(root);var source=host.idlePrefetch;await source.gate.WaitAsync();
        var preload=host.Route("/desktop-context-prefetch",Body(id),CancellationToken.None,target:file);
        var duplicate=host.Route("/desktop-context-prefetch",Body(id),CancellationToken.None,target:file);
        Check("duplicate preload shares exactly one query",host.prefetch?.Host==source&&source.starts==0&&!preload.IsCompleted);
        var compression=firstRows.First(r=>r.GetProperty("key").GetString()=="compress");var clock=Stopwatch.StartNew();
        var children=JsonSerializer.SerializeToElement(await host.Route("/desktop-context-items",Body(id,[compression.GetProperty("index").GetInt32()]),CancellationToken.None),Wire);
        metrics.Add(new{firstSubmenuWhilePrefetchBlockedMs=clock.Elapsed.TotalMilliseconds});
        Check("stock submenu is usable while full query is blocked",children.GetProperty("items").GetArrayLength()>0&&!preload.IsCompleted&&host.worker?.Id==primary);
        source.gate.Release();var complete=JsonSerializer.SerializeToElement(await preload,Wire);await duplicate;
        int fullPid=source.worker!.Id;Check("background query has independent worker and command IDs",primary!=fullPid&&complete.GetProperty("more").EnumerateArray().All(r=>r.GetProperty("index").GetInt32()>=PrefetchIndex));
        Check("full query completion never replaces first command table",host.worker?.Id==primary&&host.active==id);
        await Reject(()=>host.Route("/desktop-context-open",Body(id),CancellationToken.None,target:other),"cached More cannot change its selection");
        var fullCompress=complete.GetProperty("compact").EnumerateArray().First(r=>r.GetProperty("key").GetString()=="compress");
        var fullChildren=JsonSerializer.SerializeToElement(await host.Route("/desktop-context-items",Body(id,[fullCompress.GetProperty("index").GetInt32()]),CancellationToken.None),Wire);
        Check("namespaced full submenu routes to its own real handler",fullChildren.GetProperty("items").GetArrayLength()==children.GetProperty("items").GetArrayLength());
        clock.Restart();var cached=JsonSerializer.SerializeToElement(await host.Route("/desktop-context-open",Body(id),CancellationToken.None,target:file),Wire);
        metrics.Add(new{readyMoreMs=clock.Elapsed.TotalMilliseconds});Check("More uses completed preload without another query",cached.GetProperty("prefetched").GetBoolean()&&source.starts==1);
        foreach(var p in new[]{host.worker!,source.worker!}){p.Refresh();var cpu=p.TotalProcessorTime;await Task.Delay(1000);p.Refresh();metrics.Add(new{pid=p.Id,workingSetBytes=p.WorkingSet64,privateBytes=p.PrivateMemorySize64,idleCpuMs=(p.TotalProcessorTime-cpu).TotalMilliseconds,idleSampleMs=1000});}
        await host.Route("/menu-dismiss",Body(id),CancellationToken.None);
        Check("dismiss releases both selections under bounded warm retention",host.active is null&&host.prefetch is null&&source.active is null&&host.idlePrefetch==source);
        await Reject(()=>host.Route("/desktop-context-items",Body(id,[fullCompress.GetProperty("index").GetInt32()]),CancellationToken.None),"closed full command IDs are rejected");
        string next=Guid.NewGuid().ToString("N");await First(host,next);await host.Route("/desktop-context-prefetch",Body(next),CancellationToken.None,target:file);
        Check("next lease reuses both initialized processes",host.worker?.Id==primary&&host.prefetch?.Host.worker?.Id==fullPid&&host.prefetch.Host.starts==1);
        await host.Route("/menu-dismiss",Body(id),CancellationToken.None);Check("late dismissal cannot cancel new prefetch",host.prefetch?.Id==next);
        await host.Route("/menu-dismiss",Body(next),CancellationToken.None);
        await Task.Delay(WarmRetention+TimeSpan.FromMilliseconds(1200));
        Check("both idle processes expire without polling",!ProcessExists(primary)&&!ProcessExists(fullPid)&&source.worker is null&&host.worker is null);
        string pendingId=Guid.NewGuid().ToString("N");await First(host,pendingId);host.idlePrefetch??=new DesktopMenuHost(root);var blocked=host.idlePrefetch;await blocked.gate.WaitAsync();
        var abandoned=host.Route("/desktop-context-prefetch",Body(pendingId),CancellationToken.None,target:file);
        await host.Route("/menu-dismiss",Body(pendingId),CancellationToken.None);blocked.gate.Release();
        await Reject(()=>abandoned,"dismiss cancels incomplete preload");Check("abandoned worker is not retained",host.prefetch is null&&host.idlePrefetch is null&&blocked.worker is null);
        string suspendedId=Guid.NewGuid().ToString("N");await First(host,suspendedId);await host.Route("/desktop-context-prefetch",Body(suspendedId),CancellationToken.None,target:file);
        int lastPrimary=host.worker!.Id,lastFull=host.prefetch!.Host.worker!.Id;host.Suspend();
        Check("suspension reclaims both pending and retained processes",!ProcessExists(lastPrimary)&&!ProcessExists(lastFull)&&host.prefetch is null&&host.idlePrefetch is null);
        host.Resume();string retryId=Guid.NewGuid().ToString("N");await First(host,retryId);await host.Route("/desktop-context-prefetch",Body(retryId),CancellationToken.None,target:file);
        int preservedPrimary=host.worker!.Id;var failed=host.prefetch!.Host.worker!;failed.Kill(entireProcessTree:false);failed.WaitForExit(1000);
        var retry=JsonSerializer.SerializeToElement(await host.Route("/desktop-context-open",Body(retryId),CancellationToken.None,target:file),Wire);
        Check("explicit More retries after preload worker exits",retry.GetProperty("fullFileMenu").GetBoolean()&&retry.GetProperty("prefetched").GetBoolean()&&retry.GetProperty("more").EnumerateArray().Any(r=>r.GetProperty("key").GetString()=="properties"));
        Check("retry preserves first-level command worker",host.worker?.Id==preservedPrimary&&host.active==retryId);
        host.Dispose();Check("disposal releases all remaining processes",host.worker is null&&host.prefetch is null&&host.idlePrefetch is null);
        return new{allPass=true,checks,metrics,fixture,commandsInvoked=false,physicalInputToFrameMeasured=false,scope="Real Shell queries on owned fixture files; no clipboard, desktop takeover or actual command invocation."};
    }
}
