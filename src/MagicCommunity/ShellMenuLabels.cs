using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MagicCommunity;

// Optimized Shell queries can return canonical verb names. Resolve registered
// local display strings without invoking the skipped extension providers.
public static class ShellMenuLabels
{
    private static readonly Dictionary<string,string> Standard=new(StringComparer.OrdinalIgnoreCase){["open"]="打开",["edit"]="编辑",["print"]="打印",["runas"]="以管理员身份运行"};
    public static string Resolve(string? target,string label,string? verb)
    {
        string candidate=string.IsNullOrWhiteSpace(verb)?label:verb;
        if(!label.Equals(candidate,StringComparison.OrdinalIgnoreCase)||candidate.Length>128||candidate.IndexOfAny(['\\','/','\0'])>=0)return label;
        if(Standard.TryGetValue(candidate,out var standard))return standard;
        var classes=target is null?new List<string>{"DesktopBackground",@"Directory\Background"}:new List<string>{"*","AllFilesystemObjects"};string? extension=Path.GetExtension(target);
        if(!string.IsNullOrEmpty(extension))
        {
            classes.Add(extension);classes.Add(@"SystemFileAssociations\"+extension);
            using var key=Registry.ClassesRoot.OpenSubKey(extension);if(key?.GetValue(null) is string prog&&!prog.Contains('\\'))classes.Add(prog);
        }
        foreach(string name in classes)
        {
            using var key=Registry.ClassesRoot.OpenSubKey(name+@"\shell\"+candidate);string? value=key?.GetValue("MUIVerb") as string??key?.GetValue(null) as string;
            if(string.IsNullOrWhiteSpace(value))continue;
            if(value.StartsWith('@')){var text=new StringBuilder(1024);if(SHLoadIndirectString(value,text,(uint)text.Capacity,IntPtr.Zero)>=0&&!string.IsNullOrWhiteSpace(text.ToString()))return text.ToString();}
            else return value;
        }
        return label;
    }
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int SHLoadIndirectString(string source,StringBuilder destination,uint size,IntPtr reserved);
}
