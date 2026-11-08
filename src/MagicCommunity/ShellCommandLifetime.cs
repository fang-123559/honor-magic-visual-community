using System.Runtime.InteropServices;

namespace MagicCommunity;

// A command may own an async Shell task (for example a property sheet).
// Keep its temporary STA alive until Shell releases it; idle desktop has no loop.
public sealed class ShellCommandLifetime:IDisposable
{
    private readonly IntPtr count=Marshal.AllocHGlobal(4);
    private IntPtr reference;
    public ShellCommandLifetime()
    {
        Marshal.WriteInt32(count,0);Marshal.ThrowExceptionForHR(SHCreateThreadRef(count,out reference));Marshal.ThrowExceptionForHR(SHSetThreadRef(reference));
    }
    public void Dispose()
    {
        SHSetThreadRef(IntPtr.Zero);if(reference!=IntPtr.Zero){Marshal.Release(reference);reference=IntPtr.Zero;}
        while(Marshal.ReadInt32(count)>0)
        {
            while(PeekMessage(out var message,IntPtr.Zero,0,0,1)){TranslateMessage(ref message);DispatchMessage(ref message);}
            if(Marshal.ReadInt32(count)>0)MsgWaitForMultipleObjectsEx(0,IntPtr.Zero,100,0x4ff,4);
        }
        Marshal.FreeHGlobal(count);
    }
    internal static void DrainZipTasks()
    {
        // Windows ZIP Invoke returns before writing the archive and does not
        // reliably retain SHCreateThreadRef (or SHSetInstanceExplorer). After
        // releasing the menu COM objects, its COM server lock is the remaining
        // task lifetime. Pin only the already-loaded system ZIP module while
        // checking its documented DllCanUnloadNow export. Do not load a module,
        // unload COM libraries, use a fixed grace period, or poll while idle.
        string modulePath=System.IO.Path.Combine(Environment.SystemDirectory,"zipfldr.dll");
        if(!GetModuleHandleEx(0,modulePath,out var module))return;
        try{
            IntPtr entry=GetProcAddress(module,"DllCanUnloadNow");if(entry==IntPtr.Zero)return;
            var canUnload=Marshal.GetDelegateForFunctionPointer<CanUnload>(entry);
            while(canUnload()==1){
                while(PeekMessage(out var message,IntPtr.Zero,0,0,1)){TranslateMessage(ref message);DispatchMessage(ref message);}
                if(canUnload()==1)MsgWaitForMultipleObjectsEx(0,IntPtr.Zero,50,0x4ff,4);
            }
        }finally{FreeLibrary(module);}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int CanUnload();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool GetModuleHandleEx(uint flags,string name,out IntPtr module);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")]private static extern bool FreeLibrary(IntPtr module);
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    [DllImport("shlwapi.dll")]private static extern int SHCreateThreadRef(IntPtr count,out IntPtr reference);
    [DllImport("shlwapi.dll")]private static extern int SHSetThreadRef(IntPtr reference);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,IntPtr hwnd,uint first,uint last,uint remove);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]private static extern uint MsgWaitForMultipleObjectsEx(uint count,IntPtr handles,uint milliseconds,uint wakeMask,uint flags);
}
