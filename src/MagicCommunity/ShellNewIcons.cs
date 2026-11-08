using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MagicCommunity;

// Explorer's out-of-process New provider can expose bitmap handles that are
// unusable here. Resolve the registered file type instead, only on submenu open.
public sealed class ShellNewIcons
{
    private readonly Dictionary<string,string> icons=new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;
    public string? Get(string label)
    {
        if(!loaded){loaded=true;Load();}
        return icons.GetValueOrDefault(Clean(label));
    }
    private static string Clean(string label)=>System.Text.RegularExpressions.Regex.Replace(label.Split('\t')[0],@"\(&.\)|&","").Trim();
    private void Load()
    {
        Add("folder",true);Add("file.lnk",false);
        var extensions=new HashSet<string>(StringComparer.OrdinalIgnoreCase){".bmp",".txt",".rtf"};
        using(var cache=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew"))
            foreach(string value in cache?.GetValue("Classes") as string[]??[])if(value.StartsWith('.')&&value.Length<=80)extensions.Add(value);
        // Shell already supplies every New command. Its cached type list is
        // enough for optional fallback artwork; do not scan all HKCR or load
        // every newly registered application's icon on the interaction path.
        extensions=extensions.Take(64).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(string extension in extensions)
        {
            string? icon=Add("file"+extension,false);if(icon is null)continue;
            if(extension.Equals(".bmp",StringComparison.OrdinalIgnoreCase)){icons.TryAdd("BMP 图像",icon);icons.TryAdd("Bitmap image",icon);}
            using var ext=Registry.ClassesRoot.OpenSubKey(extension);using var shell=ext?.OpenSubKey("ShellNew");
            if(shell?.GetValue("ItemName") is string name)
            {
                var value=new StringBuilder(512);if(name.StartsWith('@')&&SHLoadIndirectString(name,value,(uint)value.Capacity,IntPtr.Zero)>=0)name=value.ToString();
                icons.TryAdd(Clean(name),icon);
            }
        }
    }
    private string? Add(string path,bool folder)
    {
        var info=new FileInfo();SHGetFileInfo(path,folder?0x10u:0x80u,ref info,(uint)Marshal.SizeOf<FileInfo>(),0x100|0x400|0x10|0x1);
        if(info.Icon==IntPtr.Zero)return null;
        try
        {
            var bitmap=Imaging.CreateBitmapSourceFromHIcon(info.Icon,System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);
            string result="data:image/png;base64,"+Convert.ToBase64String(stream.ToArray());icons.TryAdd(Clean(info.Type),result);return result;
        }
        finally{DestroyIcon(info.Icon);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct FileInfo{public IntPtr Icon;public int Index;public uint Attributes;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string Type;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr SHGetFileInfo(string path,uint attributes,ref FileInfo info,uint size,uint flags);
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int SHLoadIndirectString(string source,StringBuilder destination,uint size,IntPtr reserved);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
}
