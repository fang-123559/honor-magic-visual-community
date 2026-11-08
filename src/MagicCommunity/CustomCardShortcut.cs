using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MagicCommunity;

public static class CustomCardShortcut
{
    public static void Open(string type,string target)
    {
        if(type=="path"){OpenPath(target);return;}
        if(type!="url"||!CustomCardLibrary.IsWebUrl(target))throw new LayoutEditException("只支持HTTP/HTTPS网页。");
        Process.Start(new ProcessStartInfo(target){UseShellExecute=true});
    }
    public static void OpenPath(string target)
    {
        if(!CustomCardLibrary.IsLocalPath(target))throw new LayoutEditException("只支持本地绝对路径。");
        target=Path.GetFullPath(target);bool folder=Directory.Exists(target);if(!folder&&!File.Exists(target))throw new LayoutEditException("快捷入口的文件或目录已不存在。");
        if(folder){var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=false};start.ArgumentList.Add(target);Process.Start(start);return;}
        nint full=0,parent=0,items=0;try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(target,0,out full,0,out _));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetDirectoryName(target)!,0,out parent,0,out _));items=Marshal.AllocCoTaskMem(nint.Size);Marshal.WriteIntPtr(items,ILFindLastID(full));Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(parent,1,items,0));
        }finally{if(items!=0)Marshal.FreeCoTaskMem(items);if(parent!=0)Marshal.FreeCoTaskMem(parent);if(full!=0)Marshal.FreeCoTaskMem(full);}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,nint bind,out nint pidl,uint attrs,out uint result);
    [DllImport("shell32.dll")]private static extern int SHOpenFolderAndSelectItems(nint pidl,uint count,nint items,uint flags);
    [DllImport("shell32.dll")]private static extern nint ILFindLastID(nint pidl);
}
