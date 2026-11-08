using System.Runtime.InteropServices;
using System.Text;

namespace MagicCommunity;

// Resolve only our renderer's Explorer-owned desktop surfaces. Popup panels,
// foreign windows, and non-overlapping monitors are never restacked here.
internal static class NativeDesktopOcclusion
{
    internal static bool Covered(nint target,int renderer,bool lower=false)
    {
        if(target==0||IsIconic(target)||!GetWindowRect(target,out var app))return false;
        var seen=new HashSet<nint>();bool covered=false;
        for(nint above=GetWindow(target,3);above!=0&&seen.Count<2048&&seen.Add(above);above=GetWindow(above,3)){
            GetWindowThreadProcessId(above,out uint pid);
            if(pid!=renderer||!IsWindowVisible(above)||!GetWindowRect(above,out var rect)||
                rect.Left>=app.Right||rect.Right<=app.Left||rect.Top>=app.Bottom||rect.Bottom<=app.Top)continue;
            var name=new StringBuilder(80);GetClassName(above,name,name.Capacity);
            if(name.ToString()!="MagicCompositionTrialWindow")continue;
            nint owner=GetWindow(above,4);name.Clear();GetClassName(owner,name,name.Capacity);
            if(name.ToString() is not ("Progman" or "WorkerW"))continue;
            uint cloak=0;if(DwmGetWindowAttribute(above,14,out cloak,sizeof(uint))>=0&&cloak!=0)continue;
            covered=true;
            if(lower){
                // No synchronous cross-process input-queue wait. Windows moves
                // the owned desktop group; never use NOOWNERZORDER here.
                SetWindowPos(above,new nint(1),0,0,0,0,0x1|0x2|0x10|0x4000);
                break;
            }
        }
        return covered;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,StringBuilder text,int size);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int w,int h,uint flags);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint hwnd,uint attr,out uint value,int size);
}
