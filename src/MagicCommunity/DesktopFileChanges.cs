using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// A tiny durable intent bridges a Shell file operation and virtual references.
// Recovery updates metadata only; it never repeats or rolls back file operations.
public static class DesktopFileChanges
{
    public sealed record Intent(string Id,string Action,string Source,string? Destination);
    public static Intent PrepareExternalDrag(string root,string source)
    {
        if(!Path.IsPathFullyQualified(source)||(!File.Exists(source)&&!Directory.Exists(source)))throw new LayoutEditException("源项目已不存在。");
        // The destination belongs to Explorer/OLE, not a client-supplied path.
        // Recovery removes virtual references only if Windows removed the source.
        var intent=new Intent(Guid.NewGuid().ToString("N"),"external-drag",source,null);string directory=Path.Combine(root,"data","shell-changes");Directory.CreateDirectory(directory);
        StateStore.AtomicWrite(Path.Combine(directory,intent.Id+".json"),JsonSerializer.Serialize(intent,StateStore.Json),null);return intent;
    }
    public static Intent Prepare(string root,string action,string source,string? folder,string? name)
    {
        if(!Path.IsPathFullyQualified(source)||(!File.Exists(source)&&!Directory.Exists(source)))throw new LayoutEditException("源项目已不存在。");
        string? destination=null;
        if(action=="rename")
        {
            if(string.IsNullOrWhiteSpace(name)||name is "." or ".."||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.EndsWith('.')||name.EndsWith(' '))throw new LayoutEditException("文件名无效。");
            destination=AvailableRename(source,name);
        }
        else if(action=="move")
        {
            if(folder is null||!Directory.Exists(folder))throw new LayoutEditException("目标不是可用的真实文件夹。");
            destination=Path.Combine(Path.GetFullPath(folder),Path.GetFileName(source));
            if(folder.Equals(source,StringComparison.OrdinalIgnoreCase)||folder.StartsWith(source.TrimEnd('\\')+'\\',StringComparison.OrdinalIgnoreCase))throw new LayoutEditException("不能移到项目自身或其子文件夹。");
        }
        else if(action!="recycle")throw new InvalidDataException();
        if(destination is not null&&(File.Exists(destination)||Directory.Exists(destination))&&!(action=="rename"&&StringComparer.OrdinalIgnoreCase.Equals(source,destination)))throw new LayoutEditException("目标已有同名项目，未覆盖。");
        var intent=new Intent(Guid.NewGuid().ToString("N"),action,source,destination);string directory=Path.Combine(root,"data","shell-changes");Directory.CreateDirectory(directory);
        StateStore.AtomicWrite(Path.Combine(directory,intent.Id+".json"),JsonSerializer.Serialize(intent,StateStore.Json),null);return intent;
    }
    public static void Apply(LayoutState state,Intent intent)
    {
        bool changed=!File.Exists(intent.Source)&&!Directory.Exists(intent.Source);
        if(!changed&&intent.Action=="rename"&&intent.Destination is not null&&StringComparer.OrdinalIgnoreCase.Equals(intent.Source,intent.Destination))changed=ActualNameExists(intent.Destination);
        if(!changed)return;
        if(intent.Action=="rename"&&(intent.Destination is null||(!File.Exists(intent.Destination)&&!Directory.Exists(intent.Destination))))return;
        foreach(var board in state.Boards)
        {
            var old=new DesktopItem{Target=intent.Source};string oldKey=DesktopGridActions.ItemKey(old);
            foreach(var list in new[]{board.RootItems}.Concat(board.Boxes.Select(box=>box.Items)))
            {
                if(intent.Action=="rename")foreach(var item in list.Where(item=>item.Target.Equals(intent.Source,StringComparison.OrdinalIgnoreCase))){item.Target=intent.Destination!;item.Name=Path.GetFileName(intent.Destination!);}
                else list.RemoveAll(item=>item.Target.Equals(intent.Source,StringComparison.OrdinalIgnoreCase));
            }
            foreach(var placements in DesktopPages.Grids(board).Select(grid=>grid.Placements).Append(board.Unplaced))
                if(placements.Remove(oldKey,out var position)&&position is not null&&intent.Action=="rename")placements[DesktopGridActions.ItemKey(new(){Target=intent.Destination!})]=position;
        }
    }
    public static void Recover(string root,StateStore store,LayoutState state,bool includeExternal=true)
    {
        string directory=Path.Combine(root,"data","shell-changes");if(!Directory.Exists(directory))return;
        foreach(string path in Directory.GetFiles(directory,"*.json"))
        {
            var intent=JsonSerializer.Deserialize<Intent>(File.ReadAllText(path),StateStore.Json)??throw new InvalidDataException("Shell intent is unreadable.");
            if(intent.Action=="external-drag"&&!includeExternal)continue;
            Apply(state,intent);state.Revision++;store.Save(state);File.Delete(path);
        }
    }
    public static void Finish(string root,StateStore store,LayoutState state,string id)=>FinishMany(root,store,state,[id]);
    public static int FinishMany(string root,StateStore store,LayoutState state,IEnumerable<string> values)
    {
        var ids=values.Distinct(StringComparer.Ordinal).ToArray();if(ids.Length>256||ids.Any(id=>id.Length!=32||!id.All(Uri.IsHexDigit)))throw new InvalidDataException();
        var records=new List<(string Path,Intent Intent)>();
        foreach(string id in ids){string path=Path.Combine(root,"data","shell-changes",id+".json");if(!File.Exists(path))continue;var intent=JsonSerializer.Deserialize<Intent>(File.ReadAllText(path),StateStore.Json)??throw new InvalidDataException();if(intent.Id!=id||intent.Action!="external-drag"||intent.Destination is not null)throw new InvalidDataException();records.Add((path,intent));}
        if(records.Count==0)return 0;
        string before=JsonSerializer.Serialize(state,StateStore.Json);foreach(var row in records)Apply(state,row.Intent);
        if(JsonSerializer.Serialize(state,StateStore.Json)!=before){state.Revision++;store.Save(state);}
        // A cleanup error after the atomic save must not roll in-memory state
        // back. Remaining exact journals are idempotently recovered next entry.
        int deferred=0;foreach(var row in records)try{File.Delete(row.Path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){deferred++;}return deferred;
    }
    private static string AvailableRename(string source,string name)
    {
        string directory=Path.GetDirectoryName(source)!,candidate=Path.Combine(directory,name);
        if(StringComparer.OrdinalIgnoreCase.Equals(candidate,source)||(!File.Exists(candidate)&&!Directory.Exists(candidate)))return candidate;
        int dot=Directory.Exists(source)?-1:name.LastIndexOf('.');string extension=dot>0?name[dot..]:"",stem=dot>0?name[..dot]:name;
        int start=2,suffix=stem.LastIndexOf(" (",StringComparison.Ordinal);
        if(suffix>0&&stem.EndsWith(')')&&int.TryParse(stem.AsSpan(suffix+2,stem.Length-suffix-3),out int prior)&&prior>=2&&prior<1_000_000){start=prior+1;stem=stem[..suffix];}
        for(int offset=0;offset<10000;offset++){candidate=Path.Combine(directory,$"{stem} ({start+offset}){extension}");if(!File.Exists(candidate)&&!Directory.Exists(candidate))return candidate;}
        throw new LayoutEditException("同名项目过多，请使用其它名称。");
    }
    private static bool ActualNameExists(string path)=>Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,Path.GetFileName(path)).Any(value=>Path.GetFileName(value)==Path.GetFileName(path));
    public static object Execute(string action,string source,string? destination)
    {
        if(action=="move")ShellFileMove.Move(source,destination!,true);
        else if(action=="rename"){
            if(string.Equals(Path.GetFileName(source),Path.GetFileName(destination!),StringComparison.Ordinal))return new{ok=true,unchanged=true};
            ShellFileMove.Rename(source,Path.GetFileName(destination!));
        }
        else if(action=="recycle")ShellFileMove.Recycle(source);
        else throw new InvalidDataException();
        bool sameCasePath=action=="rename"&&destination is not null&&StringComparer.OrdinalIgnoreCase.Equals(source,destination);
        if(sameCasePath?!ActualNameExists(destination!):File.Exists(source)||Directory.Exists(source))throw new IOException("Windows 操作尚未完成，源项目仍在原处。");
        if(destination is not null&&!File.Exists(destination)&&!Directory.Exists(destination))throw new IOException("目标项目尚未出现。");
        return new{ok=true};
    }
}
