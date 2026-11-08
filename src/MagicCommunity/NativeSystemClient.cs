using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagicCommunity;

// Explicit candidate only. Event waits have a separate authenticated transport
// so a pending next request never queues ordinary actions behind its timeout.
internal sealed class NativeSystemClient : IAsyncDisposable
{
    internal const string Protocol = "Magic.System.Native/1";
    private const int MaximumFrame = 1024 * 1024;
    private readonly Process child;
    private readonly NamedPipeClientStream control, events;
    private sealed class RecoverableChannel(string name) : IDisposable
    {
        internal readonly string Name = name;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal NamedPipeClientStream? Stream;
        public void Dispose() { Stream?.Dispose(); Stream = null; }
    }
    private readonly RecoverableChannel wifi, bluetooth, urgent;
    private readonly SemaphoreSlim controlGate = new(1, 1), eventGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closing;
    private volatile bool broken;
    internal int ProcessId => child.Id;
    internal string PipeName { get; }
    private NativeSystemClient(Process process, string name)
    {
        child = process; PipeName = name;
        control = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        events = new(".", name + "-events", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        urgent = new(name + "-urgent");
        wifi = new(name + "-devices"); bluetooth = new(name + "-bluetooth");
    }
    private async Task<NamedPipeClientStream> ConnectChannel(RecoverableChannel channel, CancellationToken cancellation)
    {
        if (channel.Stream is { IsConnected: true } connected) return connected;
        channel.Dispose();
        var stream = new NamedPipeClientStream(".", channel.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await stream.ConnectAsync(3000, cancellation);
            if (!GetNamedPipeServerProcessId(stream.SafePipeHandle, out uint server) || server != child.Id)
                throw new InvalidDataException("Native system channel server identity differs.");
            return channel.Stream = stream;
        }
        catch { stream.Dispose(); throw; }
    }
    internal static async Task<NativeSystemClient> Start(string executable, string expectedSha256, CancellationToken cancellation, string? dataDirectory = null)
    {
        executable = Path.GetFullPath(executable);
        using (var input = File.OpenRead(executable))
            if (expectedSha256.Length != 64 || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(input)), expectedSha256))
                throw new InvalidDataException("Native system candidate hash differs.");
        using var parent = Process.GetCurrentProcess();
        var image = new StringBuilder(32768); uint imageSize = (uint)image.Capacity;
        if (!QueryFullProcessImageName(parent.Handle, 0, image, ref imageSize))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string name = "MagicNativeSystem-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var value in new[] { "--pipe", name, "--parent", parent.Id.ToString(), "--parent-created", parent.StartTime.ToFileTimeUtc().ToString(), "--parent-image", image.ToString() })
            start.ArgumentList.Add(value);
        if (dataDirectory is not null) { start.ArgumentList.Add("--data"); start.ArgumentList.Add(Path.GetFullPath(dataDirectory)); }
        var child = Process.Start(start) ?? throw new IOException("Native system did not start.");
        var client = new NativeSystemClient(child, name);
        try
        {
            foreach (var stream in new[] { client.control, client.events })
            {
                await stream.ConnectAsync(5000, cancellation);
                if (!GetNamedPipeServerProcessId(stream.SafePipeHandle, out uint server) || server != child.Id)
                    throw new InvalidDataException("Native system pipe server differs from the started process.");
            }
            await client.ConnectChannel(client.urgent, cancellation);
            await client.ConnectChannel(client.wifi, cancellation);
            await client.ConnectChannel(client.bluetooth, cancellation);
            var health = await client.Request("health", new { }, cancellation);
            if (health.GetProperty("protocol").GetString() != Protocol || health.GetProperty("pid").GetInt32() != child.Id)
                throw new InvalidDataException("Native system handshake differs.");
            return client;
        }
        catch (Exception failure)
        {
            client.control.Dispose(); client.events.Dispose(); client.urgent.Dispose(); client.wifi.Dispose(); client.bluetooth.Dispose();
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) when (child.HasExited) { }
            await child.WaitForExitAsync(CancellationToken.None);
            string diagnostic = await child.StandardError.ReadToEndAsync();
            child.Dispose(); client.controlGate.Dispose(); client.eventGate.Dispose(); client.urgent.Gate.Dispose(); client.wifi.Gate.Dispose(); client.bluetooth.Gate.Dispose(); client.lifetime.Dispose();
            throw new IOException("Native system startup failed: " + diagnostic, failure);
        }
    }
    internal async Task<JsonElement> Request(string operation, object arguments, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(operation == "notifications-request-access" ? 95 : 12)); cancellation = deadline.Token;
        bool interactive = operation is "window-action" or "input-target" or "input-read" or "input-select" or "input-profile-select" or "health-fast";
        var recoverable = interactive ? urgent : operation.StartsWith("wifi-", StringComparison.Ordinal) ? wifi : operation.StartsWith("bluetooth-", StringComparison.Ordinal) ? bluetooth : null;
        var gate = recoverable?.Gate ?? (operation == "next" ? eventGate : controlGate);
        try { await gate.WaitAsync(cancellation); }
        catch (OperationCanceledException) when (recoverable is not null && !lifetime.IsCancellationRequested)
        { throw new NativeSystemRequestException("上一次操作尚未返回，请稍后重试。", null, operation); }
        try
        {
            if (broken) throw new IOException("Native system connection is no longer usable.");
            string id = Guid.NewGuid().ToString("N");
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { protocol = Protocol, id, operation, arguments });
            if (body.Length > MaximumFrame) throw new InvalidDataException("Native system request exceeds bound.");
            byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
            NamedPipeClientStream? stream = null;
            try
            {
                stream = recoverable is not null ? await ConnectChannel(recoverable, cancellation) : operation == "next" ? events : control;
                await stream.WriteAsync(length, cancellation); await stream.WriteAsync(body, cancellation); await stream.FlushAsync(cancellation);
                await stream.ReadExactlyAsync(length, cancellation);
                int size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size is < 1 or > MaximumFrame) throw new InvalidDataException("Native system response exceeds bound.");
                byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                var value = document.RootElement;
                if (value.GetProperty("protocol").GetString() != Protocol || value.GetProperty("id").GetString() != id)
                    throw new InvalidDataException("Native system response identity differs.");
                if (!value.GetProperty("ok").GetBoolean())
                    throw new NativeSystemRequestException(value.GetProperty("error").GetString() ?? "Native system rejected request.", value.TryGetProperty("errorCode", out var code) ? code.GetInt32() : null, operation);
                return value.GetProperty("result").Clone();
            }
            catch (NativeSystemRequestException) { throw; }
            // Discard this connection, not the provider for the rest of the
            // session. A later explicit request reconnects with server identity
            // validation. Never replay an action whose outcome is uncertain.
            catch (Exception error) when(recoverable is not null && error is not OutOfMemoryException)
            { recoverable.Dispose(); throw new NativeSystemRequestException("操作超时或连接中断，可重新读取状态；上次操作不会自动重放。", null, operation); }
            catch { broken = true; stream?.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) { await disposed.Task; return; }
        bool heldControl = false, heldEvents = false, heldUrgent=false, heldWifi=false, heldBluetooth=false;
        try
        {
            lifetime.Cancel(); control.Dispose(); events.Dispose(); urgent.Dispose(); wifi.Dispose(); bluetooth.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await controlGate.WaitAsync(timeout.Token); heldControl = true;
            await eventGate.WaitAsync(timeout.Token); heldEvents = true;
            await urgent.Gate.WaitAsync(timeout.Token); heldUrgent = true;
            await wifi.Gate.WaitAsync(timeout.Token); heldWifi = true;
            await bluetooth.Gate.WaitAsync(timeout.Token); heldBluetooth = true;
            await child.WaitForExitAsync(timeout.Token);
            disposed.TrySetResult();
        }
        catch (Exception error) { disposed.TrySetException(error); throw; }
        finally
        {
            child.Dispose(); lifetime.Dispose();
            if (heldControl) controlGate.Dispose();
            if (heldEvents) eventGate.Dispose();
            if (heldUrgent) urgent.Gate.Dispose();
            if (heldWifi) wifi.Gate.Dispose();
            if (heldBluetooth) bluetooth.Gate.Dispose();
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
internal sealed class NativeSystemRequestException(string message, int? code = null, string? operation = null) : Exception(message)
{ internal int? ErrorCode { get; } = code; internal string? Operation { get; } = operation; }
