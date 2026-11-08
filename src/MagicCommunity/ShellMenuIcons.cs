using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MagicCommunity;

// Canonical optimized verbs and alpha-less native bitmaps may have no usable
// image. Read the registered Icon resource without loading the command handler.
// Cache encoded pixels only for this transient menu worker; no permanent scans.
public sealed class ShellMenuIcons
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Lazy<string?>> stock=new(StringComparer.Ordinal);
    private sealed record Resource(long Modified,long Length,string? Image);
    private static readonly object resourceGate=new();
    private static readonly Dictionary<string,Resource> resources=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string?> cache=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string?> labels=new(StringComparer.OrdinalIgnoreCase);
    private bool backgroundLoaded;
    public static string? Stock(string? key){
        if(key is null||key.Length>64||key.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))return null;
        // Fixed embedded assets only; do not retain arbitrary caller keys.
        if(stock.TryGetValue(key,out var known))return known.Value;
        string? image=ReadStock(key);if(image is null)return null;
        return stock.GetOrAdd(key,new Lazy<string?>(()=>image)).Value;
    }
    private static string? ReadStock(string key){
        using var stream=typeof(ShellMenuIcons).Assembly.GetManifestResourceStream("NativeShellMenu."+key+".png");
        if(stream is null)return null;using var bytes=new MemoryStream();stream.CopyTo(bytes);return "data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray());
    }
    public string? Get(string? target,string? verb,string label)
    {
        if(target is null&&string.IsNullOrWhiteSpace(verb))
        {
            if(!backgroundLoaded)
            {
                backgroundLoaded=true;
                foreach(string name in Classes(null))
                {
                    using var parent=Registry.ClassesRoot.OpenSubKey(name+@"\shell");
                    foreach(string child in parent?.GetSubKeyNames().Take(128)??[])
                    {
                        using var key=parent!.OpenSubKey(child);if(key?.GetValue("Icon") is not string location)continue;
                        string text=key.GetValue("MUIVerb") as string??key.GetValue(null) as string??child;
                        if(text.StartsWith('@')){var resolved=new StringBuilder(1024);if(SHLoadIndirectString(text,resolved,(uint)resolved.Capacity,IntPtr.Zero)>=0)text=resolved.ToString();}
                        labels.TryAdd(Clean(text),Extract(location));
                    }
                }
            }
            return labels.GetValueOrDefault(Clean(label));
        }
        if(string.IsNullOrWhiteSpace(verb)||verb.Length>128||verb.IndexOfAny(['\\','/','\0'])>=0)return null;
        string signature=(target is null?"background":Directory.Exists(target)?"directory":Path.GetExtension(target))+"|"+verb;
        if(cache.TryGetValue(signature,out var existing))return existing;
        string? result=null;
        foreach(string name in Classes(target))
        {
            using var key=Registry.ClassesRoot.OpenSubKey(name+@"\shell\"+verb);
            if(key?.GetValue("Icon") is string location&&(result=Extract(location)) is not null)break;
        }
        cache[signature]=result;return result;
    }
    private static string Clean(string label)=>Regex.Replace(label.Split('\t')[0],@"\(&.\)|&","").Trim();
    private static IEnumerable<string> Classes(string? target)
    {
        if(target is null){yield return "DesktopBackground";yield return @"Directory\Background";yield break;}
        if(Directory.Exists(target)){yield return "Directory";yield return "Folder";yield return "AllFilesystemObjects";yield break;}
        string extension=Path.GetExtension(target);
        if(extension.Length>0)
        {
            using var key=Registry.ClassesRoot.OpenSubKey(extension);
            if(key?.GetValue(null) is string prog&&!prog.Contains('\\'))yield return prog;
            yield return extension;yield return @"SystemFileAssociations\"+extension;
        }
        yield return "*";yield return "AllFilesystemObjects";
    }
    internal static string? Extract(string location)
    {
        location=Environment.ExpandEnvironmentVariables(location.Trim());int index=0,comma=location.LastIndexOf(',');
        if(comma>=0&&int.TryParse(location[(comma+1)..].Trim(),out int value)){index=value;location=location[..comma].Trim();}
        location=location.Trim('"');
        if(!Path.IsPathFullyQualified(location)&&Path.GetFileName(location)==location)location=Path.Combine(Environment.SystemDirectory,location);
        // Do not touch remote providers or execute registered command strings.
        if(!Path.IsPathFullyQualified(location)||location.StartsWith(@"\\",StringComparison.Ordinal)||!File.Exists(location))return null;
        string signature=location+"|"+index;long modified=0,length=0;bool cacheable=location.Length<=2048;
        if(cacheable){try{var file=new FileInfo(location);modified=file.LastWriteTimeUtc.Ticks;length=file.Length;}catch(IOException){cacheable=false;}catch(UnauthorizedAccessException){cacheable=false;}}
        if(cacheable)lock(resourceGate)if(resources.TryGetValue(signature,out var cached)&&cached.Modified==modified&&cached.Length==length)return cached.Image;
        IntPtr large=IntPtr.Zero,small=IntPtr.Zero;
        try
        {
            if(SHDefExtractIcon(location,index,0,out large,out small,16|(16u<<16))<0)return null;
            IntPtr icon=small!=IntPtr.Zero?small:large;if(icon==IntPtr.Zero)return null;
            var bitmap=Imaging.CreateBitmapSourceFromHIcon(icon,System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);
            string image="data:image/png;base64,"+Convert.ToBase64String(stream.ToArray());
            // Keep pixels, never native handles, providers, command state or
            // selections. Bound storage to 64 small resource icons per process.
            if(cacheable&&image.Length<=32768)lock(resourceGate){if(resources.Count>=64&&!resources.ContainsKey(signature))resources.Clear();resources[signature]=new(modified,length,image);}
            return image;
        }
        finally{if(large!=IntPtr.Zero)DestroyIcon(large);if(small!=IntPtr.Zero&&small!=large)DestroyIcon(small);}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHDefExtractIconW")]private static extern int SHDefExtractIcon(string file,int index,uint flags,out IntPtr large,out IntPtr small,uint size);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int SHLoadIndirectString(string source,StringBuilder destination,uint size,IntPtr reserved);
}
