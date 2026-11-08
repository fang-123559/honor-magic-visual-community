using System.IO;
using System.Text.Json;
namespace MagicCommunity;
internal static class NativeNotificationPreferences
{
    private static string PathFor(string directory)=>Path.Combine(directory,"notifications-preferences.json");
    internal static bool Read(string directory)
    {
        string path=PathFor(directory);if(!File.Exists(path))return false;
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Notification preferences cannot be a link.");
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var bytes=new byte[513];int count=0,read;while(count<bytes.Length&&(read=file.Read(bytes,count,bytes.Length-count))>0)count+=read;
        if(count>512)throw new InvalidDataException("Notification preferences exceed bound.");
        using var document=JsonDocument.Parse(bytes.AsMemory(0,count),new JsonDocumentOptions{MaxDepth=4});var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=root.EnumerateObject().Count()||root.GetProperty("schema").GetInt32()!=1)throw new InvalidDataException("Notification preference schema differs.");
        return root.GetProperty("quiet").GetBoolean();
    }
    internal static void Save(string directory,bool quiet)=>StateStore.AtomicWrite(PathFor(directory),JsonSerializer.Serialize(new{schema=1,quiet}),null);
}
