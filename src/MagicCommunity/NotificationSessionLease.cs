using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicCommunity;

// Acquired only after both notification access and the authenticated renderer
// are ready. The native guard owns restoration even if this adapter is killed.
internal sealed class NotificationSessionLease : IDisposable
{
    private const string Quiet = "Microsoft.QuietHoursProfile.AlarmsOnly";
    private readonly string journal;
    private Process? guard;
    private EventWaitHandle? restore;
    private bool attempted, disposed, recovered;
    internal string Status { get; private set; } = "";
    private sealed record LeaseRecord(int Pid, long Created, string Original, bool Active);

    internal NotificationSessionLease(string root) =>
        journal = Path.Combine(root, "runtime/desktop/notification-session-lease.json");

    internal void Update(bool access, int rendererPid)
    {
        if (disposed) return;
        if (!recovered)
        {
            try { RecoverStaleLease(); recovered = true; }
            catch (Exception error) { Status = "上次通知设置尚未恢复：" + error.Message; return; }
        }
        if (guard is { HasExited: true })
        {
            Release();
            Status = "通知恢复守护已结束；本次会话不再自动切换勿扰。";
        }
        if (!access)
        {
            Release();
            Status = "通知监听未获授权，Windows 通知横幅保持原设置。";
            return;
        }
        if (attempted || rendererPid == 0) return;
        attempted = true;
        try
        {
            string original = Profile();
            if (original is not ("Microsoft.QuietHoursProfile.Unrestricted" or "Microsoft.QuietHoursProfile.PriorityOnly" or Quiet))
                throw new InvalidDataException("Windows 返回了未知的勿扰模式。");
            if (original == Quiet)
            {
                Status = "Windows 勿扰已开启；退出社区桌面时保留原设置。";
                return;
            }
            using var adapter = Process.GetCurrentProcess();
            using var renderer = Process.GetProcessById(rendererPid);
            var record = new LeaseRecord(adapter.Id, adapter.StartTime.ToFileTimeUtc(), original, true);
            StateStore.AtomicWrite(journal, JsonSerializer.Serialize(record), null);
            string id = "Local\\MagicNotifications-" + Guid.NewGuid().ToString("N");
            restore = new(false, EventResetMode.ManualReset, id + "-restore");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, id + "-ready");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MagicDesktopGuard.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--notifications", adapter.Id.ToString(),
                record.Created.ToString(), renderer.Id.ToString(), renderer.StartTime.ToFileTimeUtc().ToString(),
                id + "-restore", id + "-ready", original }) start.ArgumentList.Add(argument);
            guard = Process.Start(start) ?? throw new IOException("通知恢复守护未能启动。");
            using var ended = new ProcessWaitHandle(guard);
            int result = WaitHandle.WaitAny(new WaitHandle[] { ready, ended }, 2000);
            if (result != 0) throw new IOException("Windows 未接受临时勿扰会话。");
            Status = "Windows 通知横幅已临时静音，退出社区桌面后恢复。";
        }
        catch (Exception error)
        {
            Release();
            Status = "临时勿扰未启用：" + error.Message;
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(Process process) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, false);
    }
    private void RecoverStaleLease()
    {
        if (!File.Exists(journal)) return;
        if (new FileInfo(journal).Length > 4096) throw new InvalidDataException("通知恢复记录超出限制。");
        var old = JsonSerializer.Deserialize<LeaseRecord>(File.ReadAllText(journal));
        if (old is not { Active: true }) return;
        bool alive;
        try { using var owner = Process.GetProcessById(old.Pid); alive = !owner.HasExited && owner.StartTime.ToFileTimeUtc() == old.Created; }
        catch (ArgumentException) { alive = false; }
        if (alive) throw new IOException("另一社区桌面会话仍持有通知恢复记录。");
        if (Profile() == Quiet) Profile(old.Original);
        StateStore.AtomicWrite(journal, JsonSerializer.Serialize(old with { Active = false }), null);
    }
    private void Release()
    {
        if (guard is null) { restore?.Dispose(); restore = null; return; }
        restore?.Set();
        if (guard.WaitForExit(2000) && guard.ExitCode == 0)
        {
            try
            {
                var old = JsonSerializer.Deserialize<LeaseRecord>(File.ReadAllText(journal));
                if (old is not null) StateStore.AtomicWrite(journal, JsonSerializer.Serialize(old with { Active = false }), null);
            }
            catch (Exception error) { Debug.WriteLine(error); }
        }
        guard.Dispose(); guard = null; restore?.Dispose(); restore = null;
    }
    private static string Profile(string? set = null)
    {
        if (set is not null && set is not ("Microsoft.QuietHoursProfile.Unrestricted" or
            "Microsoft.QuietHoursProfile.PriorityOnly" or Quiet)) throw new InvalidDataException("未知的通知恢复模式。");
        var type = Type.GetTypeFromCLSID(new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf"), true)!;
        object instance = Activator.CreateInstance(type)!;
        try
        {
            var settings = (IQuietHoursSettings)instance;
            if (set is not null) Marshal.ThrowExceptionForHR(settings.Set(set));
            Marshal.ThrowExceptionForHR(settings.Get(out var value));
            try { return Marshal.PtrToStringUni(value) ?? throw new IOException("Windows 通知模式为空。"); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig] int Get(out IntPtr value);
        [PreserveSig] int Set([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    public void Dispose() { if (disposed) return; disposed = true; Release(); }
}
