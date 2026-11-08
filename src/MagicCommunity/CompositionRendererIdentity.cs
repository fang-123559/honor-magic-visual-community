using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagicCommunity;
internal sealed class CompositionRendererIdentity : IDisposable
{
    private static readonly Dictionary<string,CompositionRendererIdentity> Sessions=new(StringComparer.OrdinalIgnoreCase);
    private readonly string root,path;
    private readonly Process child;
    private readonly long created;
    private bool disposed;
    public static string Resolve()
    {
        string directory=AppContext.BaseDirectory,path=Path.Combine(directory,"MagicDesktopComposition.exe");
        using var receipt=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"composition-renderer.json")));
        if(receipt.RootElement.GetProperty("schemaVersion").GetInt32()!=1||
           !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),receipt.RootElement.GetProperty("sha256").GetString()))
            throw new InvalidDataException("原生桌面渲染器校验失败。");
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("原生渲染器不接受链接。");
        return path;
    }
    public CompositionRendererIdentity(string root,Process child,string path)
    {
        this.root=Path.GetFullPath(root);this.child=child;this.path=Path.GetFullPath(path);created=child.StartTime.ToFileTimeUtc();
        if(Sessions.ContainsKey(this.root))throw new InvalidOperationException("已有原生桌面宿主。");
        Sessions.Add(this.root,this);
    }
    public static bool Verify(long window,string root)
    {
        if(!Sessions.TryGetValue(Path.GetFullPath(root),out var value)||value.disposed)return false;
        GetWindowThreadProcessId(new(window),out uint pid);
        if(pid!=value.child.Id)return false;
        using var current=Process.GetProcessById((int)pid);
        return !current.HasExited&&current.StartTime.ToFileTimeUtc()==value.created&&
            StringComparer.OrdinalIgnoreCase.Equals(current.MainModule?.FileName,value.path);
    }
    public void Dispose(){if(disposed)return;disposed=true;Sessions.Remove(root);}
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
}
