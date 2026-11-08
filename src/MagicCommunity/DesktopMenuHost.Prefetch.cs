using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagicCommunity;

public sealed partial class DesktopMenuHost
{
    // A second transient STA is created only after the renderer has displayed
    // the first page. Its slow extensions never occupy the first page's pipe.
    // Separate root indices retain both command tables until the menu closes.
    private const int PrefetchIndex=2_000_000;
    private sealed record PendingFull(string Id,long Epoch,DesktopMenuHost Host,Task<object> Query);
    private PendingFull? prefetch;
    private DesktopMenuHost? idlePrefetch;
    private string? menuTarget;
    private string[] menuTargets=[];
    private bool menuBackground;
    private bool menuCanPrefetch;
    private readonly ConcurrentDictionary<DesktopMenuHost,byte> prefetchCommands=new();

    private async Task<object> Prefetch(string id,string? target,string[]? selection,bool background,long expected,CancellationToken stop)
    {
        PendingFull pending;
        lock(stateGate)
        {
            VerifyEpoch(expected);
            if(cancelled.Contains(id)||active!=id||worker is null||querying||!menuCanPrefetch)throw new OperationCanceledException("Menu expired.");
            if(target!=menuTarget||background!=menuBackground||!(selection??[]).SequenceEqual(menuTargets,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Menu selection changed.");
            if(prefetch is not null&&prefetch.Id==id)pending=prefetch;
            else
            {
                CancelPrefetch();
                var source=idlePrefetch??new DesktopMenuHost(root,trace:trace);source.providerFailures=providerFailures;idlePrefetch=null;
                var body=JsonSerializer.SerializeToElement(new{requestId=id,fullFileMenu=true});
                var query=Task.Run(()=>source.Route("/desktop-context-open",body,CancellationToken.None,target:target,folderBackground:background,targets:selection));
                // Observe failures even when dismissal abandons the HTTP reply.
                _=query.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                pending=prefetch=new(id,expected,source,query);
            }
        }
        return await ReadPrefetch(pending,stop).ConfigureAwait(false);
    }
    private async Task<object?> PrefetchedFull(string id,string? target,string[]? selection,bool background,long expected,CancellationToken stop)
    {
        PendingFull? pending;
        lock(stateGate){VerifyEpoch(expected);pending=prefetch?.Id==id?prefetch:null;}
        if(pending is null)
        {
            bool eligible; lock(stateGate) eligible=active==id&&menuCanPrefetch;
            return eligible ? await Prefetch(id,target,selection,background,expected,stop).ConfigureAwait(false) : null;
        }
        lock(stateGate)if(target!=menuTarget||background!=menuBackground||!(selection??[]).SequenceEqual(menuTargets,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Menu selection changed.");
        // An optional preload failure is not a poisoned cache. An explicit More
        // click may retry through another independently bounded worker.
        try{return await ReadPrefetch(pending,stop).ConfigureAwait(false);}
        catch(Exception error)when(error is not OutOfMemoryException&&!stop.IsCancellationRequested)
        {
            lock(stateGate){
                VerifyEpoch(expected);if(prefetch!=pending||active!=id)throw new OperationCanceledException("Menu expired.");
                // A failed read is not a warm, reusable selection. Dismissing
                // and retaining it would mark this same request ID cancelled.
                prefetch=null;pending.Host.Dispose();
            }
            // Retry in another independent worker. Replacing the original STA
            // here invalidated commands in the still-visible first-level menu.
            return await Prefetch(id,target,selection,background,expected,stop).ConfigureAwait(false);
        }
    }
    private async Task<object> ReadPrefetch(PendingFull pending,CancellationToken stop)
    {
        var result=await pending.Query.WaitAsync(stop).ConfigureAwait(false);
        lock(stateGate){VerifyEpoch(pending.Epoch);if(prefetch!=pending||active!=pending.Id||cancelled.Contains(pending.Id)||pending.Host.worker is not {HasExited:false})throw new OperationCanceledException("Menu expired.");}
        var data=JsonSerializer.SerializeToNode(result,Wire)!.AsObject();
        foreach(string name in new[]{"compact","more"})foreach(var item in data[name]!.AsArray())item!["index"]=checked(item["index"]!.GetValue<int>()+PrefetchIndex);
        data["prefetched"]=true;
        return JsonSerializer.SerializeToElement(data);
    }
    private async Task<object> RoutePrefetched(string route,string id,JsonElement body,long expected,CancellationToken stop)
    {
        if(route is not ("/desktop-context-items" or "/desktop-context-invoke"))throw new InvalidDataException("Invalid prefetched operation.");
        var data=JsonNode.Parse(body.GetRawText())!.AsObject();var path=data["path"]!.AsArray();
        path[0]=checked(path[0]!.GetValue<int>()-PrefetchIndex);
        PendingFull pending;bool command=route=="/desktop-context-invoke";
        lock(stateGate)
        {
            VerifyEpoch(expected);pending=prefetch??throw new InvalidDataException("Menu expired.");
            if(pending.Id!=id||active!=id||cancelled.Contains(id)||!pending.Query.IsCompletedSuccessfully)throw new InvalidDataException("Menu expired.");
            if(command){prefetch=null;prefetchCommands[pending.Host]=0;StopWorker();CancelRequest(id);}
        }
        try{return await pending.Host.Route(route,JsonSerializer.SerializeToElement(data),stop).ConfigureAwait(false);}
        catch{if(!command)lock(stateGate)if(prefetch==pending)CancelPrefetch();throw;}
        finally{if(command){prefetchCommands.TryRemove(pending.Host,out _);pending.Host.Dispose();}}
    }
    private void CancelPrefetch()
    {
        var old=prefetch;prefetch=null;
        if(old is not null)
        {
            if(!disposed&&!suspended&&old.Query.IsCompletedSuccessfully)
            {
                // Release its selection immediately; only initialized code is
                // retained under the existing 10 s / eight-query worker bounds.
                old.Host.Route("/menu-dismiss",JsonSerializer.SerializeToElement(new{requestId=old.Id}),CancellationToken.None).GetAwaiter().GetResult();
                idlePrefetch?.Dispose();idlePrefetch=old.Host;
            }
            else old.Host.Dispose();
        }
        if(disposed||suspended){idlePrefetch?.Dispose();idlePrefetch=null;}
    }
}
