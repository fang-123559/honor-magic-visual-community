using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Magic.ToolbarExtension;

// Reference host transport. Discovery, user consent, native rendering and
// placement stay in the host; this class does not modify any configuration.
public sealed class ToolbarHostClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly object requests = new();
    private Task reading = Task.CompletedTask, heartbeat = Task.CompletedTask;
    public ExtensionManifest Manifest { get; }
    public ToolbarSnapshot? Latest { get; private set; }
    public event Action<ToolbarSnapshot>? SnapshotReceived;
    public event Action? Disconnected;
    private ToolbarHostClient(NamedPipeClientStream pipe, ExtensionManifest manifest) { this.pipe = pipe; Manifest = manifest; }
    public static async Task<ToolbarHostClient> ConnectAsync(string manifestPath, CancellationToken token = default)
    {
        await using var file = File.OpenRead(manifestPath);
        if (file.Length is < 1 or > 8192) throw new InvalidDataException("Manifest exceeds bound.");
        var manifest = await JsonSerializer.DeserializeAsync<ExtensionManifest>(file, ToolbarProtocol.Json, token) ?? throw new InvalidDataException("Missing manifest.");
        ValidateManifest(manifest);
        if (Path.GetFileName(manifestPath) != "manifest.json" || Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(manifestPath))) != manifest.Id) throw new InvalidDataException("Manifest directory identity differs.");
        var pipe = new NamedPipeClientStream(".", manifest.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var result = new ToolbarHostClient(pipe, manifest);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != manifest.ProviderPid) throw new InvalidDataException("Provider process differs from manifest.");
            using var process = Process.GetProcessById((int)pid);
            if (process.SessionId != Process.GetCurrentProcess().SessionId) throw new InvalidDataException("Provider Windows session differs.");
            if (process.StartTime.ToFileTimeUtc() != manifest.ProviderCreated) throw new InvalidDataException("Provider process was replaced.");
            await ToolbarProtocol.WriteAsync(pipe, ToolbarProtocol.Message("hello", new HostHello(manifest.Id, manifest.SessionId, manifest.Token), "hello"), deadline.Token);
            var welcome = await ToolbarProtocol.ReadAsync(pipe, deadline.Token);
            if (welcome.Type != "welcome" || welcome.RequestId != "hello" || welcome.Payload.GetProperty("extensionId").GetString() != manifest.Id || welcome.Payload.GetProperty("heartbeatSeconds").GetInt32() != ToolbarProtocol.HeartbeatSeconds || welcome.Payload.GetProperty("timeoutSeconds").GetInt32() != ToolbarProtocol.TimeoutSeconds) throw new InvalidDataException("Provider handshake differs.");
            result.reading = result.ReadLoop(); result.heartbeat = result.Heartbeat(); return result;
        }
        catch { pipe.Dispose(); result.stop.Dispose(); result.writing.Dispose(); throw; }
    }
    public static void ValidateManifest(ExtensionManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Protocol != ToolbarProtocol.Name || !ToolbarProtocol.ValidId(manifest.Id) || manifest.Surface != "toolbar" || manifest.DisplayName is not { Length: > 0 and <= 128 } || manifest.Version is not { Length: > 0 and <= 64 } || manifest.ProviderPid <= 0 || manifest.ProviderCreated <= 0 || manifest.SessionId is not { Length: 32 } || !manifest.SessionId.All(Uri.IsHexDigit) || manifest.Token is not { Length: 64 } || !manifest.Token.All(Uri.IsHexDigit) || manifest.PipeName != "MagicToolbarExtension-" + manifest.SessionId || manifest.HeartbeatSeconds != ToolbarProtocol.HeartbeatSeconds || manifest.TimeoutSeconds != ToolbarProtocol.TimeoutSeconds) throw new InvalidDataException("Invalid extension manifest.");
    }
    public async Task<bool> SetStateAsync(bool mounted, bool visible, CancellationToken token = default) => (await Request("host-state", new HostState(mounted, visible), token)).GetProperty("ok").GetBoolean();
    public async Task<bool> InvokeAsync(long revision, string id, CancellationToken token = default) => (await Request("action", new HostAction(revision, id), token)).GetProperty("ok").GetBoolean();
    public async Task<JsonElement> Request(string type, object payload, CancellationToken token = default)
    {
        stop.Token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        string id = "r-" + Guid.NewGuid().ToString("N"); var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (requests) { if (pending.Count >= 16) throw new InvalidOperationException("Too many outstanding requests."); pending[id] = response; }
        try
        {
            await writing.WaitAsync(deadline.Token);
            try { await ToolbarProtocol.WriteAsync(pipe, ToolbarProtocol.Message(type, payload, id), deadline.Token); }
            finally { writing.Release(); }
            return await response.Task.WaitAsync(deadline.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(ToolbarProtocol.TimeoutSeconds));
                var message = await ToolbarProtocol.ReadAsync(pipe, deadline.Token);
                if (message.Type == "snapshot")
                {
                    var view = message.Payload.Deserialize<ToolbarSnapshot>(ToolbarProtocol.Json) ?? throw new InvalidDataException("Missing snapshot.");
                    ToolbarProtocol.Validate(view.Content);
                    if (view.Revision <= 0 || (Latest != null && view.Revision <= Latest.Revision)) throw new InvalidDataException("Snapshot revision did not advance.");
                    Latest = view; SnapshotReceived?.Invoke(view);
                }
                else if (message.Type == "ack" && message.RequestId != null && pending.TryGetValue(message.RequestId, out var response)) response.TrySetResult(message.Payload);
                else throw new InvalidDataException("Unexpected provider message.");
            }
        }
        // Untrusted payloads and a failing subscriber affect this extension
        // connection only, never the host's other modules.
        catch (Exception) { }
        finally { stop.Cancel(); foreach (var response in pending.Values) response.TrySetCanceled(); try { Disconnected?.Invoke(); } catch { } }
    }
    private async Task Heartbeat()
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ToolbarProtocol.HeartbeatSeconds)); while (await timer.WaitForNextTickAsync(stop.Token)) await Request("ping", new { }, stop.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { stop.Cancel(); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); pipe.Dispose(); await Task.WhenAll(reading, heartbeat); writing.Dispose(); stop.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
