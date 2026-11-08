using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MagicCommunity;

internal sealed class NativeTaskbarLease : IAsyncDisposable
{
    private readonly EventWaitHandle stop, show, hide, ready, expired;
    private Process? guard;
    private RegisteredWaitHandle? expiry;
    private string? recoveryJournal;
    private int closing;
    internal int ProcessId => guard?.Id ?? 0;
    internal bool IsAlive => guard is { HasExited: false };
    private NativeTaskbarLease(string prefix)
    {
        stop = new(false, EventResetMode.ManualReset, prefix + "-stop"); show = new(false, EventResetMode.ManualReset, prefix + "-show"); hide = new(false, EventResetMode.ManualReset, prefix + "-hide"); ready = new(false, EventResetMode.ManualReset, prefix + "-ready"); expired = new(false, EventResetMode.ManualReset, prefix + "-expired");
    }
    internal static async Task<NativeTaskbarLease> Start(string executable, string sha256, int rendererPid, int backendPid, DateTimeOffset? expires, string directory, Action? onExpired = null, IntPtr fixtureWindow = default, bool continuous = false)
    {
        using (var file = File.OpenRead(executable)) if (sha256.Length != 64 || !Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Taskbar guard hash differs.");
        if (continuous ? expires.HasValue : !expires.HasValue || expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddHours(1)) throw new InvalidDataException("Taskbar acceptance deadline differs from its explicit mode.");
        Directory.CreateDirectory(directory); string prefix = "Local\\MagicTaskbar-" + Guid.NewGuid().ToString("N"); var lease = new NativeTaskbarLease(prefix);
        try
        {
            string guardLog = Path.Combine(directory, "taskbar-" + Guid.NewGuid().ToString("N") + ".jsonl");
            if (fixtureWindow == IntPtr.Zero) lease.recoveryJournal = NativeTaskbarRecovery.Begin(directory, guardLog);
            var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true };
            void Pair(string key, string value) { start.ArgumentList.Add(key); start.ArgumentList.Add(value); }
            void Owner(string option, int pid) { using var process = Process.GetProcessById(pid); var path = new StringBuilder(32768); uint n = (uint)path.Capacity; if (!QueryFullProcessImageName(process.Handle, 0, path, ref n)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); Pair(option, pid.ToString()); Pair(option == "--parent" ? "--created" : option + "-created", process.StartTime.ToFileTimeUtc().ToString()); Pair(option == "--parent" ? "--image" : option + "-image", path.ToString()); }
            if (lease.recoveryJournal is not null) Pair("--original-state", NativeTaskbarRecovery.Original(lease.recoveryJournal).ToString());
            Owner("--parent", Environment.ProcessId); Owner("--renderer", rendererPid); Owner("--backend", backendPid); Pair("--events", prefix); Pair("--mode",continuous ? "continuous" : "timed"); Pair("--expires", expires?.UtcDateTime.ToFileTimeUtc().ToString() ?? "0"); Pair("--log", guardLog); if (fixtureWindow != IntPtr.Zero) Pair("--fixture", fixtureWindow.ToInt64().ToString());
            lease.guard = Process.Start(start) ?? throw new IOException("Taskbar guard did not start.");
            using var timeout = new CancellationTokenSource(4000);
            var signaled = Task.Run(() => lease.ready.WaitOne(3500)); var ended = lease.guard.WaitForExitAsync(timeout.Token); await Task.WhenAny(signaled, ended); if (!signaled.IsCompletedSuccessfully || !signaled.Result || lease.guard.HasExited) throw new IOException("Taskbar guard failed before readiness.");
            lease.expiry = ThreadPool.RegisterWaitForSingleObject(lease.expired, (_, timedOut) => { if (!timedOut && Volatile.Read(ref lease.closing) == 0) onExpired?.Invoke(); }, null, Timeout.Infinite, true);
            lease.guard.EnableRaisingEvents = true;
            lease.guard.Exited += (_, _) => { if (Volatile.Read(ref lease.closing) == 0) { try { if (lease.recoveryJournal is not null) NativeTaskbarRecovery.Complete(lease.recoveryJournal); } catch { } onExpired?.Invoke(); } };
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    internal void RequestStop() { if (Volatile.Read(ref closing) == 0) try { stop.Set(); } catch (ObjectDisposedException) { } }
    internal void RevealWindowsEntry() { ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this); show.Set(); }
    internal void HideWindowsEntry() { ObjectDisposedException.ThrowIf(Volatile.Read(ref closing) != 0, this); hide.Set(); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        expiry?.Unregister(null); expiry = null;
        try
        {
            stop.Set();
            if (guard is not null) { using var wait = new CancellationTokenSource(5000); await guard.WaitForExitAsync(wait.Token); if (recoveryJournal is not null) NativeTaskbarRecovery.Complete(recoveryJournal); }
        }
        finally
        {
            guard?.Dispose(); guard = null;
            stop.Dispose(); show.Dispose(); hide.Dispose(); ready.Dispose(); expired.Dispose();
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref uint size);
}

// Durable intent is written before changing Explorer state. Recovery only
// restores a dead owner's exact lease while Windows still has the leased value.
internal static class NativeTaskbarRecovery
{
    private sealed record Window(long Hwnd, int Pid, long Created, bool Visible);
    private sealed record Record(int SchemaVersion, bool Active, int OwnerPid, long OwnerCreated, string OwnerImage, uint OriginalState, uint LeasedState, Window[] Windows, string GuardLog);
    private static readonly object gate = new();
    internal static void RecoverStale(string directory) { lock (gate) { string path = Path.Combine(directory, "lease.json"); if (!File.Exists(path)) return; var record = Read(path); if (record.Active && !Alive(record)) Restore(path, record); } }
    internal static uint Original(string journal) => Read(journal).OriginalState;
    internal static string Begin(string directory, string guardLog)
    {
        lock (gate)
        {
            string path = Path.Combine(directory, "lease.json"); if (File.Exists(path)) { var old = Read(path); if (old.Active) { if (Alive(old)) throw new IOException("Another native taskbar lease is still active."); Restore(path, old); } }
            uint original = State(); if (original > 3) throw new InvalidDataException("Unknown Windows taskbar state."); var windows = new List<Window>();
            EnumWindows((h, _) => { if (Verified(h, out int pid, out long created)) windows.Add(new(h.ToInt64(), pid, created, IsWindowVisible(h))); return windows.Count < 32; }, IntPtr.Zero);
            if (windows.Count == 0) throw new IOException("No validated Windows taskbar is available."); using var owner = Process.GetCurrentProcess(); var record = new Record(1, true, owner.Id, owner.StartTime.ToFileTimeUtc(), ProcessImage(owner), original, original | 1, windows.ToArray(), guardLog); Write(path, record); return path;
        }
    }
    internal static void Complete(string path) { lock (gate) { var record = Read(path); if (record.Active && record.OwnerPid == Environment.ProcessId) { using var owner = Process.GetCurrentProcess(); if (record.OwnerCreated == owner.StartTime.ToFileTimeUtc() && record.OwnerImage.Equals(ProcessImage(owner), StringComparison.OrdinalIgnoreCase)) Restore(path, record); } } }
    private static Record Read(string path) { if (new FileInfo(path).Length > 16384 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Taskbar recovery record exceeds bound or is a link."); var record = System.Text.Json.JsonSerializer.Deserialize<Record>(File.ReadAllBytes(path)) ?? throw new InvalidDataException(); if (record.SchemaVersion != 1 || record.OriginalState > 3 || record.LeasedState != (record.OriginalState | 1) || record.Windows.Length > 32 || !Path.GetFullPath(record.GuardLog).StartsWith(Path.GetDirectoryName(Path.GetFullPath(path))! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Taskbar recovery record differs."); return record; }
    private static void Write(string path, Record record) { string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record); using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(data); file.Flush(true); } File.Move(temp, path, true); }
    private static bool Alive(Record record) { try { using var owner = Process.GetProcessById(record.OwnerPid); return !owner.HasExited && owner.StartTime.ToFileTimeUtc() == record.OwnerCreated && ProcessImage(owner).Equals(record.OwnerImage, StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } catch (System.ComponentModel.Win32Exception) { return true; } }
    internal static bool ShouldRestore(uint original, uint leased, uint current, bool userChanged) => !userChanged && original != leased && current == leased;
    internal static bool UserChanged(string log)
    {
        if (!File.Exists(log)) return false;
        var info = new FileInfo(log);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Taskbar outcome record is a link.");
        // Older guards could grow this log without bound after a reveal timeout.
        // Treat an oversized history conservatively: preserve current appbar
        // preferences, but still recover verified windows and end the dead lease.
        if (info.Length > 65536) return true;
        foreach (string line in File.ReadLines(log))
        {
            try { using var value = System.Text.Json.JsonDocument.Parse(line); if (value.RootElement.TryGetProperty("userChanged", out var changed) && changed.ValueKind == System.Text.Json.JsonValueKind.True) return true; }
            catch (System.Text.Json.JsonException) { }
        }
        return false;
    }
    private static void Restore(string path, Record record)
    {
        if (ShouldRestore(record.OriginalState, record.LeasedState, State(), UserChanged(record.GuardLog))) { var data = new Appbar { Size = (uint)Marshal.SizeOf<Appbar>(), Param = new IntPtr(record.OriginalState) }; SHAppBarMessage(10, ref data); if (State() != record.OriginalState) throw new IOException("Windows taskbar state was not restored; recovery record retained."); }
        foreach (var window in record.Windows) { var h = new IntPtr(window.Hwnd); if (Verified(h, out int pid, out long created) && pid == window.Pid && created == window.Created) ShowWindowAsync(h, window.Visible ? 4 : 0); }
        Write(path, record with { Active = false });
    }
    private static string ProcessImage(Process process) { var text = new StringBuilder(32768); uint length = (uint)text.Capacity; if (!QueryFullProcessImageName(process.Handle, 0, text, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return text.ToString(); }
    private static bool Verified(IntPtr window, out int pid, out long created)
    {
        pid = 0; created = 0; var cls = new StringBuilder(128); GetClassName(window, cls, cls.Capacity); if (cls.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return false; GetWindowThreadProcessId(window, out uint owner); pid = (int)owner;
        try { using var process = Process.GetProcessById(pid); created = process.StartTime.ToFileTimeUtc(); return ProcessImage(process).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private static uint State() { var data = new Appbar { Size = (uint)Marshal.SizeOf<Appbar>() }; return (uint)SHAppBarMessage(4, ref data).ToUInt64(); }
    private delegate bool Enumerate(IntPtr h, IntPtr context);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Appbar { internal uint Size; internal IntPtr Window; internal uint Callback, Edge; internal Rect Rect; internal IntPtr Param; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Enumerate callback, IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder text, int count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref uint size);
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref Appbar data);
}
