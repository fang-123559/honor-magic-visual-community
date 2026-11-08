using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace MagicCommunity;

// One explicit sort, no automatic sorting and no background metadata cache.
internal static class DesktopSort
{
    internal sealed record Properties(string Target,long? Size,long? Modified,string? Type);
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int StrCmpLogicalW(string a,string b);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ShellInfo{
        public IntPtr Icon;public int IconIndex;public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string TypeName;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr SHGetFileInfo(string path,uint attrs,ref ShellInfo info,uint bytes,uint flags);
    // Runs in the existing owned, time-bounded Shell worker. Never opens menus,
    // recurses folders, invokes file handlers, or reads file contents.
    internal static object ReadProperties(JsonElement transfer){
        string mode=transfer.GetProperty("mode").GetString()??"";new DesktopSortSettings{Mode=mode}.Validate();
        var rows=transfer.GetProperty("items");if(rows.GetArrayLength()>4096)throw new InvalidDataException();
        var result=new List<Properties>();var types=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows.EnumerateArray()){
            var item=row.Deserialize<DesktopItem>(StateStore.Json)??throw new InvalidDataException();
            if(item.Target.Length>32768||(!item.IsVirtual&&!Path.IsPathFullyQualified(item.Target)))throw new InvalidDataException();
            long? size=null,date=null;string? type=null;
            if(!item.IsVirtual){
                try{
                    if(mode=="size"&&!item.IsFolder){var info=new FileInfo(item.Target);if(info.Exists)size=info.Length;}
                    if(mode=="modified"){FileSystemInfo info=item.IsFolder?new DirectoryInfo(item.Target):new FileInfo(item.Target);if(info.Exists)date=info.LastWriteTimeUtc.Ticks;}
                }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){}
                if(mode=="type"){
                    string extension=item.IsFolder?"<folder>":Path.GetExtension(item.Target);
                    if(!types.TryGetValue(extension,out type)){
                        var info=new ShellInfo();var ok=SHGetFileInfo(item.IsFolder?"folder":"file"+extension,item.IsFolder?0x10u:0x80u,ref info,(uint)Marshal.SizeOf<ShellInfo>(),0x400|0x10);
                        type=ok!=IntPtr.Zero&&!string.IsNullOrWhiteSpace(info.TypeName)?info.TypeName:null;types[extension]=type;
                    }
                }
            }
            result.Add(new(item.Target,size,date,type));
        }
        return new{items=result};
    }
    internal static async Task<Dictionary<string,Properties>> Metadata(string root,Board board,string mode,CancellationToken cancellation){
        if(mode is "name" or "custom")return new(StringComparer.OrdinalIgnoreCase);
        using var worker=new DesktopMenuHost(root,reuseQueries:false);
        string requestId=Guid.NewGuid().ToString("N");
        var body=JsonSerializer.SerializeToElement(new{requestId,mode,items=board.RootItems});
        var reply=JsonSerializer.SerializeToElement(await worker.Route("/desktop-sort-properties",body,cancellation));
        return reply.GetProperty("items").Deserialize<Properties[]>(StateStore.Json)!.ToDictionary(p=>p.Target,StringComparer.OrdinalIgnoreCase);
    }
    internal static Board Arrange(Board original,string mode,bool descending,IReadOnlyDictionary<string,Properties> properties){
        new DesktopSortSettings{Mode=mode}.Validate();
        var board=DesktopGridLayout.Copy(original);
        board.SortSettings=new(){Mode=mode,Descending=descending};
        if(mode=="custom")return board;
        var items=board.RootItems.ToDictionary(DesktopGridActions.ItemKey,StringComparer.Ordinal);
        int Name(DesktopItem a,DesktopItem b){int n=StrCmpLogicalW(a.Name,b.Name);return n!=0?n:StringComparer.OrdinalIgnoreCase.Compare(a.Target,b.Target);}
        int Compare(DesktopItem a,DesktopItem b){
            var pa=properties.GetValueOrDefault(a.Target);var pb=properties.GetValueOrDefault(b.Target);
            object? ka=mode switch{"size"=>pa?.Size,"modified"=>pa?.Modified,"type"=>pa?.Type,_=>a.Name};
            object? kb=mode switch{"size"=>pb?.Size,"modified"=>pb?.Modified,"type"=>pb?.Type,_=>b.Name};
            if(ka is null||kb is null){if(ka is null&&kb is null)return Name(a,b);return ka is null?1:-1;}
            int n=ka is long x&&kb is long y?x.CompareTo(y):StrCmpLogicalW((string)ka,(string)kb);
            if(n!=0)return descending?-Math.Sign(n):Math.Sign(n);
            return Name(a,b);
        }
        foreach(var grid in DesktopPages.Grids(board)){
            var keys=grid.Placements.Keys.Where(k=>items.ContainsKey(k)).ToList();
            keys.Sort((a,b)=>Compare(items[a],items[b]));
            foreach(var key in keys)grid.Placements.Remove(key);
            int at=0;
            for(int y=0;y<grid.Rows&&at<keys.Count;y++)for(int x=0;x<grid.Columns&&at<keys.Count;x++){
                var cell=new GridRect(x,y,1,1);if(DesktopGridActions.CanFit(grid,cell))grid.Placements.Add(keys[at++],cell);
            }
            if(at!=keys.Count)throw new LayoutEditException("当前页没有足够位置，原布局已保留。");
        }
        DesktopGridActions.Validate(board);return board;
    }
    internal static void Manual(Board board){if(board.SortSettings is {} sort)sort.Mode="custom";}
}
