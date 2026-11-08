namespace MagicCommunity;

public static class UnifiedDesktopGrid
{
    public const double Cell=96,Inset=8,MarginX=14,MarginY=8;
    public static void PruneUnshownRoots(LayoutState state,IReadOnlySet<string> hidden)
    {
        // Only remove auto-exposed root entries; an explicit folder reference survives.
        foreach(var board in state.Boards)board.RootItems.RemoveAll(item=>hidden.Contains(item.Target));
    }
    public static (double X,double Y,int Column,int Row) ProjectDrop(Display source,double sourceScale,double clientX,double clientY,Display destination,double destinationScale,double scrollX,double scrollY,double offsetX,double offsetY,AccessoryProjection? actual=null)
    {
        var projection=actual??AccessoryProjection.Create(destination,destinationScale);
        double step=projection.StepX/destinationScale;
        double x=(source.X+clientX*sourceScale-destination.X)/destinationScale+scrollX-projection.OriginX/destinationScale;
        double y=(source.Y+clientY*sourceScale-destination.Y)/destinationScale+scrollY-projection.OriginY/destinationScale;
        return(x,y,(int)Math.Floor((x-offsetX)/step+.5),(int)Math.Floor((y-offsetY)/step+.5));
    }
    public static void Transfer(LayoutState state,Board source,Board destination,string key,string? sourceFolder,int x,int y,string? destinationFolder=null)
    {
        if(source.Id==destination.Id)throw new LayoutEditException("跨屏目标必须是另一块屏幕。");
        if(key.StartsWith("folder:",StringComparison.Ordinal))
        {
            if(sourceFolder is not null||destinationFolder is not null)throw new LayoutEditException("大文件夹不能放入另一个文件夹。");
            DesktopGridActions.MoveScreen(source,destination,key);
        }
        else
        {
            var origin=sourceFolder is null?source.RootItems:source.Boxes.Single(box=>box.Id==sourceFolder).Items;
            var item=origin.SingleOrDefault(item=>DesktopGridActions.ItemKey(item)==key)??throw new LayoutEditException("来源已变化，请重试。");
            if(destinationFolder is not null){Collect(state,destination,destinationFolder,[item]);return;}
            if(state.Boards.SelectMany(board=>board.Boxes).Where(box=>box.Id!=sourceFolder).SelectMany(box=>box.Items).Any(row=>StringComparer.OrdinalIgnoreCase.Equals(row.Target,item.Target)))throw new LayoutEditException("项目仍在其它文件夹中，请先移除重复引用。");
            var slot=new GridRect(x,y,1,1);
            if(destination.Pages is not null&&!DesktopGridActions.CanFit(destination.Grid!,slot))throw new LayoutEditException("目标当前页已满或落点被占用，原位置已保留。");
            origin.Remove(item);DesktopPages.Forget(source,key);
            if(!destination.RootItems.Any(row=>StringComparer.OrdinalIgnoreCase.Equals(row.Target,item.Target)))destination.RootItems.Add(item);
            if(destination.Pages is not null)destination.Grid!.Placements[key]=slot;
            DesktopGridActions.ReconcileBoard(source);DesktopGridActions.ReconcileBoard(destination);
        }
        DesktopGridActions.Place(destination,key,x,y);DesktopSort.Manual(source);DesktopSort.Manual(destination);
    }
    public static void Ensure(Board board,AccessoryProjection projection)
    {
        board.Grid??=new();var grid=board.Grid;
        if(board.Pages is not null)
        {if(projection.AdaptiveSecondary)DesktopGridLayout.ResizeAdaptiveSecondary(board,projection);else{DesktopGridLayout.ResizeWithoutMoving(board,projection);DesktopPages.Reconcile(board);}return;}
        grid.FoldersOnly=false;grid.Bounded=true;
        grid.Columns=projection.Columns;grid.Rows=projection.Rows;
        // Preserve every valid integer anchor; relocate only invalid placements.
        var old=grid.Placements;grid.Placements=new(StringComparer.Ordinal);
        foreach(var row in old)
        {
            var r=row.Value;
            if(r.X>=0&&r.Y>=0&&r.X+r.W<=grid.Columns&&r.Y+r.H<=grid.Rows&&grid.Placements.Values.All(other=>!Overlap(r,other)))grid.Placements[row.Key]=r;
        }
        DesktopGridActions.ReconcileBoard(board);grid.NativeInitialized=true;
    }
    public static bool Overlap(GridRect a,GridRect b)=>a.X<b.X+b.W&&a.X+a.W>b.X&&a.Y<b.Y+b.H&&a.Y+a.H>b.Y;
    public static void Collect(LayoutState state,Board board,string folderId,IEnumerable<DesktopItem> items)
    {
        var destination=board.Boxes.Single(box=>box.Id==folderId);
        var selected=items.DistinctBy(item=>item.Target,StringComparer.OrdinalIgnoreCase).ToArray();
        var targets=selected.Select(item=>item.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var source in state.Boards){source.RootItems.RemoveAll(item=>targets.Contains(item.Target));foreach(var box in source.Boxes)box.Items.RemoveAll(item=>targets.Contains(item.Target));}
        LayoutActions.Add(destination,selected);
        foreach(var source in state.Boards)DesktopGridActions.ReconcileBoard(source);
    }
}
