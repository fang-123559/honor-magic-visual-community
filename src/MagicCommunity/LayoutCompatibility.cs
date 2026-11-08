using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// A downgrade exports current references, not an old snapshot that discards later edits.
public static class LayoutCompatibility
{
    public static string ExportV1(LayoutState state)
    {
        StateStore.Validate(state);
        if(state.Boards.Any(board=>DesktopCards.Items(board).Any()))throw new InvalidDataException("旧版布局不支持桌面卡片；请先保留完整v5布局，不能静默丢弃卡片设置。");
        var boards=state.Boards.Select(board=>
        {
            var boxes=board.Boxes.Select(box=>new {box.Id,box.Name,box.Collapsed,Items=box.Items.Select(Item).ToArray()}).ToList();
            if(board.RootItems.Count>0)
            {
                if(boxes.Count>=100)throw new InvalidDataException("旧版每屏最多 100 个收纳盒，无法安全导出独立图标；请先减少一个文件夹。");
                // Legacy boxes permit 2,000 references, while v2 roots permit 4,096.
                foreach(var chunk in board.RootItems.Chunk(2000))
                {
                    if(boxes.Count>=100)throw new InvalidDataException("旧版收纳盒容量不足，当前布局未改变。");
                    boxes.Add(new {Id=Guid.NewGuid().ToString("N"),Name="网格独立图标（回退保留）",Collapsed=false,Items=chunk.Select(Item).ToArray()});
                }
            }
            return new {board.Id,board.Name,Boxes=boxes};
        }).ToArray();
        return JsonSerializer.Serialize(new {SchemaVersion=1,state.SoftwareRendering,Boards=boards},StateStore.Json);
    }
    private static object Item(DesktopItem item)=>new {item.Name,item.Target,item.IsFolder,item.IsVirtual};
    public static void ExportFile(string root,string destination)
    {
        var store=new StateStore(Path.Combine(root,"data"));var state=store.Load();
        if(store.ReadOnly)throw new InvalidDataException(store.Problem);
        string output=Path.GetFullPath(destination);
        if(File.Exists(output)||Directory.Exists(output))throw new IOException("导出目标已存在，未覆盖文件。");
        string text=ExportV1(state);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary=output+".tmp-"+Guid.NewGuid().ToString("N");
        try {File.WriteAllText(temporary,text,new System.Text.UTF8Encoding(false));File.Move(temporary,output,false);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
