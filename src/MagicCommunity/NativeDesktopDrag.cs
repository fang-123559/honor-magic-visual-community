using System.Runtime.InteropServices;
using System.Text;

namespace MagicCommunity;

// Read-only routing at the current mouse position. OLE decides whether the
// external application's actual drop target accepts the Shell IDataObject.
public static class NativeDesktopDrag
{
    public readonly record struct Target(bool Eligible,bool External);

    public static bool CanHandoff(IEnumerable<long> desktopWindows)=>Inspect(desktopWindows).Eligible;

    // External is separate from Eligible so a release-time guard can refuse a
    // layout move over an application even after the physical button is up.
    public static Target Inspect(IEnumerable<long> desktopWindows)
    {
        if(!GetCursorPos(out var point))throw new InvalidOperationException("无法读取当前拖放位置，已保留原位置。");
        var root=GetAncestor(WindowFromPoint(point),2);if(root==IntPtr.Zero)throw new InvalidOperationException("无法确认当前拖放窗口，已保留原位置。");
        var type=new StringBuilder(128);if(GetClassName(root,type,type.Capacity)==0)throw new InvalidOperationException("拖放窗口已变化，已保留原位置。");
        bool external=IsExternalTarget(root.ToInt64(),type.ToString(),IsWindowVisible(root),desktopWindows);
        return new(external&&IsWindowEnabled(root)&&LeftPressed,external);
    }

    // No application executable/class allow-list: Chromium, WinUI, WPF, classic
    // Win32 and Explorer all enter the same native DoDragDrop path. The exclusions
    // are routing surfaces, not a claim that an application supports file drops.
    internal static bool IsExternalTarget(long root,string className,bool visible,IEnumerable<long> desktopWindows)
        =>root!=0&&visible&&!string.IsNullOrEmpty(className)&&!desktopWindows.Contains(root)&&className is not
            ("Progman" or "WorkerW" or "SHELLDLL_DefView" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
             "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland");
    public static bool LeftPressed=>(GetAsyncKeyState(1)&0x8000)!=0;
    public static (bool Control,bool Shift) Modifiers()=>((GetAsyncKeyState(0x11)&0x8000)!=0,(GetAsyncKeyState(0x10)&0x8000)!=0);
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr hwnd,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr hwnd);
}
