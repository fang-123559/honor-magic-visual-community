using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagicCommunity;

// The adapter is restarted when the host pointer changes. Validate the immutable
// candidate once per adapter session, rather than hashing it on every drag event.
internal static class NativeHostSelection
{
    private static readonly ConcurrentDictionary<string,Lazy<string>> Trusted=new(StringComparer.OrdinalIgnoreCase);
    public static string Resolve(string root)
    {
        root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return Trusted.GetOrAdd(root,key=>new Lazy<string>(()=>ReadVerified(key))).Value;
    }
    internal static void RequireNativePopupSupport(string root)
    {
        if(NativeSystemDeployment.IsSelected(root)){_ = NativeSystemDeployment.Selected(root)??throw new InvalidDataException("Independent native deployment is absent.");return;}
        if(CompositionPopupSession.Selected(root).Length==0)return;
        string path=Resolve(root);byte[] marker=System.Text.Encoding.ASCII.GetBytes("Magic.NativePopups/1");
        using var stream=File.OpenRead(path);byte[] buffer=new byte[65536+marker.Length];int carry=0,count;
        while((count=stream.Read(buffer,carry,buffer.Length-carry))>0){int length=carry+count;if(buffer.AsSpan(0,length).IndexOf(marker)>=0)return;carry=Math.Min(marker.Length-1,length);buffer.AsSpan(length-carry,carry).CopyTo(buffer);}
        throw new InvalidDataException("当前选择的社区宿主尚未支持原生弹窗。请先构建并选择配套宿主，或使用不包含 nativePopups 的版本清单。");
    }
    internal static string ReadVerified(string root)
    {
        if(NativeSystemDeployment.Selected(root) is { } native)return native.Backend;
        if(NativeSystemRuntime.Selected(root) is { } independent)return independent.Host;
        using var install=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"runtime/desktop/install-state.json")));
        string original=Path.Combine(install.RootElement.GetProperty("installLocation").GetString()!,"seelen-ui.exe");
        string pointer=Path.Combine(root,"runtime/desktop/host.current.json");
        if(!File.Exists(pointer))return original;
        using var document=JsonDocument.Parse(File.ReadAllBytes(pointer));var value=document.RootElement;
        if(!value.TryGetProperty("enabled",out var enabled)||!enabled.GetBoolean())return original;
        if(value.GetProperty("schemaVersion").GetInt32()!=1||
           !StringComparer.OrdinalIgnoreCase.Equals(value.GetProperty("owner").GetString(),root)||
           value.GetProperty("package").GetString()!=install.RootElement.GetProperty("packageFullName").GetString())
            throw new InvalidDataException("社区版宿主归属或固定基线不匹配。");
        string expected=Path.GetFullPath(value.GetProperty("executable").GetString()!);
        string prefix=Path.GetFullPath(Path.Combine(root,"profiles/native-host"))+Path.DirectorySeparatorChar;
        if(!expected.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||
           !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(expected),"seelen-ui.exe"))
            throw new InvalidDataException("社区版宿主路径超出版本目录。");
        if((File.GetAttributes(Path.GetDirectoryName(expected)!)&FileAttributes.ReparsePoint)!=0||
           (File.GetAttributes(expected)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("社区版宿主路径不允许重解析点。");
        using var stream=File.OpenRead(expected);
        string actual=Convert.ToHexString(SHA256.HashData(stream));
        if(!StringComparer.OrdinalIgnoreCase.Equals(actual,value.GetProperty("sha256").GetString()))
            throw new InvalidDataException("社区版宿主文件校验失败。");
        return expected;
    }
}
