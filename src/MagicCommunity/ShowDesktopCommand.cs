using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MagicCommunity;

// A temporarily unresponsive Explorer must not occupy the bar's input loop.
// Only one explicit toggle is in flight; no automatic action retry.
internal sealed class ShowDesktopCommand
{
    private Task? pending;
    internal void Request(Func<Exception,Task> notice)
    {
        if(pending is not {IsCompleted:false})pending=Observe(notice);
    }
    private static async Task Observe(Func<Exception,Task> notice)
    {
        try { await Execute(); }
        catch(Exception error)when(error is not OutOfMemoryException){await notice(error);}
    }
    private static async Task Execute()
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true,StandardErrorEncoding=Encoding.UTF8};
        if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--show-desktop");
        using var child=Process.Start(start)??throw new IOException("显示桌面操作未启动。");
        var error=child.StandardError.ReadToEndAsync();
        AllowSetForegroundWindow((uint)child.Id);
        await child.StandardInput.WriteLineAsync("confirm");child.StandardInput.Close();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try{await child.WaitForExitAsync(deadline.Token);}
        catch(OperationCanceledException){
            // Owned one-shot helper only; never Explorer, a game or renderer.
            try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException)when(child.HasExited){}
            await child.WaitForExitAsync();
            throw new IOException("Windows 显示桌面操作超时，其他面板仍可使用；该操作不会自动重试。");
        }
        string detail=(await error).Trim();
        if(child.ExitCode!=0)throw new IOException(detail.Length is >0 and <=2048?detail:"Windows 未完成显示桌面操作。");
    }
    internal static int Run()=>RecycleBinCommand.Run(Toggle,@"Local\MagicCommunity.ShowDesktop");
    private static void Toggle(){
        object? shell=null;
        try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);((dynamic)shell!).ToggleDesktop();}
        finally{if(shell is not null&&Marshal.IsComObject(shell))Marshal.ReleaseComObject(shell);}
    }
    [DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(uint pid);
}
