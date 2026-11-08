using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MagicCommunity;

// A real Shell transaction has its own short-lived STA process. It must neither
// occupy the bar command reader nor be terminated when the desktop session ends.
internal sealed class RecycleBinCommand
{
    private Task? pending;
    internal Task Completion => pending ?? Task.CompletedTask;
    internal void Request(Func<Exception, Task> notice)
    {
        // Calls come from the single bar-command reader. Repeated clicks share
        // one operation, including its confirmation and progress windows.
        if (pending is not { IsCompleted: false }) pending = Observe(notice);
    }

    private static async Task Observe(Func<Exception, Task> notice)
    {
        try { await Execute(); }
        catch (Exception error) when (error is not OutOfMemoryException) { await notice(error); }
    }

    private static async Task Execute()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--empty-recycle-bin");
        using var child = Process.Start(start) ?? throw new IOException("回收站操作未启动。");
        var error = ReadError(child.StandardError);
        // Transfer this user gesture before allowing the worker to show UI.
        AllowSetForegroundWindow((uint)child.Id);
        await child.StandardInput.WriteLineAsync("confirm");
        child.StandardInput.Close();
        // No transaction timeout, parent-exit kill or session cancellation.
        await child.WaitForExitAsync();
        string detail = (await error).Trim();
        if (child.ExitCode != 0) throw new IOException(detail.Length is > 0 and <= 2048 ? detail : "Windows 未能完成清空回收站。");
    }

    private static async Task<string> ReadError(StreamReader stream)
    {
        var text = new StringBuilder(); var buffer = new char[512];
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
            if (text.Length < 2048) text.Append(buffer, 0, Math.Min(read, 2048 - text.Length));
        return text.ToString();
    }

    internal static int Run(Action empty, string mutexName)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (Console.ReadLine() != "confirm") return 2;
        // Also coalesce an operation still running after the desktop restarts.
        using var single = new Mutex(false, mutexName);
        bool owns;
        try { owns = single.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) return 0;
        try { empty(); return 0; }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine(error.Message); return 1; }
        finally { single.ReleaseMutex(); }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
