namespace MagicCommunity;

public static class DesktopFileDrop
{
    public static bool IsRecycleBin(DesktopItem item)=>item.IsVirtual&&item.Target.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}",StringComparison.OrdinalIgnoreCase);
    public static DesktopItem? Hit(Board board,double x,double y,double cell=UnifiedDesktopGrid.Cell)
    {
        if(board.Grid is null)return null;
        foreach(var item in board.RootItems.Where(item=>IsRecycleBin(item)||item.IsFolder&&!item.IsVirtual))
        {
            if(!board.Grid.Placements.TryGetValue(DesktopGridActions.ItemKey(item),out var r))continue;
            if(x>=r.X*cell&&x<(r.X+r.W)*cell-UnifiedDesktopGrid.Inset*cell/96&&y>=r.Y*cell&&y<(r.Y+r.H)*cell-UnifiedDesktopGrid.Inset*cell/96)return item;
        }
        return null;
    }
}
