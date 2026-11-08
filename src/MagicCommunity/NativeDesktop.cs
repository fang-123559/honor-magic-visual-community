using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MagicCommunity;

public sealed record NativeIconRect(int Left,int Top,int Right,int Bottom);
public sealed record NativeDesktopSnapshot(bool Available,string? Error,long ListWindow,int SpacingX,int SpacingY,NativeIconRect[] Icons,string[]? VisibleTargets=null);

// Uses read-only Shell view methods. Never selects, moves, hides, or deletes native icons.
public static class NativeDesktop
{
    internal static T WithView<T>(Func<object,T> action)
    {
        object? windows=null,dispatch=null,browser=null,view=null;
        try
        {
            windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            object location=0,root=0;
            dispatch=((IShellWindows)windows!).FindWindowSW(ref location,ref root,8,out _,1)??throw new InvalidOperationException("Explorer desktop unavailable.");
            var sid=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");var iid=typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IServiceProvider)dispatch).QueryService(ref sid,ref iid,out browser));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browser!).QueryActiveShellView(out view));
            return action(view!);
        }
        finally{foreach(var value in new[]{view,browser,dispatch,windows}.Distinct(ReferenceEqualityComparer.Instance))if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }
    public static NativeDesktopSnapshot Read()
    {
        object? windows=null,dispatch=null,browser=null,view=null;
        try
        {
            windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            object location=0,locationRoot=0;
            dispatch=((IShellWindows)windows!).FindWindowSW(ref location,ref locationRoot,8,out _,1)??throw new InvalidOperationException("Explorer 桌面视图不可用。");
            var sid=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");var iid=new Guid("000214E2-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(((IServiceProvider)dispatch).QueryService(ref sid,ref iid,out browser));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browser!).QueryActiveShellView(out view));
            var folder=(IFolderView)view!;IntPtr list=FindDesktopListView();if(list==IntPtr.Zero)throw new InvalidOperationException("原生桌面图标窗口不可用。");
            var origin=new Point();if(!ClientToScreen(list,ref origin))throw new Win32Exception();
            Marshal.ThrowExceptionForHR(folder.GetSpacing(out var spacing));
            Marshal.ThrowExceptionForHR(folder.ItemCount(2,out int count));if(count is <0 or >4096)throw new InvalidOperationException("原生桌面项目数量无效。");
            var icons=new List<NativeIconRect>();var targets=new List<string>();bool visible=IsWindowVisible(list);
            for(int index=0;index<count;index++)
            {
                Marshal.ThrowExceptionForHR(folder.Item(index,out var pidl));
                try
                {
                    Marshal.ThrowExceptionForHR(SHGetNameFromIDList(pidl,0x80028000,out var target));
                    try{targets.Add(Marshal.PtrToStringUni(target)??throw new InvalidDataException());}finally{Marshal.FreeCoTaskMem(target);}
                    if(visible){Marshal.ThrowExceptionForHR(folder.GetItemPosition(pidl,out var position));icons.Add(new(origin.X+position.X,origin.Y+position.Y,origin.X+position.X+spacing.X,origin.Y+position.Y+spacing.Y));}
                }
                finally{Marshal.FreeCoTaskMem(pidl);}
            }
            Marshal.ThrowExceptionForHR(folder.ItemCount(2,out int countAfter));
            if(countAfter!=count)throw new InvalidOperationException("原生桌面清单正在变化，暂不发布该快照。");
            if(spacing.X is <16 or >1024||spacing.Y is <16 or >1024)throw new InvalidOperationException("原生桌面网格间距无效。");
            return new(true,null,list.ToInt64(),spacing.X,spacing.Y,icons.ToArray(),targets.ToArray());
        }
        catch(Exception ex){return new(false,ex.Message,0,0,0,[]);}
        finally{foreach(var value in new[]{view,browser,dispatch,windows}.Distinct(ReferenceEqualityComparer.Instance))if(value is not null&&Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }
    [DllImport("shell32.dll")]private static extern int SHGetNameFromIDList(IntPtr pidl,uint kind,out IntPtr name);
    [StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}
    public static IntPtr FindDesktopListView(){IntPtr found=IntPtr.Zero;EnumWindows((window,_)=>{var view=FindWindowEx(window,IntPtr.Zero,"SHELLDLL_DefView",null);if(view==IntPtr.Zero)return true;found=FindWindowEx(view,IntPtr.Zero,"SysListView32",null);return found==IntPtr.Zero;},IntPtr.Zero);return found;}
    private delegate bool EnumWindowsCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string? name,string? title);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr hwnd);
    [ComImport,Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]private interface IShellWindows
    {[DispId(1610743816)][return:MarshalAs(UnmanagedType.IDispatch)]object? FindWindowSW([In]ref object location,[In]ref object root,int kind,out int hwnd,int options);}
    [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IServiceProvider
    {[PreserveSig]int QueryService(ref Guid service,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object result);}
    [ComImport,Guid("00000114-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IOleWindow
    {[PreserveSig]int GetWindow(out IntPtr hwnd);[PreserveSig]int ContextSensitiveHelp(bool enter);}
    [ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IShellBrowser
    {
        [PreserveSig]int GetWindow(out IntPtr hwnd);[PreserveSig]int ContextSensitiveHelp(bool enter);
        [PreserveSig]int InsertMenusSB(IntPtr menu,IntPtr widths);[PreserveSig]int SetMenuSB(IntPtr menu,IntPtr oleMenu,IntPtr active);
        [PreserveSig]int RemoveMenusSB(IntPtr menu);[PreserveSig]int SetStatusTextSB(IntPtr text);[PreserveSig]int EnableModelessSB(bool enable);
        [PreserveSig]int TranslateAcceleratorSB(IntPtr message,ushort id);[PreserveSig]int BrowseObject(IntPtr pidl,uint flags);
        [PreserveSig]int GetViewStateStream(uint mode,out IntPtr stream);[PreserveSig]int GetControlWindow(uint id,out IntPtr hwnd);
        [PreserveSig]int SendControlMsg(uint id,uint message,IntPtr w,IntPtr l,out IntPtr result);
        [PreserveSig]int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)]out object view);
    }
    [ComImport,Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IFolderView
    {
        [PreserveSig]int GetCurrentViewMode(out uint mode);[PreserveSig]int SetCurrentViewMode(uint mode);[PreserveSig]int GetFolder(ref Guid iid,out IntPtr folder);
        [PreserveSig]int Item(int index,out IntPtr pidl);[PreserveSig]int ItemCount(uint flags,out int count);[PreserveSig]int Items(uint flags,ref Guid iid,out IntPtr items);
        [PreserveSig]int GetSelectionMarkedItem(out int index);[PreserveSig]int GetFocusedItem(out int index);
        [PreserveSig]int GetItemPosition(IntPtr pidl,out Point position);[PreserveSig]int GetSpacing(out Point spacing);
    }
}
