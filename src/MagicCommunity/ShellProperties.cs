using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MagicCommunity;

// File property sheets are asynchronous windows inside their caller process.
// Their providers do not reliably take SHCreateThreadRef references. Keep only
// the command worker alive, driven by its own window events; no idle polling.
public static class ShellProperties
{
    public static void Show(string target)=>Show([target]);
    public static void Show(string[] values)
    {
        string[] targets=ShellDesktop.ValidateSelection(values);
        foreach(string target in targets)if(!target.StartsWith("::",StringComparison.Ordinal)&&!File.Exists(target)&&!Directory.Exists(target))throw new FileNotFoundException("项目已不存在。",target);
        RunWindowCommand(()=>{
            // A hidden Explorer icon ListView must never own the property sheet.
            if(targets.Length==1){var info=new ExecuteInfo{Size=Marshal.SizeOf<ExecuteInfo>(),Mask=0x410c,Verb="properties",File=targets[0],Show=1};if(!ShellExecuteEx(ref info))throw new Win32Exception(Marshal.GetLastWin32Error());}
            else{object data=ShellDesktop.ContextObjects(targets,typeof(IDataObject).GUID,unowned:true);try{Marshal.ThrowExceptionForHR(SHMultiFileProperties((IDataObject)data,0));}finally{Marshal.ReleaseComObject(data);}}
        },targets.Any(target=>!target.StartsWith("::",StringComparison.Ordinal)),"Windows 未显示属性窗口，请重试。");
    }
    // Some Shell wizards create an asynchronous caller-owned window without
    // taking a Shell thread reference. Reuse the existing window event lifetime.
    internal static void RunWindowCommand(Action invoke,bool requireWindow,string missingWindow)
    {
        uint thread=GetCurrentThreadId(),pid=(uint)Environment.ProcessId;bool shown=false,finished=false,timedOut=false;
        UIntPtr timer=UIntPtr.Zero;
        var windows=new HashSet<IntPtr>();
        void Update()
        {
            windows.RemoveWhere(hwnd=>!IsWindow(hwnd)||!IsWindowVisible(hwnd));
            if(shown&&windows.Count==0){finished=true;PostThreadMessage(thread,0x8001,UIntPtr.Zero,IntPtr.Zero);}
        }
        WinEvent callback=(_,kind,hwnd,objectId,child,_,_)=>{
            if(objectId!=0||child!=0||hwnd==IntPtr.Zero)return;
            if(kind==0x8002){GetWindowThreadProcessId(hwnd,out uint owner);if(owner!=pid||GetAncestor(hwnd,2)!=hwnd)return;}
            if(kind==0x8002&&IsWindowVisible(hwnd)){windows.Add(hwnd);shown=true;if(timer!=UIntPtr.Zero){KillTimer(IntPtr.Zero,timer);timer=UIntPtr.Zero;}}
            if(kind is 0x8001 or 0x8003)windows.Remove(hwnd);
            Update();
        };
        // Force creation of the thread queue before hooking asynchronous events.
        PeekMessage(out _,IntPtr.Zero,0,0,0);
        IntPtr hook=SetWinEventHook(0x8001,0x8003,IntPtr.Zero,callback,pid,0,0);
        if(hook==IntPtr.Zero)throw new Win32Exception();
        try
        {
            invoke();
            timer=SetTimer(IntPtr.Zero,UIntPtr.Zero,10000,IntPtr.Zero);if(timer==UIntPtr.Zero)throw new Win32Exception();
            while(!finished)
            {
                int status=GetMessage(out var message,IntPtr.Zero,0,0);if(status<0)throw new Win32Exception();if(status==0)break;
                if(message.Id==0x113&&message.W==timer){if(!shown){timedOut=true;break;}KillTimer(IntPtr.Zero,timer);timer=UIntPtr.Zero;}
                TranslateMessage(ref message);DispatchMessage(ref message);
                Update();
            }
            // Virtual properties may delegate to a separate Windows application
            // (e.g. This PC opens Settings), which owns its own lifetime.
            if(timedOut&&requireWindow)throw new InvalidOperationException(missingWindow);
        }
        finally{if(timer!=UIntPtr.Zero)KillTimer(IntPtr.Zero,timer);UnhookWinEvent(hook);GC.KeepAlive(callback);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ExecuteInfo{public int Size;public uint Mask;public IntPtr Owner;public string? Verb,File,Parameters,Directory;public int Show;public IntPtr Instance,Pidl;public string? Class;public IntPtr ClassKey;public uint HotKey;public IntPtr Icon,Process;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    private delegate void WinEvent(IntPtr hook,uint kind,IntPtr hwnd,int objectId,int child,uint thread,uint time);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ShellExecuteEx(ref ExecuteInfo info);
    [DllImport("shell32.dll")]private static extern int SHMultiFileProperties([MarshalAs(UnmanagedType.Interface)]IDataObject data,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,UIntPtr w,IntPtr l);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)]private static extern UIntPtr SetTimer(IntPtr hwnd,UIntPtr id,uint milliseconds,IntPtr callback);
    [DllImport("user32.dll")]private static extern bool KillTimer(IntPtr hwnd,UIntPtr id);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,IntPtr hwnd,uint first,uint last,uint remove);
    [DllImport("user32.dll",SetLastError=true)]private static extern int GetMessage(out Message message,IntPtr hwnd,uint first,uint last);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
}
