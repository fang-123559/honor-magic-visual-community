using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Magic.ToolbarExtension;

// One current-user host connection, bounded frames/queue, push on change, and
// host heartbeat. The host discovers manifests; providers never edit its layout.
public sealed class ToolbarProviderServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Func<HostState, Task<bool>> state;
    private readonly Func<string, Task> action;
    private readonly Func<int, bool> allowPeer;
    private readonly string path;
    private readonly Task listening;
    private readonly object gate = new();
    private Channel<ExtensionMessage>? outgoing;
    private ToolbarSnapshot? latest;
    private string? contentKey;
    private bool granted;
    private CancellationTokenSource? connection;
    private long revision;
    public ExtensionManifest Manifest { get; }
    public string ManifestPath => path;
    public event Action<string>? ConnectionChanged;

    public ToolbarProviderServer(string registryRoot, string id, string name, string version, Func<HostState, Task<bool>> state, Func<string, Task> action, Func<int, bool> allowPeer)
    {
        if (!ToolbarProtocol.ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 128 || version.Length > 64) throw new ArgumentException("Invalid extension identity.");
        this.state = state; this.action = action; this.allowPeer = allowPeer;
        string session = Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        Manifest = new(1, ToolbarProtocol.Name, id, name, version, "toolbar", "MagicToolbarExtension-" + session, session, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), process.Id, process.StartTime.ToFileTimeUtc(), ToolbarProtocol.HeartbeatSeconds, ToolbarProtocol.TimeoutSeconds);
        path = Path.Combine(Path.GetFullPath(registryRoot), id, "manifest.json");
        // Start listening before making discovery visible. No private host token.
        listening = Listen();
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Manifest, ToolbarProtocol.Json)); File.Move(path + ".tmp", path, true); }
        catch { stop.Cancel(); throw; }
    }

    public void Publish(ToolbarContent content)
    {
        ToolbarProtocol.Validate(content);
        // Keep immutable owned data even if a caller reuses its array afterwards.
        string key = JsonSerializer.Serialize(content, ToolbarProtocol.Json);
        lock (gate)
        {
            if (stop.IsCancellationRequested || contentKey == key) return;
            contentKey = key;
            latest = new(++revision, JsonSerializer.Deserialize<ToolbarContent>(key, ToolbarProtocol.Json)!);
            if (outgoing != null && !outgoing.Writer.TryWrite(ToolbarProtocol.Message("snapshot", latest))) connection?.Cancel();
        }
    }

    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Manifest.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || !allowPeer((int)peer)) continue;
                await Serve(pipe);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException or TimeoutException)
            { if (!stop.IsCancellationRequested) ConnectionChanged?.Invoke("disconnected"); }
            finally
            {
                lock (gate) { outgoing = null; connection = null; granted = false; }
                // On EOF, timeout or malformed input, return presentation to the
                // normal app. Never leave its window hidden by a dead host.
                try { await state(new(false, false)); } catch { }
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var first = await ToolbarProtocol.ReadAsync(pipe, helloTimeout.Token);
        var hello = first.Payload.Deserialize<HostHello>(ToolbarProtocol.Json);
        if (first.Type != "hello" || first.RequestId == null || hello == null || hello.ExtensionId != Manifest.Id || hello.SessionId != Manifest.SessionId || !SameToken(hello.Token, Manifest.Token)) return;
        var queue = Channel.CreateBounded<ExtensionMessage>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        lock (gate)
        {
            outgoing = queue; connection = lifetime;
            queue.Writer.TryWrite(ToolbarProtocol.Message("welcome", new { extensionId = Manifest.Id, heartbeatSeconds = ToolbarProtocol.HeartbeatSeconds, timeoutSeconds = ToolbarProtocol.TimeoutSeconds }, first.RequestId));
            if (latest != null) queue.Writer.TryWrite(ToolbarProtocol.Message("snapshot", latest));
        }
        ConnectionChanged?.Invoke("connected");
        var writing = WriteLoop();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(ToolbarProtocol.TimeoutSeconds));
                var frame = await ToolbarProtocol.ReadAsync(pipe, deadline.Token);
                if (frame.RequestId == null) throw new InvalidDataException("Requests require an identifier.");
                bool ok = false; string? reason = null;
                switch (frame.Type)
                {
                    case "ping": ok = true; break;
                    case "host-state":
                        var change = frame.Payload.Deserialize<HostState>(ToolbarProtocol.Json) ?? throw new InvalidDataException("Missing host state.");
                        if (change.Visible && !change.Mounted) throw new InvalidDataException("Visible content must be mounted.");
                        try { ok = await state(change).WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token); }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                        catch { reason = "presentation-failed"; lifetime.Cancel(); }
                        lock (gate) granted = change.Mounted && ok;
                        break;
                    case "action":
                        var request = frame.Payload.Deserialize<HostAction>(ToolbarProtocol.Json) ?? throw new InvalidDataException("Missing action.");
                        lock (gate) ok = granted && latest != null && request.Revision == latest.Revision && ToolbarProtocol.Actions(latest.Content).Contains(request.Id);
                        if (ok) {
                            try { await action(request.Id).WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token); }
                            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                            catch { ok = false; reason = "action-failed"; }
                        }
                        else reason = "stale-or-unoffered-action";
                        break;
                    default: reason = "unsupported-message"; break;
                }
                await queue.Writer.WriteAsync(ToolbarProtocol.Message("ack", new { ok, reason }, frame.RequestId), lifetime.Token);
            }
        }
        finally { lifetime.Cancel(); queue.Writer.TryComplete(); try { await writing; } catch (OperationCanceledException) { } }
        async Task WriteLoop()
        {
            try { await foreach (var message in queue.Reader.ReadAllAsync(lifetime.Token)) await ToolbarProtocol.WriteAsync(pipe, message, lifetime.Token); }
            finally { lifetime.Cancel(); }
        }
    }
    private static bool SameToken(string? supplied, string expected) => supplied != null && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); lock (gate) connection?.Cancel(); await listening;
        // An older provider must never delete a newer provider's manifest.
        try { var current = JsonSerializer.Deserialize<ExtensionManifest>(await File.ReadAllTextAsync(path), ToolbarProtocol.Json); if (current?.SessionId == Manifest.SessionId) File.Delete(path); } catch (IOException) { } catch (JsonException) { }
        stop.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
