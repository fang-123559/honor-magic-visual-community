using System.Text.Json;
using System.Runtime.InteropServices;
using MagicCommunity;
internal static class NotificationWindowChecks
{
    internal static void Run(Dictionary<string,bool> checks)
    {
        void Check(string name,bool value){checks["notification-window-"+name]=value;if(!value)throw new InvalidOperationException(name);}
        JsonElement Snapshot(params object[] windows)=>JsonSerializer.SerializeToElement(new{windows});
        object Window(string id,string aumid="Example!App",string path="C:/Apps/Example.exe",bool focused=false,bool minimized=false)=>new{id,hwnd=id,pid=1,aumid,executable=path,focused,minimized};
        var owner=new NativeNotificationWindows();
        var two=Snapshot(Window("1",focused:true),Window("2",minimized:true));owner.Observe(two);
        var hidden=Snapshot(Window("1",minimized:true),Window("2"));
        Check("last-activated-account-wins-even-when-minimized",owner.Find("Example!App",hidden,[]).GetProperty("id").GetString()=="1");
        owner.Observe(Snapshot(Window("1"),Window("2",focused:true)));
        Check("focus-event-changes-selected-instance",owner.Find("Example!App",hidden,[]).GetProperty("id").GetString()=="2");
        Check("current-focus-has-priority",owner.Find("Example!App",two,[]).GetProperty("id").GetString()=="1");
        owner.Observe(Snapshot());owner.Observe(hidden);
        Check("destroyed-identity-drops-recency",owner.Find("Example!App",hidden,[]).GetProperty("id").GetString()=="2");
        Check("exact-process-name-without-aumid",owner.Find("Example.exe",Snapshot(Window("3",aumid:"")),[]).GetProperty("id").GetString()=="3");
        Check("resolved-config-executable-path",owner.Find("Other.Id",Snapshot(Window("3",aumid:"")),["c:\\apps\\EXAMPLE.exe"]).GetProperty("id").GetString()=="3");
        Check("window-property-identity",owner.Find("Window.Id",Snapshot(Window("3",aumid:"")),[],_=>"Window.Id").GetProperty("id").GetString()=="3");
        Check("title-or-prefix-is-not-an-application-identity",owner.Find("Example.Other",hidden,[]).ValueKind!=JsonValueKind.Object);
        int activated=0,launched=0;
        owner.Open("Example!App",hidden,[],_=>{activated++;return Task.CompletedTask;},_=>launched++).GetAwaiter().GetResult();
        Check("existing-window-does-not-launch",activated==1&&launched==0);
        bool failed=false;try{owner.Open("Example!App",hidden,[],_=>Task.FromException(new InvalidOperationException("Owned activation refused")),_=>launched++).GetAwaiter().GetResult();}catch(InvalidOperationException){failed=true;}
        Check("failed-activation-never-falls-through-to-relaunch",failed&&launched==0);
        owner.Open("No.Instances",hidden,[],_=>{activated++;return Task.CompletedTask;},_=>launched++).GetAwaiter().GetResult();
        Check("no-existing-instance-uses-registered-launch-once",launched==1&&activated==1);
        int resident=0;
        owner.Open("QQ",Snapshot(),[],_=>throw new Exception("Unexpected window"),_=>launched++,activateResidentQq:()=>{resident++;return Task.FromResult(true);}).GetAwaiter().GetResult();
        Check("qq-tray-only-session-opens-without-login-launch",resident==1&&launched==1);
        failed=false;try{owner.Open("QQ",Snapshot(),[],_=>Task.CompletedTask,_=>launched++,activateResidentQq:()=>Task.FromException<bool>(new InvalidOperationException())).GetAwaiter().GetResult();}catch(InvalidOperationException){failed=true;}
        Check("qq-indeterminate-resident-never-relaunches",failed&&launched==1);
        owner.Open("QQ",Snapshot(),[],_=>Task.CompletedTask,_=>launched++,activateResidentQq:()=>Task.FromResult(false)).GetAwaiter().GetResult();
        Check("qq-cold-start-launches-once",launched==2);
        owner.Open("Microsoft.YourPhone!YourPhoneNotifications_com.tencent.mobileqq",Snapshot(),[],_=>Task.CompletedTask,_=>launched++,activateResidentQq:()=>throw new Exception("Wrong application")).GetAwaiter().GetResult();
        Check("phone-link-qq-keeps-original-identity",launched==3);
        string qq=@"C:\Owned\QQ.exe",elsewhere=@"D:\Owned\QQ.exe";
        JsonElement Tray(params (string id,string executable)[] icons)=>JsonSerializer.SerializeToElement(new{icons=icons.Select(i=>new{i.id,i.executable})});
        Check("qq-exact-resident-icon",NativeNotificationWindows.QqTrayIcon(Tray(("music",@"C:\Owned\QQMusic.exe"),("qq",qq)),[qq])=="qq");
        Check("qq-tray-other-installation-excluded",NativeNotificationWindows.QqTrayIcon(Tray(("qq",elsewhere)),[qq])==null);
        Check("qq-tray-ambiguous-accounts",NativeNotificationWindows.QqTrayIcon(Tray(("one",qq),("two",qq)),[qq])==null);
        Check("malformed-hwnd-is-not-queried",NativeNotificationWindows.WindowAppId(JsonSerializer.SerializeToElement(new{hwnd="invalid",pid=1}))=="");
        var hwnd=CreateWindowExW(0,"STATIC","Magic owned notification identity fixture",unchecked((int)0x80000000),-30000,-30000,80,40,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(hwnd==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        IPropertyStore? store=null;Value value=default;
        try
        {
            var iid=typeof(IPropertyStore).GUID;Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd,ref iid,out store));
            var key=new Key(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),5);value.Type=31;value.Pointer=Marshal.StringToCoTaskMemUni("Owned.Notification!App");
            Marshal.ThrowExceptionForHR(store.SetValue(ref key,ref value));Marshal.ThrowExceptionForHR(store.Commit());
            var own=JsonSerializer.SerializeToElement(new{hwnd=hwnd.ToInt64().ToString(),pid=Environment.ProcessId});
            Check("real-window-property-store-identity",NativeNotificationWindows.WindowAppId(own)=="Owned.Notification!App");
            Check("recycled-or-wrong-pid-is-rejected",NativeNotificationWindows.WindowAppId(JsonSerializer.SerializeToElement(new{hwnd=hwnd.ToInt64().ToString(),pid=Environment.ProcessId+1}))=="");
        }
        finally{PropVariantClear(ref value);if(store is not null)Marshal.ReleaseComObject(store);DestroyWindow(hwnd);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Key(Guid format,uint id){public Guid Format=format;public uint Id=id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct Value{[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Pointer;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IPropertyStore
    {
        [PreserveSig]int GetCount(out uint count);[PreserveSig]int GetAt(uint index,out Key key);[PreserveSig]int GetValue(ref Key key,out Value value);
        [PreserveSig]int SetValue(ref Key key,ref Value value);[PreserveSig]int Commit();
    }
    [DllImport("shell32.dll")]private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd,ref Guid iid,out IPropertyStore store);
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref Value value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowExW(uint ex,string cls,string title,int style,int x,int y,int w,int h,IntPtr owner,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr hwnd);
}
