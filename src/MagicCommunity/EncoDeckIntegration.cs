using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MagicCommunity;

// App discovery is separate from optional card-IPC readiness. No idle process
// polling, registry scan, shell strings, arbitrary arguments or forced kills.
internal sealed class EncoDeckIntegration(string root, string? activationPipe = null)
{
    internal sealed record Location(string Status, string? Path, int ProcessId = 0, long Created = 0)
    {
        public bool Running => ProcessId > 0;
        public bool Installed => Path is not null;
    }
    private readonly string settingsPath = System.IO.Path.Combine(root, "data", "encodeck.json");
    private readonly SemaphoreSlim actions = new(1, 1);
    internal static string ValidateExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            !System.IO.Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.IndexOf(':', 2) >= 0 || !string.Equals(System.IO.Path.GetFileName(value), "EncoDeck.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择本机的 EncoDeck.exe 文件。");
        using var file = new FileStream(value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("无法核对 EncoDeck 的真实路径。");
        string path = buffer.ToString();
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EncoDeck 必须位于本机磁盘。");
        path = path[4..];
        if (!string.Equals(System.IO.Path.GetFileName(path), "EncoDeck.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "EncoDeck", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件不是可识别的 EncoDeck 程序。");
        return path;
    }
    internal string? ConfiguredPath()
    {
        if (!File.Exists(settingsPath)) return null;
        if (new FileInfo(settingsPath).Length > 8192) throw new InvalidDataException("EncoDeck 配置文件过大。");
        using var json = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { MaxDepth = 4 });
        if (json.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("EncoDeck 配置版本不受支持。");
        return ValidateExecutable(json.RootElement.GetProperty("executable").GetString() ?? "");
    }
    internal void Configure(string path)
    {
        path = ValidateExecutable(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // A failed replace preserves the prior user selection; no settings from
        // the external application are read or changed.
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = 1, executable = path }), new UTF8Encoding(false));
            File.Move(temporary, settingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal Location Locate()
    {
        string? selected;
        try { selected = ConfiguredPath(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or Win32Exception or FormatException or ArgumentException)
        { return new("invalid-path", null); }
        using var current = Process.GetCurrentProcess();
        Location? found = null;
        foreach (var process in Process.GetProcessesByName("EncoDeck"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != current.SessionId || process.HasExited) continue;
                    string path = ValidateExecutable(ImagePath(process.Id));
                    if (selected is not null && !string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found is not null) return new("multiple-instances", selected);
                    found = new("running-no-bridge", path, process.Id, process.StartTime.ToUniversalTime().Ticks);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            }
        }
        return found ?? new(selected is null ? "not-found" : "installed-not-running", selected);
    }
    internal JsonElement Unavailable()
    {
        var location = Locate();
        return JsonSerializer.SerializeToElement(new { available = false, connecting = false,
            programRunning = location.Running, programInstalled = location.Installed, providerStatus = location.Status });
    }
    internal static string ImagePath(int pid)
    {
        using var handle = OpenProcess(0x1000, false, pid);
        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    private static Process Verify(Location location)
    {
        var process = Process.GetProcessById(location.ProcessId);
        try
        {
            using var self = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != self.SessionId || process.StartTime.ToUniversalTime().Ticks != location.Created ||
                !string.Equals(ValidateExecutable(ImagePath(process.Id)), location.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("EncoDeck 实例已经变化，请重新操作。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    // EncoDeck's documented single-instance activation. Verify the *actual*
    // pipe server, including session and creation time, before sending a command.
    private async Task Signal(Location location, string action, CancellationToken cancellation)
    {
        using var owner = Verify(location);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认当前用户。");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)).AsSpan(0, 6));
        await using var pipe = new NamedPipeClientStream(".", activationPipe ?? "EncoDeck-" + hash, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(1500, cancellation).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != location.ProcessId)
            throw new IOException("EncoDeck 激活入口不属于当前实例。");
        using var rechecked = Verify(location);
        // Fixed allowlist, never accept a command line from the renderer/config.
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(action switch { "exit" => "--exit", "main" => "--page\nHome", "panel" => "--show-panel", _ => throw new InvalidDataException("不支持的 EncoDeck 操作。") }), cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }
    internal async Task<bool> ShowPanel(CancellationToken cancellation)
    {
        var location = Locate();
        if (!location.Running) return false;
        await Signal(location, "panel", cancellation).ConfigureAwait(false);
        return true;
    }
    internal static Process Recheck(Location location) => Verify(location);
    private static async Task<byte[]> Fingerprint(string path, CancellationToken cancellation)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
    }
    internal async Task<string> Perform(bool restart, CancellationToken cancellation)
    {
        if (!await actions.WaitAsync(0, cancellation).ConfigureAwait(false)) return "EncoDeck 正在处理上一次操作，请稍候。";
        try
        {
            var location = Locate();
            if (location.Status == "multiple-instances") return "检测到多个 EncoDeck 实例，请先指定安装位置。";
            if (location.Path is null) return "请先指定有效的 EncoDeck 安装位置。";
            string path = ValidateExecutable(location.Path);
            byte[] fingerprint = await Fingerprint(path, cancellation).ConfigureAwait(false);
            if (location.Running)
            {
                using var process = Verify(location);
                await Signal(location, restart ? "exit" : "main", cancellation).ConfigureAwait(false);
                if (!restart) return "已请求 EncoDeck 打开主界面。";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { return "EncoDeck 未在等待时间内正常退出，已取消重启；未强制结束程序。"; }
            }
            cancellation.ThrowIfCancellationRequested();
            var after = Locate();
            if (after.Running || after.Status == "multiple-instances") return "EncoDeck 实例已变化，请重新操作。";
            byte[] currentFingerprint = await Fingerprint(path, cancellation).ConfigureAwait(false);
            if (!string.Equals(ValidateExecutable(path), path, StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(fingerprint, currentFingerprint))
                return "EncoDeck 程序在操作期间发生变化，已取消启动。";
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! };
            start.ArgumentList.Add(restart ? "--background" : "--page");
            if (!restart) start.ArgumentList.Add("Home");
            using var launched = Process.Start(start) ?? throw new IOException("无法启动 EncoDeck。");
            return restart ? "已请求正常重启 EncoDeck，请稍后连接卡片。" : "已启动 EncoDeck。";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { return "EncoDeck 操作未完成：" + e.Message; }
        finally { actions.Release(); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
