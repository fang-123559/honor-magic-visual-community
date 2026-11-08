using System.IO;
using System.Text.Json;

namespace MagicCommunity;

internal sealed record ShortcutPreferences(bool Win=false,bool WinA=false)
{
    internal int Modes => (Win?1:0)|(WinA?2:0);
    private static string FilePath(string root)=>Path.Combine(root,"data","keyboard-shortcuts.json");
    internal static bool IsReadOnly(string root)
    {
        try {return (File.GetAttributes(FilePath(root))&FileAttributes.ReadOnly)!=0;}
        catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException){return false;}
        catch(Exception error) when(IsConfigError(error)){return true;}
    }
    private static JsonElement Read(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(file.Length is <=0 or >8192)throw new InvalidDataException("快捷键配置大小无效。");
        using var json=JsonDocument.Parse(file,new JsonDocumentOptions{MaxDepth=4});
        return json.RootElement.Clone();
    }
    internal void Save(string root)=>StateStore.AtomicWrite(FilePath(root),JsonSerializer.Serialize(new{schema=1,win=Win,winA=WinA}),null);
    internal static ShortcutPreferences Initialize(string root,bool readOnly)
    {
        if(readOnly || IsReadOnly(root))return new();
        try {
            JsonElement config;
            try {config=Read(FilePath(root));}
            catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException) {
                bool legacy=false;
                try {
                    var receipt=Read(Path.Combine(root,"runtime","system","native-ready.json"));
                    legacy=receipt.GetProperty("schemaVersion").GetInt32()==1 &&
                        receipt.GetProperty("protocol").GetString()==NativeSystemClient.Protocol &&
                        !receipt.TryGetProperty("keyboardPreferencesVersion",out _) &&
                        receipt.GetProperty("adapterPid").GetInt32()>0 && receipt.GetProperty("backendPid").GetInt32()>0 &&
                        Version.TryParse(receipt.GetProperty("version").GetString()?.Split('-')[0],out var version) &&
                        version>=new Version(0,3,9) && version<=new Version(0,4,0);
                } catch(Exception error) when(IsConfigError(error)) { }
                var initial=new ShortcutPreferences(legacy,false);
                initial.Save(root);
                return initial;
            }
            if(config.ValueKind!=JsonValueKind.Object||config.GetProperty("schema").GetInt32()!=1)return new();
            return new(config.GetProperty("win").GetBoolean(),config.GetProperty("winA").GetBoolean());
        } catch(Exception error) when(IsConfigError(error)) {return new();}
    }
    private static bool IsConfigError(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or System.Security.SecurityException;
}
