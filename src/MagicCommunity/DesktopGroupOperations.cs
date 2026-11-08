namespace MagicCommunity;

// Group moves use the accepted grid geometry. Nothing is reconciled, grown or
// displaced here: one shared delta, all validation first, then one commit.
public static class DesktopGroupOperations
{
    public static void Place(Board board,IEnumerable<string> keys,string anchorKey,int x,int y)
    {
        var grid=board.Grid??throw new LayoutEditException("桌面网格尚未就绪。");
        string[] selected=keys.ToArray();
        if(selected.Length is <1 or >ShellDesktop.MaximumSelection||selected.Any(string.IsNullOrEmpty))throw new LayoutEditException("请选择 1 至 256 个桌面项目。");
        var moved=selected.ToHashSet(StringComparer.Ordinal);
        if(moved.Count!=selected.Length||!moved.Contains(anchorKey))throw new LayoutEditException("拖动选择或锚点已变化，请重新选择。");
        if(!grid.Placements.TryGetValue(anchorKey,out var anchor))throw new LayoutEditException("拖动锚点已变化，请刷新。");
        long deltaX=(long)x-anchor.X,deltaY=(long)y-anchor.Y;
        var next=new Dictionary<string,GridRect>(StringComparer.Ordinal);
        foreach(string key in selected)
        {
            if(!grid.Placements.TryGetValue(key,out var old))throw new LayoutEditException("所选项目已变化，请刷新。");
            long left=old.X+deltaX,top=old.Y+deltaY;
            if(left<0||top<0||left>grid.Columns-old.W||top>grid.Rows-old.H)throw new LayoutEditException("整组选中项目超出网格，原位置已保留。");
            next.Add(key,old with{X=(int)left,Y=(int)top});
        }
        foreach(var candidate in next)
        {
            if(grid.Placements.Any(row=>!moved.Contains(row.Key)&&UnifiedDesktopGrid.Overlap(candidate.Value,row.Value)))throw new LayoutEditException("整组选中项目与其它图标或文件夹重叠，原位置已保留。");
        }
        // Cross-screen and folder-out callers may stage selected source slots
        // in a destination board. Those old slots can be outside this screen
        // or overlap its neighbours; only the complete proposed board matters.
        // Copy the whole board so validation sees every page's membership.
        var proposed=System.Text.Json.JsonSerializer.Deserialize<Board>(System.Text.Json.JsonSerializer.Serialize(board,StateStore.Json),StateStore.Json)!;
        foreach(var row in next)proposed.Grid!.Placements[row.Key]=row.Value;
        DesktopGridActions.Validate(proposed);
        // The complete final board is valid. A translation preserves every
        // selected pair's separation and each folder's dimensions.
        foreach(var row in next)grid.Placements[row.Key]=row.Value;
    }
}
