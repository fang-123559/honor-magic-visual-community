namespace MagicCommunity;

// Layout-only transaction helpers. Controller.Commit owns the copied state;
// validation failure never commits even one member of a group.
public static class DesktopGroupBridgeActions
{
    public static DesktopItem[] Resolve(Board board,string[] keys,string? sourceFolder)
    {
        if(keys.Length is <1 or >256||keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length)throw new LayoutEditException("请重新选择要移动的项目。");
        var source=sourceFolder is null?board.RootItems:board.Boxes.Single(box=>box.Id==sourceFolder).Items;
        return keys.Select(key=>source.SingleOrDefault(item=>DesktopGridActions.ItemKey(item)==key)??throw new LayoutEditException("选中项目已变化，请重试。")).ToArray();
    }
    public static void Reference(LayoutState state,Board source,Board destination,string[] keys,string anchor,string? sourceFolder,string? destinationFolder,int x,int y)
    {
        var items=Resolve(source,keys,sourceFolder);if(!keys.Contains(anchor))throw new LayoutEditException("拖动锚点已变化。");
        // Returning a still-held group to its collapsed source folder cancels
        // the move. Resolve first, but preserve ordering and automatic sort.
        if(source.Id==destination.Id&&sourceFolder is not null&&sourceFolder==destinationFolder)return;
        DesktopSort.Manual(source);DesktopSort.Manual(destination);
        if(destinationFolder is not null){UnifiedDesktopGrid.Collect(state,destination,destinationFolder,items);return;}
        if(source.Id==destination.Id&&sourceFolder is null){DesktopGroupOperations.Place(source,keys,anchor,x,y);return;}
        var positions=new Dictionary<string,GridRect>(StringComparer.Ordinal);
        int columns=Math.Min(4,Math.Min(destination.Grid!.Columns,(int)Math.Ceiling(Math.Sqrt(keys.Length))));
        for(int i=0;i<keys.Length;i++)positions[keys[i]]=sourceFolder is null?source.Grid!.Placements[keys[i]]:new GridRect(i%columns,i/columns,1,1);
        var list=sourceFolder is null?source.RootItems:source.Boxes.Single(box=>box.Id==sourceFolder).Items;
        foreach(var item in items){list.Remove(item);source.Grid!.Placements.Remove(DesktopGridActions.ItemKey(item));if(!destination.RootItems.Any(row=>StringComparer.OrdinalIgnoreCase.Equals(row.Target,item.Target)))destination.RootItems.Add(item);}
        foreach(var row in positions)destination.Grid!.Placements[row.Key]=row.Value;
        DesktopGroupOperations.Place(destination,keys,anchor,x,y);DesktopGridActions.ReconcileBoard(source);
    }
}
