using MagicCommunity;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Reflection;
internal static class TaskbarChecks
{
    internal static async Task Run(string guard,string directory,Dictionary<string,bool> checks)
    {
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(guard)));
        void Check(string name,bool pass){checks[name]=pass;if(!pass)throw new InvalidDataException(name);}
        async Task Visible(IntPtr window,bool value){var clock=Stopwatch.StartNew();while(IsWindowVisible(window)!=value&&clock.ElapsedMilliseconds<2000)await Task.Delay(10);if(IsWindowVisible(window)!=value)throw new TimeoutException("Owned guard fixture visibility differs");}
        Check("taskbar-restoration-preserves-user-settings",!NativeTaskbarRecovery.ShouldRestore(0,1,1,true)&&!NativeTaskbarRecovery.ShouldRestore(0,1,0,false)&&NativeTaskbarRecovery.ShouldRestore(0,1,1,false));
        Directory.CreateDirectory(directory);
        string journal=Path.Combine(directory,"owned-outcome-check.jsonl");
        File.WriteAllText(journal,"{\"userChanged\":false}\n");Check("taskbar-journal-unchanged-outcome-readable",!NativeTaskbarRecovery.UserChanged(journal));
        File.AppendAllText(journal,"{\"userChanged\":true}\n{incomplete");Check("taskbar-journal-preserves-user-change-before-partial-record",NativeTaskbarRecovery.UserChanged(journal));
        using(var large=new FileStream(journal,FileMode.Create,FileAccess.Write))large.SetLength(2*1024*1024);
        Check("taskbar-oversized-history-preserves-preferences-without-blocking",NativeTaskbarRecovery.UserChanged(journal)&&!NativeTaskbarRecovery.ShouldRestore(0,1,1,NativeTaskbarRecovery.UserChanged(journal)));
        var logsBefore=Directory.GetFiles(directory,"taskbar-*.jsonl").ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var fixture=new Fixture("MagicGuardFixture-");await Visible(fixture.Window,true);
        await using(var lease=await NativeTaskbarLease.Start(guard,hash,Environment.ProcessId,Environment.ProcessId,DateTimeOffset.UtcNow.AddSeconds(25),directory,fixtureWindow:fixture.Window))
        {
            await Visible(fixture.Window,false);Check("taskbar-guard-fixture-hidden-after-ready",true);
            ShowWindowAsync(fixture.Window,4);await Task.Delay(100);await Visible(fixture.Window,false);Check("taskbar-guard-rehides-owned-window-on-external-show",true);
            lease.RevealWindowsEntry();await Visible(fixture.Window,true);Check("taskbar-guard-explicit-native-entry-can-be-revealed",true);
            await Task.Delay(8300);await Visible(fixture.Window,false);
            string outcome=Directory.GetFiles(directory,"taskbar-*.jsonl").Single(path=>!logsBefore.Contains(path));
            long bytesBefore=new FileInfo(outcome).Length;
            using(var process=Process.GetProcessById(lease.ProcessId)){
                var cpu=process.TotalProcessorTime;await Task.Delay(300);process.Refresh();
                Check("taskbar-reveal-timeout-does-not-spin-or-flood-log",lease.IsAlive&&new FileInfo(outcome).Length-bytesBefore<4096&&process.TotalProcessorTime-cpu<TimeSpan.FromMilliseconds(150));
            }
            using(var log=new StreamReader(new FileStream(outcome,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)))
                Check("taskbar-reveal-timeout-hidden-once",log.ReadToEnd().Split('\n').Count(line=>line.Contains("\"phase\":\"hidden\""))==1);
            lease.RevealWindowsEntry();await Visible(fixture.Window,true);Check("taskbar-reveal-can-rearm-after-timeout",true);
            lease.HideWindowsEntry();await Visible(fixture.Window,false);
        }
        await Visible(fixture.Window,true);Check("taskbar-guard-normal-stop-restores-fixture",true);
        using(var renderer=StartOwnedWaiter())
        {
            try { await using(var lease=await NativeTaskbarLease.Start(guard,hash,renderer.Id,Environment.ProcessId,DateTimeOffset.UtcNow.AddSeconds(10),directory,fixtureWindow:fixture.Window)){await Visible(fixture.Window,false);renderer.Kill();await renderer.WaitForExitAsync();await Visible(fixture.Window,true);Check("taskbar-guard-renderer-exit-restores-fixture",true);} }
            finally {if(!renderer.HasExited){renderer.Kill();await renderer.WaitForExitAsync();}}
        }
        using(var backend=StartOwnedWaiter())
        {
            try { await using(var lease=await NativeTaskbarLease.Start(guard,hash,Environment.ProcessId,backend.Id,DateTimeOffset.UtcNow.AddSeconds(10),directory,fixtureWindow:fixture.Window)){await Visible(fixture.Window,false);backend.Kill();await backend.WaitForExitAsync();await Visible(fixture.Window,true);Check("taskbar-guard-backend-exit-restores-fixture",true);} }
            finally {if(!backend.HasExited){backend.Kill();await backend.WaitForExitAsync();}}
        }
        bool nullRejected=false;try{await using var invalid=await NativeTaskbarLease.Start(guard,hash,Environment.ProcessId,Environment.ProcessId,null,directory,fixtureWindow:fixture.Window);}catch(InvalidDataException){nullRejected=true;}Check("taskbar-no-deadline-requires-explicit-continuous-mode",nullRejected);
        int interrupted=0;
        await using(var lease=await NativeTaskbarLease.Start(guard,hash,Environment.ProcessId,Environment.ProcessId,null,directory,()=>Interlocked.Exchange(ref interrupted,1),fixture.Window,continuous:true))
        {
            await Visible(fixture.Window,false);await Task.Delay(2300);Check("taskbar-continuous-has-no-deadline-exit",lease.IsAlive&&Volatile.Read(ref interrupted)==0&&!IsWindowVisible(fixture.Window));
        }
        await Visible(fixture.Window,true);Check("taskbar-continuous-normal-stop-restores",true);
        using(var renderer=StartOwnedWaiter())
        {
            try{await using var lease=await NativeTaskbarLease.Start(guard,hash,renderer.Id,Environment.ProcessId,null,directory,fixtureWindow:fixture.Window,continuous:true);await Visible(fixture.Window,false);renderer.Kill();await renderer.WaitForExitAsync();await Visible(fixture.Window,true);Check("taskbar-continuous-renderer-death-restores",true);}
            finally{if(!renderer.HasExited){renderer.Kill();await renderer.WaitForExitAsync();}}
        }
        int expired=0;await using(var lease=await NativeTaskbarLease.Start(guard,hash,Environment.ProcessId,Environment.ProcessId,DateTimeOffset.UtcNow.AddSeconds(2),directory,()=>Interlocked.Exchange(ref expired,1),fixture.Window)){await Visible(fixture.Window,false);await Task.Delay(2300);await Visible(fixture.Window,true);Check("taskbar-guard-acceptance-deadline-restores-and-signals",Volatile.Read(ref expired)==1);}
    }
    private static Process StartOwnedWaiter(){var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--guard-owned-wait");return Process.Start(start)!;}
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(IntPtr h,int mode);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
}
