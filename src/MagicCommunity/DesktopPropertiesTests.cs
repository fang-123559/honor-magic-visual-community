using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopPropertiesTests
{
    public static object Run(string root)
    {
        string directory=Path.Combine(root,"evidence/desktop-feedback/2026-10-17","properties-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"magic-properties-"+Guid.NewGuid().ToString("N")+".txt"),folder=Path.Combine(directory,"magic-properties-folder-"+Guid.NewGuid().ToString("N"));File.WriteAllText(file,"Property sheet lifecycle fixture");Directory.CreateDirectory(folder);
        var checks=new List<object>();
        foreach(string target in new[]{file,folder,"::{645FF040-5081-101B-9F08-00AA002F954E}"})
        {
            using var host=new DesktopMenuHost(root);using var input=JsonDocument.Parse(JsonSerializer.Serialize(new{requestId=Guid.NewGuid().ToString("N")}));
            string label=target.StartsWith("::",StringComparison.Ordinal)?"回收站":Path.GetFileName(target);var watch=Stopwatch.StartNew();uint worker=0;bool stable=false;IntPtr sheet=IntPtr.Zero;double firstVisible=0;
            var command=host.Route("/desktop-properties",input.RootElement,CancellationToken.None,target);
            var observer=Task.Run(()=>{
                while(watch.ElapsedMilliseconds<12000)
                {
                    EnumWindows((hwnd,_)=>{
                        var title=new StringBuilder(512);GetWindowText(hwnd,title,title.Capacity);
                        if(!title.ToString().Contains(label,StringComparison.Ordinal)||!IsWindowVisible(hwnd))return true;
                        GetWindowThreadProcessId(hwnd,out uint pid);
                        if(!host.IsInvokingProcess(pid))return true;
                        using var process=Process.GetProcessById((int)pid);
                        if(process.MainModule?.FileName!=Environment.ProcessPath)return true;
                        sheet=hwnd;worker=pid;if(firstVisible==0)firstVisible=watch.Elapsed.TotalMilliseconds;
                        if(watch.Elapsed.TotalMilliseconds-firstVisible>=1200){stable=!command.IsCompleted;PostMessage(hwnd,0x10,IntPtr.Zero,IntPtr.Zero);}
                        return true;
                    },IntPtr.Zero);
                    if(stable)return;Thread.Sleep(40);
                }
                host.Suspend();
            });
            command.GetAwaiter().GetResult();observer.GetAwaiter().GetResult();
            bool exited;try{using var process=Process.GetProcessById((int)worker);exited=process.HasExited;}catch(ArgumentException){exited=true;}
            bool pass=sheet!=IntPtr.Zero&&stable&&exited;
            checks.Add(new{name=(target.StartsWith("::",StringComparison.Ordinal)?"Recycle Bin":Directory.Exists(target)?"folder":"file")+" property sheet visible and stable until closed; worker reclaimed",pass,firstVisibleMs=firstVisible,completedMs=watch.Elapsed.TotalMilliseconds});
            if(!pass)throw new InvalidDataException("Property window lifecycle test failed.");
        }
        return new{allPass=true,checks,scope="Actual Windows file/folder/Recycle Bin property sheets, closed by handle after command-worker ownership validation; no settings changes, clipboard writes or human mouse/dual-screen acceptance"};
    }
    private delegate bool Enumerate(IntPtr hwnd,IntPtr value);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Enumerate callback,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int size);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
}
