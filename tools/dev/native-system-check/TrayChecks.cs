using MagicCommunity;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class TrayChecks
{
    internal static async Task Run(NativeSystemClient backend, Dictionary<string,bool> checks, bool live = false)
    {
        void Check(string key,bool pass){checks[key]=pass;if(!pass)throw new InvalidDataException(key);}
        using var fixture=new TrayFixture(live); string? stagedCapture = null;
        if(live)await backend.Request("tray-subscribe",new{enabled=true,production=true,initializeExisting=false});
        else await backend.Request("tray-subscribe",new{enabled=true,fixtureWindow=fixture.Window.ToInt64().ToString()});
        try
        {
            fixture.Publish(0,"Owned tray fixture");
            async Task<JsonElement> Read(Func<JsonElement,bool> condition){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<2500){var data=await backend.Request("tray-read",new{});if(condition(data))return data;await Task.Delay(10);}throw new TimeoutException("Owned tray fixture data missing");}
            var first=await Read(d=>d.GetProperty("icons").GetArrayLength()==1);var icon=first.GetProperty("icons")[0];string id=icon.GetProperty("id").GetString()!;
            Check("tray-owned-icon-add-captured",icon.GetProperty("tooltip").GetString()=="Owned tray fixture");
            Check("owned-tray-real-icon-encoded-as-png",Convert.FromBase64String(icon.GetProperty("png").GetString()!).AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}));
            if (!live)
            {
                stagedCapture = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(x => x.ModuleName == "MagicTrayCapture.dll").FileName;
                using var ownedProcess = Process.GetProcessById(backend.ProcessId);
                string source = Path.Combine(Path.GetDirectoryName(ownedProcess.MainModule!.FileName)!, "MagicTrayCapture.dll");
                Check("tray-capture-stages-exact-owned-dll-outside-package", !StringComparer.OrdinalIgnoreCase.Equals(stagedCapture, source) && File.ReadAllBytes(stagedCapture).SequenceEqual(File.ReadAllBytes(source)));
                bool locked = false; try { using var write = File.Open(stagedCapture, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { locked = true; }
                Check("staged-tray-dll-cannot-be-replaced-while-hook-active", locked);
            }
            await backend.Request("tray-action",new{iconId=id,action="activate",x=10,y=20});await fixture.WaitFor(0x202);Check("legacy-tray-left-click-delivers-down-up",fixture.PreviousEvent==0x201&&fixture.LastId==77);
            await backend.Request("tray-action",new{iconId=id,action="context",x=10,y=20});await fixture.WaitFor(0x205);Check("legacy-tray-right-click-delivers-down-up",fixture.PreviousEvent==0x204&&fixture.LastId==77);
            fixture.Publish(4,"Owned tray fixture",4);fixture.Publish(1,"Owned tray updated");await Read(d=>d.GetProperty("icons")[0].GetProperty("tooltip").GetString()=="Owned tray updated");Check("owned-tray-modification-preserves-identity",(await backend.Request("tray-read",new{})).GetProperty("icons")[0].GetProperty("id").GetString()==id);
            await backend.Request("tray-action",new{iconId=id,action="activate",x=120,y=240});await fixture.WaitFor(0x400);Check("owned-tray-v4-select-callback-delivered",fixture.LastId==77&&fixture.LastX==120&&fixture.LastY==240);
            await backend.Request("tray-action",new{iconId=id,action="context",x=-120,y=240});await fixture.WaitFor(0x7b);Check("owned-tray-v4-context-callback-delivered",fixture.LastId==77&&fixture.LastX==-120);
            if(!live){int before=(int)GetGuiResources(Process.GetCurrentProcess().Handle,0);for(int n=0;n<64;n++)fixture.Publish(1,"Owned tray updated");int after=(int)GetGuiResources(Process.GetCurrentProcess().Handle,0);Check("tray-icon-updates-release-gdi-resources",after-before<=1);}
            fixture.Publish(2,"Owned tray updated");await Read(d=>d.GetProperty("icons").GetArrayLength()==0);bool stale=false;try{await backend.Request("tray-action",new{iconId=id,action="activate"});}catch(NativeSystemRequestException){stale=true;}Check("deleted-tray-icon-action-rejected",stale);
            if(!live){fixture.Publish32(0,"Owned x86 V3 fixture");var wow=await Read(d=>d.GetProperty("icons").GetArrayLength()==1);Check("x86-v3-and-x64-v2-overlap-resolved-by-owner",wow.GetProperty("icons")[0].GetProperty("tooltip").GetString()=="Owned x86 V3 fixture");fixture.Publish32(2,"",true);await Read(d=>d.GetProperty("icons").GetArrayLength()==0);Check("guid-only-x86-deletion-removes-original-icon",true);}
        }
        finally{await backend.Request("tray-subscribe",new{enabled=false});}
        var stopped=await backend.Request("health",new{});Check("tray-unsubscribe-removes-hooks-and-cache",stopped.GetProperty("tray").GetProperty("nativeHooks").GetInt32()==0&&stopped.GetProperty("tray").GetProperty("cachedIcons").GetInt32()==0);
        if(!live)await LateQtChecks(backend,checks);
        if (stagedCapture is not null)
        {
            await backend.Request("tray-subscribe", new { enabled = true, fixtureWindow = fixture.Window.ToInt64().ToString() });
            try
            {
                fixture.Publish(0, "Owned cache reuse fixture"); var wait = Stopwatch.StartNew(); bool restored = false;
                while (wait.ElapsedMilliseconds < 1500) { var data = await backend.Request("tray-read", new { }); if (data.GetProperty("icons").GetArrayLength() == 1) { restored = true; break; } await Task.Delay(10); }
                Check("tray-capture-restarts-with-same-verified-asset", restored && Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(x => x.ModuleName == "MagicTrayCapture.dll").FileName == stagedCapture);
            }
            finally { fixture.Publish(2, "Owned cache reuse fixture"); await backend.Request("tray-subscribe", new { enabled = false }); }
        }
    }
    private static async Task LateQtChecks(NativeSystemClient backend,Dictionary<string,bool> checks)
    {
        void Check(string key,bool pass){checks[key]=pass;if(!pass)throw new InvalidDataException(key);}
        foreach(bool qt in new[]{false,true})
        {
            using var fixture=new TrayFixture(false,qt?"Qt51514MagicOwnedTrayIconMessageWindowClass":null);
            fixture.PublishedFlags|=128;
            fixture.ExpectVersion(qt?4u:0u);
            await backend.Request("tray-subscribe",new{enabled=true,fixtureWindow=fixture.Window.ToInt64().ToString()});
            try
            {
                async Task<JsonElement> ReadVersion(int version){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<2000){var data=await backend.Request("tray-read",new{});if(data.GetProperty("icons").GetArrayLength()==1&&data.GetProperty("icons")[0].GetProperty("callbackVersion").GetInt32()==version)return data.GetProperty("icons")[0].Clone();await Task.Delay(10);}throw new TimeoutException("Late tray protocol missing");}
                fixture.Publish(1,"Owned late registration",4);
                var icon=await ReadVersion(qt?4:-1);string id=icon.GetProperty("id").GetString()!;
                Check(qt?"late-qt-update-recovers-v4-without-setversion":"non-qt-union-is-not-used-as-protocol",true);
                foreach(var (action,eventId) in new[]{("activate",qt?0x400:0x202),("context",qt?0x7b:0x205),("double-click",qt?0x400:0x202)})
                {
                    fixture.ResetCallbacks();await backend.Request("tray-action",new{iconId=id,action,x=-120,y=64});await fixture.WaitFor(eventId);
                    Check($"late-{(qt?"qt":"generic")}-{action}-callback",fixture.LastId==77&&(!qt||fixture.LastX==-120&&fixture.LastY==64));
                }
                fixture.Publish(0,"Owned explicit add",4);fixture.ExpectVersion(0);fixture.Publish(1,"Owned explicit add",4);await ReadVersion(0);
                Check($"late-{qt}-successful-add-resets-to-legacy-until-setversion",true);
                // Explicit legacy registration/version always takes priority over
                // the narrow hint; a balloon timeout of 4 is never a version.
                fixture.Publish(4,"Owned explicit legacy",0);fixture.ExpectVersion(0);fixture.Publish(1,"Owned explicit legacy",4);await ReadVersion(0);
                fixture.ResetCallbacks();await backend.Request("tray-action",new{iconId=id,action="context"});await fixture.WaitFor(0x205);Check($"late-{qt}-explicit-legacy-wins",fixture.LastId==77);
                fixture.Publish(2,"Removed");fixture.PublishedFlags|=16;fixture.Publish(1,"Owned balloon timeout",4);await ReadVersion(-1);Check($"late-{qt}-balloon-timeout-not-version",true);
            }
            finally{await backend.Request("tray-subscribe",new{enabled=false});}
        }
    }
    [DllImport("user32.dll")]private static extern uint GetGuiResources(IntPtr process,uint kind);
    private sealed class TrayFixture : IDisposable
    {
        private readonly bool live;private readonly Thread thread;private readonly ManualResetEventSlim ready=new();private readonly Procedure callback;private readonly string cls="MagicTrayFixture-"+Guid.NewGuid().ToString("N");private readonly Guid icon=Guid.NewGuid();private IntPtr window;
        internal IntPtr Window=>window;private uint callbackVersion; internal volatile int LastEvent,PreviousEvent,LastId,LastX,LastY;
        internal uint PublishedFlags=1|2|4|32;
        internal void ExpectVersion(uint version)=>callbackVersion=version;
        internal void ResetCallbacks(){LastEvent=PreviousEvent=0;}
        internal TrayFixture(bool live,string? className=null){this.live=live;if(className is not null)cls=className;callback=WndProc;thread=new Thread(Pump){IsBackground=true};thread.Start();if(!ready.Wait(3000)||window==IntPtr.Zero)throw new InvalidOperationException("Tray fixture failed");}
        private void Pump(){var wc=new WindowClass{proc=callback,name=cls,instance=GetModuleHandle(null)};RegisterClass(ref wc);window=CreateWindowEx(0,cls,"Magic owned tray protocol fixture",0,0,0,0,0,IntPtr.Zero,IntPtr.Zero,wc.instance,IntPtr.Zero);ready.Set();while(GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}UnregisterClass(cls,wc.instance);}
        private IntPtr WndProc(IntPtr h,uint message,IntPtr w,IntPtr l){if(message==0x4a)return new IntPtr(1);if(message==0x8000+42){PreviousEvent=LastEvent;LastId=callbackVersion>=4?(int)((l.ToInt64()>>16)&0xffff):(int)w;LastX=(short)(w.ToInt64()&0xffff);LastY=(short)((w.ToInt64()>>16)&0xffff);LastEvent=callbackVersion>=4?(int)(l.ToInt64()&0xffff):(int)l;return IntPtr.Zero;}if(message==0x10){DestroyWindow(h);return IntPtr.Zero;}if(message==2){PostQuitMessage(0);return IntPtr.Zero;}return DefWindowProc(h,message,w,l);}
        internal void Publish(uint operation,string label,uint version=0){if(operation==4)callbackVersion=version;var data=new NotifyIcon{size=(uint)Marshal.SizeOf<NotifyIcon>(),window=window,id=77,flags=PublishedFlags,message=0x8000+42,icon=LoadIcon(IntPtr.Zero,new IntPtr(32516)),tip=label,info="",title="",guid=icon,version=version};if(live){if(!ShellNotifyIcon(operation,ref data))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return;}int n=Marshal.SizeOf<NotifyIcon>();IntPtr payload=Marshal.AllocHGlobal(n+8),header=Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());try{Marshal.WriteInt32(payload,unchecked((int)0x34753423));Marshal.WriteInt32(payload,4,(int)operation);Marshal.StructureToPtr(data,payload+8,false);var copy=new CopyData{kind=new UIntPtr(1),size=n+8,data=payload};Marshal.StructureToPtr(copy,header,false);if(SendMessageTimeout(window,0x4a,window,header,2,2000,out _)==IntPtr.Zero)throw new InvalidOperationException("Tray fixture publish failed");}finally{Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(payload);}}
        internal void Publish32(uint operation,string label,bool guidOnly=false){var data=new Notify32{size=952,window=guidOnly?0:unchecked((uint)window.ToInt64()),id=77,flags=guidOnly?32u:1u|2|4|32,message=0x8000+42,icon=unchecked((uint)LoadIcon(IntPtr.Zero,new IntPtr(32516)).ToInt64()),tip=label,info="",title="",guid=icon};IntPtr payload=Marshal.AllocHGlobal(960),header=Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());try{Marshal.WriteInt32(payload,unchecked((int)0x34753423));Marshal.WriteInt32(payload,4,(int)operation);Marshal.StructureToPtr(data,payload+8,false);Marshal.StructureToPtr(new CopyData{kind=new UIntPtr(1),size=960,data=payload},header,false);if(SendMessageTimeout(window,0x4a,window,header,2,2000,out _)==IntPtr.Zero)throw new InvalidOperationException("x86 tray fixture failed");}finally{Marshal.FreeHGlobal(header);Marshal.FreeHGlobal(payload);}}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode,Pack=4)]private struct Notify32{public uint size,window,id,flags,message,icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string tip;public uint state,stateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string info;public uint version;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string title;public uint infoFlags;public Guid guid;}
        internal async Task WaitFor(int type){var clock=Stopwatch.StartNew();while(LastEvent!=type&&clock.ElapsedMilliseconds<1500)await Task.Delay(5);if(LastEvent!=type)throw new TimeoutException("Tray callback missing");}
        public void Dispose(){if(live&&window!=IntPtr.Zero)try{Publish(2,"");}catch{}if(window!=IntPtr.Zero)PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);thread.Join(2000);ready.Dispose();}
        private delegate IntPtr Procedure(IntPtr h,uint m,IntPtr w,IntPtr l);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint style;public Procedure proc;public int cbCls,cbWnd;public IntPtr instance,icon,cursor,background;public string? menu,name;}
        [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr window;public uint message;public IntPtr w,l;public uint time;public int x,y;public uint reserved;}
        [StructLayout(LayoutKind.Sequential)]private struct CopyData{public UIntPtr kind;public int size;public IntPtr data;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyIcon{public uint size;public IntPtr window;public uint id,flags,message;public IntPtr icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string tip;public uint state,stateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string info;public uint version;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string title;public uint infoFlags;public Guid guid;public IntPtr balloon;}
        [DllImport("shell32",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ShellNotifyIcon(uint operation,ref NotifyIcon icon);
        [DllImport("kernel32",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? module);
        [DllImport("user32",CharSet=CharSet.Unicode)]private static extern ushort RegisterClass(ref WindowClass value);
        [DllImport("user32",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string name,IntPtr instance);
        [DllImport("user32",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32")]private static extern int GetMessage(out Message value,IntPtr h,uint min,uint max);
        [DllImport("user32")]private static extern bool TranslateMessage(ref Message value);
        [DllImport("user32")]private static extern IntPtr DispatchMessage(ref Message value);
        [DllImport("user32",CharSet=CharSet.Unicode)]private static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32")]private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32")]private static extern void PostQuitMessage(int code);
        [DllImport("user32")]private static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32")]private static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
        [DllImport("user32",CharSet=CharSet.Unicode)]private static extern IntPtr LoadIcon(IntPtr module,IntPtr name);
    }
}
