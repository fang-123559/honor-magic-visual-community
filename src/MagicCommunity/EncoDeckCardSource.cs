using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// Use the live provider while it owns a verified subscription. Older EncoDeck
// builds can still supply their last readings or an explicitly visible panel.
internal sealed class EncoDeckCardSource : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim delivery = new(1,1);
    private readonly Func<JsonElement,Task> publish;
    private readonly CompositionEncoDeckCardData live;
    private readonly OriginalEncoDeckCardData original;
    private EventWaitHandle? signal;
    private RegisteredWaitHandle? registration;
    private bool demanded, disposed;

    internal EncoDeckCardSource(EncoDeckIntegration app, Func<JsonElement,Task> publish)
    {
        this.publish=publish;
        live=new(OnLive,acceptPeer:pid=>app.Locate().ProcessId==pid);
        original=new(app,state=>Deliver(state,false));
    }
    internal bool IsProgramRunning => live.IsConnected || original.IsProgramRunning;
    internal void Demand(bool value)
    {
        lock(gate)
        {
            if(disposed||demanded==value)return;
            demanded=value;
            if(value)
            {
                using var process=Process.GetCurrentProcess();
                try {
                    signal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\EncoDeck.DesktopCard.v1.s"+process.SessionId+".ready");
                    registration=ThreadPool.RegisterWaitForSingleObject(signal,(_,_)=>live.ProviderStarted(),null,Timeout.Infinite,false);
                } catch(Exception error) when(error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
            }
            else {registration?.Unregister(null);registration=null;signal?.Dispose();signal=null;original.Demand(false);}
            live.Demand(value);
        }
    }
    private Task OnLive(JsonElement state)
    {
        bool available=state.TryGetProperty("available",out var flag)&&flag.ValueKind==JsonValueKind.True;
        lock(gate)
        {
            if(!demanded||disposed)return Task.CompletedTask;
            if(!available && state.TryGetProperty("connecting",out var pending)&&pending.ValueKind==JsonValueKind.True)return Task.CompletedTask;
            original.Demand(!available);
        }
        return available?Deliver(state,true):Task.CompletedTask;
    }
    private async Task Deliver(JsonElement state,bool fromPipe)
    {
        await delivery.WaitAsync().ConfigureAwait(false);
        try {
            lock(gate)if(!demanded||disposed||fromPipe!=live.IsConnected)return;
            await publish(state).ConfigureAwait(false);
        } finally {delivery.Release();}
    }
    internal void SelectionChanged()
    {
        lock(gate){if(!demanded||disposed)return;Demand(false);Demand(true);}
    }
    internal void Retry()
    {
        if(live.IsConnected)return;
        live.ProviderStarted();original.Retry();
    }
    internal Task<bool> SendActionAsync(string action,int? choice,CancellationToken cancel) =>
        live.IsConnected?live.SendActionAsync(action,choice,cancel):original.SendActionAsync(action,choice,cancel);
    public void Dispose()
    {
        lock(gate){if(disposed)return;Demand(false);disposed=true;}
        live.Dispose();original.Dispose();
    }
}
