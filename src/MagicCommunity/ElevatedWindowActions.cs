using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MagicCommunity;

// One owner / one interactive login session. No elevated process until a real
// Dock operation requires it; the normal owner hosts the authenticated pipe.
internal sealed class ElevatedWindowActions : IDisposable
{
    internal const string Protocol = "Magic.WindowAction/1";
    private readonly NativeSystemDeployment deployment;
    private readonly Action<string, object> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private ulong sequence;
    private long startupAttempt;
    private int disposed;
    private long retryAfter;
    internal ElevatedWindowActions(NativeSystemDeployment deployment, Action<string, object> log) { this.deployment = deployment; this.log = log; }
    internal async Task Ensure(CancellationToken cancellation)
    {
        long observed=Interlocked.Read(ref startupAttempt);
        bool alreadyStarting=gate.CurrentCount==0;
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (pipe?.IsConnected == true && helper is not null && !helper.HasExited) return;
            if(alreadyStarting||observed!=Interlocked.Read(ref startupAttempt))throw new InvalidOperationException("本次管理员授权未完成；之后点击可重新授权。");
            Interlocked.Increment(ref startupAttempt);
            var prior = helper;
            if(prior is not null) { pipe?.Dispose(); pipe=null; try { await prior.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(2),cancellation); } catch(TimeoutException) { throw new InvalidOperationException("上一次管理员窗口助手尚未退出，请稍后重试。"); } }
            Disconnect();
            if (Environment.TickCount64 < Interlocked.Read(ref retryAfter)) throw new InvalidOperationException("管理员授权刚结束，请稍候再点击一次重试。");
            string executable = Path.Combine(deployment.CodeDirectory, "MagicWindowAction.exe");
            using (var input = File.OpenRead(executable))
                if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(input)), deployment.Files["MagicWindowAction.exe"])) throw new InvalidDataException("管理员窗口助手校验失败。");
            string nonce = Guid.NewGuid().ToString("N"), name = "Magic.WindowAction." + nonce;
            pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16384, 16384);
            using var owner = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = deployment.CodeDirectory };
            start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(owner.StartTime.ToFileTimeUtc().ToString()); start.ArgumentList.Add(nonce);
            log("window_helper_uac_requested", new { protocol = Protocol, sessionId = owner.SessionId });
            // ShellExecute may wait for the secure desktop. Never block WPF or the
            // renderer; cancellation closes the pipe even if consent arrives late.
            helper = await Task.Run(() =>
            {
                var process = Process.Start(start) ?? throw new InvalidOperationException("管理员窗口助手未启动。");
                if (Volatile.Read(ref disposed) != 0) { process.Dispose(); throw new OperationCanceledException(); }
                return process;
            }, cancellation);
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            ready.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                await pipe.WaitForConnectionAsync(ready.Token);
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) && peer == helper.Id &&
                    helper.SessionId == owner.SessionId &&
                    StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(ProcessImage((uint)helper.Id)), Path.GetFullPath(executable))) break;
                pipe.Disconnect(); // untrusted connection cannot supply commands or shut the helper down
            }
            var hello = await Read(pipe, ready.Token);
            if (hello.GetProperty("protocol").GetString() != Protocol || hello.GetProperty("nonce").GetString() != nonce) throw new InvalidDataException("管理员窗口助手协议不兼容。");
            sequence = 0;
            log("window_helper_ready", new { pid = helper.Id, sessionId = owner.SessionId, protocol = Protocol });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Disconnect(); Interlocked.Exchange(ref retryAfter, Environment.TickCount64 + 1000);
            log("window_helper_uac_cancelled", new { win32Error = 1223 });
            throw new InvalidOperationException("已取消管理员窗口授权；之后点击可重新授权。", error);
        }
        catch { Disconnect(); Interlocked.Exchange(ref retryAfter, Environment.TickCount64 + 1000); throw; }
        finally { gate.Release(); }
    }
    internal async Task<JsonElement> Perform(JsonElement identity, string action, CancellationToken cancellation)
    {
        if (action is not ("activate" or "restore" or "minimize" or "close")) throw new InvalidDataException("管理员助手不支持该操作。");
        await gate.WaitAsync(cancellation);
        try
        {
            if (pipe?.IsConnected != true || helper is null || helper.HasExited) throw new InvalidOperationException("管理员窗口助手已退出，请再次点击重试。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            string id = Guid.NewGuid().ToString("N");
            bool foregroundGranted = (action is "activate" or "restore") && AllowSetForegroundWindow((uint)helper.Id);
            var request = new { protocol = Protocol, id, sequence = (++sequence).ToString(), action,
                hwnd = identity.GetProperty("hwnd").GetString(), pid = identity.GetProperty("pid").GetString(),
                created = identity.GetProperty("created").GetString(), path = identity.GetProperty("path").GetString() };
            await Write(pipe, request, deadline.Token);
            var result = await Read(pipe, deadline.Token);
            if (result.GetProperty("protocol").GetString() != Protocol || result.GetProperty("id").GetString() != id) throw new InvalidDataException("管理员窗口助手响应身份不匹配。");
            log("window_helper_action", new { id, action, helperPid = helper.Id, foregroundGranted, result });
            return result;
        }
        catch { Disconnect(); throw; }
        finally { gate.Release(); }
    }
    internal static async Task Write(Stream stream, object value, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is 0 or > 16384) throw new InvalidDataException("Window action frame exceeds bound.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation);
        await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
    internal static async Task<JsonElement> Read(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        int count = BitConverter.ToInt32(header);
        if (count is <= 0 or > 16384) throw new InvalidDataException("Window action frame exceeds bound.");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, cancellation);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.Clone();
    }
    private void Disconnect() { var old = pipe; pipe = null; old?.Dispose(); helper?.Dispose(); helper = null; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        // EOF is authenticated by ownership of this private server endpoint.
        // The helper also waits on the exact owner process handle, including crash.
        Disconnect();
    }
    private static string ProcessImage(uint pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if(process.IsInvalid) throw new Win32Exception();
        uint count=32768; var path=new StringBuilder((int)count);
        if(!QueryFullProcessImageName(process,0,path,ref count)) throw new Win32Exception();
        return path.ToString();
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}

// Installed on the WPF dispatcher (which pumps WinEvents), before UAC is shown.
// Together with the backend's opaque window generation, this invalidates the
// original click when its HWND is destroyed while consent is pending.
internal sealed class WindowActionObservation : IDisposable
{
    private readonly IntPtr target;
    private readonly WinEvent callback;
    private IntPtr hook;
    private int expired;
    internal bool Expired => Volatile.Read(ref expired) != 0;
    internal WindowActionObservation(IntPtr target)
    {
        this.target = target; callback = OnEvent;
        hook = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, callback, 0, 0, 0);
        if (hook == IntPtr.Zero) throw new Win32Exception();
    }
    private void OnEvent(IntPtr source, uint evt, IntPtr window, int objectId, int child, uint thread, uint time) { if (window == target && objectId == 0 && child == 0) Interlocked.Exchange(ref expired, 1); }
    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; } GC.KeepAlive(callback); }
    private delegate void WinEvent(IntPtr hook, uint evt, IntPtr window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
