using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace MagicCommunity;

// Native built-in card: one same-user/session subscription, only for visible
// instances. External data is normalized before crossing into the renderer.
internal sealed class CompositionEncoDeckCardData : IDisposable
{
    private const int MaximumLineLength = 8192;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private readonly Func<JsonElement, Task> emit;
    private readonly string pipeName;
    private readonly Func<JsonElement> unavailable;
    private readonly Func<int, bool>? acceptPeer;
    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private Session? running;
    private bool demanded, attempted, restartRequested, disposed;
    private long nextId, attempts, published, duplicates;
    private sealed class Session : IDisposable
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly Channel<JsonElement> Updates = Channel.CreateBounded<JsonElement>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        internal NamedPipeClientStream? Pipe;
        internal StreamWriter? Writer;
        internal JsonElement State;
        internal long PendingId;
        internal TaskCompletionSource<bool>? Pending;
        internal bool Connected;
        internal void Cancel() { Stop.Cancel(); Pipe?.Dispose(); Pending?.TrySetResult(false); }
        public void Dispose() { Pipe?.Dispose(); Stop.Dispose(); }
    }
    internal CompositionEncoDeckCardData(Func<JsonElement, Task> emit, string? pipeName = null, Func<JsonElement>? unavailable = null, Func<int, bool>? acceptPeer = null)
    { this.emit = emit; this.pipeName = pipeName ?? "EncoDeck.DesktopCard.v1.s" + Process.GetCurrentProcess().SessionId; this.unavailable = unavailable ?? (() => JsonSerializer.SerializeToElement(new { available = false, connecting = false })); this.acceptPeer = acceptPeer; }
    public bool IsConnected { get { lock (gate) return demanded && running is { Connected: true }; } }
    internal object Diagnostics { get { lock (gate) return new { demanded, active = running is not null, connected = running?.Connected == true,
        attempts = Interlocked.Read(ref attempts), published = Interlocked.Read(ref published), duplicates = Interlocked.Read(ref duplicates),
        pending = running?.Pending is not null }; } }

    public void Demand(bool value)
    {
        lock (gate)
        {
            if (disposed) return;
            demanded = value;
            if (!value) { attempted = false; restartRequested = false; running?.Cancel(); return; }
            // Desktop changes do not repeatedly reconnect a failed provider.
            if (!attempted) { if (running is not null) restartRequested = true; else StartLocked(); }
        }
    }
    public void Retry()
    {
        lock (gate)
        {
            if (!demanded || disposed) return;
            if (running is null) { attempted = false; StartLocked(); }
            else if (!running.Connected && !running.Stop.IsCancellationRequested) return;
            else { restartRequested = true; running.Cancel(); }
        }
    }
    internal void ProviderStarted()
    {
        lock (gate)
        {
            if (!demanded || disposed || running?.Connected == true) return;
            if (running is null) StartLocked();
            else restartRequested = true;
        }
    }
    private void StartLocked()
    {
        if (!demanded || disposed || running is not null) return;
        attempted = true; var owner = new Session(); running = owner;
        Interlocked.Increment(ref attempts); _ = Task.Run(() => RunAsync(owner));
    }
    private async Task<bool> Emit(Session owner, JsonElement value, bool cancelled = false)
    {
        lock (gate) if (disposed || !demanded || running != owner || (!cancelled && owner.Stop.IsCancellationRequested)) return false;
        await emit(value).ConfigureAwait(false); Interlocked.Increment(ref published); return true;
    }
    private async Task Pump(Session owner)
    {
        long last = 0;
        while (await owner.Updates.Reader.WaitToReadAsync(owner.Stop.Token).ConfigureAwait(false))
        {
            int delay = (int)Math.Clamp(200 - (Environment.TickCount64 - last), 0, 200);
            if (delay > 0) await Task.Delay(delay, owner.Stop.Token).ConfigureAwait(false);
            JsonElement latest = default;
            while (owner.Updates.Reader.TryRead(out var value)) latest = value;
            if (latest.ValueKind == JsonValueKind.Object) { await Emit(owner, latest).ConfigureAwait(false); last = Environment.TickCount64; }
        }
    }
    private async Task RunAsync(Session owner)
    {
        bool failed = false; Task? pump = null;
        try
        {
            await Emit(owner, JsonSerializer.SerializeToElement(new { available = false, connecting = true })).ConfigureAwait(false);
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification, HandleInheritability.None);
            lock (gate) { if (disposed || !demanded || running != owner) return; owner.Pipe = client; }
            await client.ConnectAsync(1200, owner.Stop.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) throw new IOException("EncoDeck peer unavailable.");
            using (var peer = Process.GetProcessById((int)pid))
            using (var self = Process.GetCurrentProcess())
                if (peer.SessionId != self.SessionId || acceptPeer is not null && !acceptPeer((int)pid)) throw new IOException("EncoDeck peer identity differs from the selected application.");
            using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
            using var output = new StreamWriter(client, new UTF8Encoding(false, true), 1024, leaveOpen: true);
            await Write(output, new { version = 1, op = "subscribe", client = "MagicCommunity" }, owner.Stop.Token).ConfigureAwait(false);
            lock (gate) { if (disposed || !demanded || running != owner || owner.Stop.IsCancellationRequested) return; owner.Writer = output; }
            pump = Pump(owner);
            var lines = new BoundedLines(reader); string? previous = null;
            using var firstState = CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token);
            firstState.CancelAfter(TimeSpan.FromSeconds(3));
            long window = Environment.TickCount64; int messages = 0;
            while (!owner.Stop.IsCancellationRequested)
            {
                string? line = await lines.Read(owner.State.ValueKind == JsonValueKind.Object ? owner.Stop.Token : firstState.Token).ConfigureAwait(false);
                if (line is null) { failed = true; break; }
                long now = Environment.TickCount64;
                if (now - window >= 1000) { window = now; messages = 0; }
                if (++messages > 120) throw new InvalidDataException("EncoDeck message rate exceeds bound.");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 12 });
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("EncoDeck frame must be an object.");
                RejectDuplicates(message);
                if (message.TryGetProperty("version", out var version) && (!version.TryGetInt32(out int v) || v != 1)) throw new InvalidDataException("EncoDeck version differs.");
                string type = Text(message, "type", 16);
                if (type == "state")
                {
                    var state = Normalize(message.GetProperty("data")); string key = state.GetRawText();
                    lock (gate) { if (disposed || !demanded || running != owner || owner.Stop.IsCancellationRequested) return; owner.State = state; owner.Connected = true; restartRequested = false; }
                    if (key == previous) { Interlocked.Increment(ref duplicates); continue; }
                    previous = key; owner.Updates.Writer.TryWrite(state);
                }
                else if (type == "result" && message.TryGetProperty("id", out var id) && id.TryGetInt64(out long requestId))
                {
                    lock (gate) if (running == owner && owner.PendingId == requestId) owner.Pending?.TrySetResult(Boolean(message, "ok"));
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failed = !owner.Stop.IsCancellationRequested; }
        finally
        {
            lock (gate) { owner.Connected = false; owner.Cancel(); owner.Updates.Writer.TryComplete(); }
            if (pump is not null) { try { await pump.ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { } }
            if (failed) { try { await Emit(owner, unavailable(), true).ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException) { } }
            lock (gate)
            {
                if (running == owner) running = null;
                owner.Dispose();
                if (demanded && !disposed && restartRequested) { restartRequested = false; attempted = false; StartLocked(); }
            }
        }
    }
    public async Task<bool> SendActionAsync(string action, int? choice, CancellationToken cancellationToken)
    {
        Session owner; TaskCompletionSource<bool> completion; long id; CancellationToken ownerToken;
        lock (gate)
        {
            if (disposed || !demanded || running is not { Connected: true, Writer: not null } active || active.Pending is not null ||
                !Allowed(active.State, action, choice)) return false;
            owner = active; id = ++nextId; completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.PendingId = id; owner.Pending = completion; ownerToken = owner.Stop.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await Write(owner.Writer!, new { version = 1, id, action, choice }, linked.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { return false; }
        finally { lock (gate) if (owner.PendingId == id) owner.Pending = null; }
    }
    internal static bool Allowed(JsonElement state, string action, int? choice)
    {
        if (action == "refresh") return choice is null;
        if (state.ValueKind != JsonValueKind.Object || !Boolean(state, "canSetNoise") || choice is not (>= 0 and <= 255)) return false;
        if (action == "setNoiseMode") return choice < state.GetProperty("modes").GetArrayLength();
        if (action == "setNoiseOption") return state.GetProperty("levels").EnumerateArray().Any(item => item.GetProperty("index").GetInt32() == choice);
        return false;
    }
    private async Task Write(StreamWriter target, object value, CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try { await target.WriteLineAsync(JsonSerializer.Serialize(value, Wire).AsMemory(), token).ConfigureAwait(false); await target.FlushAsync(token).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }
    internal static JsonElement Normalize(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("EncoDeck state must be an object.");
        RejectDuplicates(data);
        bool available = Boolean(data, "available"), linked = Boolean(data, "deviceConnected"), ready = Boolean(data, "controlReady");
        object Battery(string name)
        {
            if (!data.TryGetProperty(name, out var b) || b.ValueKind == JsonValueKind.Null) return new { level = (double?)null, charging = false, stale = true };
            if (b.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid battery object.");
            double? level = null;
            if (b.TryGetProperty("level", out var l) && l.ValueKind != JsonValueKind.Null)
            { if (!l.TryGetDouble(out double n) || !double.IsFinite(n) || n < -1 || (n < 0 && n != -1) || n > 100) throw new InvalidDataException("Invalid battery percent."); level = n == -1 ? null : n; }
            return new { level, charging = Boolean(b, "charging"), stale = !linked || Boolean(b, "stale", true) };
        }
        var modes = new List<object>(); var levels = new List<object>();
        if (data.TryGetProperty("modes", out var m))
        {
            if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() > 4) throw new InvalidDataException("Invalid modes.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in m.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid mode.");
                string mode = Text(item, "mode", 64), label = Text(item, "label", 24); if (mode.Length == 0 || !names.Add(mode)) throw new InvalidDataException("Invalid mode identity."); modes.Add(new { mode, label }); }
        }
        if (data.TryGetProperty("levels", out var l))
        {
            if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() > 4) throw new InvalidDataException("Invalid levels.");
            var indices = new HashSet<int>();
            foreach (var item in l.EnumerateArray()) { if (item.ValueKind != JsonValueKind.Object || !item.GetProperty("index").TryGetInt32(out int index) || index < 0 || index > 255 || !indices.Add(index)) throw new InvalidDataException("Invalid level index.");
                levels.Add(new { index, label = Text(item, "label", 24), selected = Boolean(item, "selected") }); }
        }
        return JsonSerializer.SerializeToElement(new { available, providerStatus = "desktop-card-bridge", programRunning = true, deviceName = Text(data, "deviceName", 80), deviceConnected = linked, controlReady = ready,
            left = Battery("left"), right = Battery("right"), @case = Battery("case"), noiseMode = Text(data, "noiseMode", 64),
            noiseStale = Boolean(data, "noiseStale", true), modes, levels, canSetNoise = available && linked && ready && Boolean(data, "canSetNoise") }, Wire);
    }
    private static bool Boolean(JsonElement value, string name, bool fallback = false)
    {
        if (!value.TryGetProperty(name, out var result)) return fallback;
        if (result.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid boolean.");
        return result.GetBoolean();
    }
    private static string Text(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var result)) return "";
        if (result.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid text.");
        string text = result.GetString()!;
        if (text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("Text exceeds card bounds.");
        return text;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var field in value.EnumerateObject()) { if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate field."); RejectDuplicates(field.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    internal sealed class BoundedLines(TextReader reader)
    {
        private readonly char[] buffer = new char[1024];
        private int offset, count;
        internal async Task<string?> Read(CancellationToken token)
        {
            var line = new StringBuilder(); int received = 0;
            while (true)
            {
                if (offset == count) { count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); offset = 0;
                    if (count == 0) { if (received != 0) throw new EndOfStreamException("Truncated EncoDeck frame."); return null; } }
                char c = buffer[offset++]; if (c == '\n') return line.ToString();
                if (++received > MaximumLineLength) throw new InvalidDataException("EncoDeck frame exceeds bound.");
                if (c != '\r') line.Append(c);
            }
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; demanded = false; restartRequested = false; running?.Cancel(); } }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint process);
}
