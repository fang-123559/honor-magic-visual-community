using System.Runtime.InteropServices;
namespace MagicCommunity;
// Only alive while a visible card requests data. No timer, provider traversal,
// window activation or cross-process action in a Windows event callback.
internal sealed class EncoDeckWindowEvents : IDisposable
{
    private readonly Action changed;private readonly Thread thread;
    private int process;private uint threadId;private int disposed;
    internal EncoDeckWindowEvents(Action changed){this.changed=changed;thread=new Thread(Run){IsBackground=true,Name="Magic EncoDeck events"};thread.Start();}
    internal void Track(int pid){if(Interlocked.Exchange(ref process,pid)==pid)return;uint id=Volatile.Read(ref threadId);if(id!=0)PostThreadMessage(id,0x8001,UIntPtr.Zero,IntPtr.Zero);}
    private void Run(){
        PeekMessage(out _,IntPtr.Zero,0,0,0);Volatile.Write(ref threadId,GetCurrentThreadId());
        if(Volatile.Read(ref disposed)!=0)return;
        WinEvent callback=(_,kind,window,objectId,child,_,_)=>{
            if(window==IntPtr.Zero||Volatile.Read(ref disposed)!=0||kind is not (0x8001 or 0x8002 or 0x8003 or 0x8006 or 0x800a or 0x800c))return;
            GetWindowThreadProcessId(window,out uint pid);if(pid!=(uint)Volatile.Read(ref process)||pid==0)return;
            try{changed();}catch(ObjectDisposedException){}
        };
        IntPtr hook=IntPtr.Zero;
        void Bind(){if(hook!=IntPtr.Zero)UnhookWinEvent(hook);int pid=Volatile.Read(ref process);hook=pid>0?SetWinEventHook(0x8001,0x800c,IntPtr.Zero,callback,(uint)pid,0,2):IntPtr.Zero;}
        Bind();
        try{while(Volatile.Read(ref disposed)==0&&GetMessage(out var message,IntPtr.Zero,0,0)>0){if(message.Id==0x8001){Bind();continue;}TranslateMessage(ref message);DispatchMessage(ref message);}}
        finally{UnhookWinEvent(hook);GC.KeepAlive(callback);}
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;uint id=Volatile.Read(ref threadId);if(id!=0)PostThreadMessage(id,0x12,UIntPtr.Zero,IntPtr.Zero);}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    private delegate void WinEvent(IntPtr hook,uint kind,IntPtr hwnd,int objectId,int child,uint thread,uint time);
    [DllImport("user32.dll")]private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,IntPtr window,uint first,uint last,uint remove);
    [DllImport("user32.dll")]private static extern int GetMessage(out Message message,IntPtr window,uint first,uint last);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,UIntPtr w,IntPtr l);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
}
