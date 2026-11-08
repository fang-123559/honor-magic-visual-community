using System.Text.Json;

namespace MagicCommunity;

// One small snapshot and one in-flight read, owned by this UI session. Expiry is
// checked on demand; there is no timer, worker thread or hidden-panel polling.
internal sealed class NativeNetworkDetails : IDisposable
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<JsonElement>> read;
    private readonly Func<string, long, JsonElement, Exception?, Task> publish;
    private readonly Func<long> clock;
    private readonly CancellationToken cancellation;
    private JsonElement cached;
    private long cachedAt, revision;
    private string? visible;
    private bool running, disposed;
    internal const long MaximumAgeMs = 30000;

    internal NativeNetworkDetails(Func<CancellationToken, Task<JsonElement>> read,
        Func<string, long, JsonElement, Exception?, Task> publish, CancellationToken cancellation,
        Func<long>? clock = null)
    { this.read = read; this.publish = publish; this.cancellation = cancellation; this.clock = clock ?? (() => Environment.TickCount64); }

    private bool Fresh => cached.ValueKind == JsonValueKind.Object && clock() - cachedAt < MaximumAgeMs;
    internal JsonElement Snapshot { get { lock (gate) { if (!Fresh) cached = default; return cached; } } }
    internal bool IsCurrent(string token, long version) { lock (gate) return !disposed && visible == token && revision == version; }
    internal void Show(string token)
    {
        bool start;
        lock (gate) { if (disposed) return; visible = token; start = !running; if (start) running = true; }
        // Reapply even a fresh snapshot: a read can finish between the first
        // panel model being built and its renderer-ready acknowledgement.
        if (start) _ = Run();
    }
    internal void Hide(string? token = null) { lock (gate) { if (token is null || visible == token) visible = null; } }
    internal void Invalidate()
    {
        bool start;
        lock (gate) { if (disposed) return; ++revision; cached = default; start = visible is not null && !running; if (start) running = true; }
        if (start) _ = Run();
    }
    private async Task Run()
    {
        // Even an injected synchronous provider cannot hold the caller's popup
        // switch gate. A pending IPC is allowed to drain when the panel closes.
        await Task.Yield();
        while (true)
        {
            long version; JsonElement value;
            lock (gate)
            {
                if (disposed || cancellation.IsCancellationRequested || visible is null) { running = false; return; }
                version = revision; value = Fresh ? cached : default;
            }
            bool loaded = value.ValueKind != JsonValueKind.Object;
            Exception? failure = null;
            try { if (loaded) value = await read(cancellation); }
            catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
            string? token;
            lock (gate)
            {
                if (disposed || cancellation.IsCancellationRequested) { running = false; return; }
                // A device event or action arriving during a slow read makes
                // its result obsolete. Coalesce all such events into one retry.
                if (version != revision) continue;
                if (failure is null && loaded) { cached = value; cachedAt = clock(); }
                token = visible;
            }
            // The consumer also checks its current popup token before sending.
            // Provider failures are represented in the panel, never fatal to UI.
            if (token is not null)
                try { await publish(token, version, value, failure); }
                catch (Exception error) when (error is not OutOfMemoryException) { /* UI consumer owns error reporting. */ }
            lock (gate)
            {
                if (disposed || cancellation.IsCancellationRequested || visible is null ||
                    (version == revision && visible == token)) { running = false; return; }
            }
        }
    }
    public void Dispose() { lock (gate) { disposed = true; visible = null; cached = default; ++revision; } }
}
