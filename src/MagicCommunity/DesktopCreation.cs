using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopCreation
{
    public static (int X,int Y)? Position(JsonElement body)
    {
        bool hasX=body.TryGetProperty("x",out var x),hasY=body.TryGetProperty("y",out var y);
        if(hasX!=hasY)throw new InvalidDataException("新建位置不完整。");
        if(!hasX)return null;
        if(x.ValueKind==JsonValueKind.Null&&y.ValueKind==JsonValueKind.Null)return null;
        int column=x.GetInt32(),row=y.GetInt32();
        if(column is <-128 or >128||row is <-128 or >128)throw new InvalidDataException("新建位置无效。");
        return(column,row);
    }
    public static bool TryPlace(Board board,string key,(int X,int Y)? point)
    {
        if(point is not { } p)return false;
        var grid=board.Grid??throw new LayoutEditException("桌面网格尚未就绪。");
        if(!grid.Placements.TryGetValue(key,out var original))return false;
        var next=original with{X=p.X,Y=p.Y};
        if(!DesktopGridActions.CanFit(grid,next,key)){
            // Stable nearest-cell search; ties follow normal row-major order.
            var candidates=from y in Enumerable.Range(0,grid.Rows) from x in Enumerable.Range(0,grid.Columns)
                orderby (long)(x-p.X)*(x-p.X)+(long)(y-p.Y)*(y-p.Y),y,x select original with{X=x,Y=y};
            next=candidates.FirstOrDefault(candidate=>DesktopGridActions.CanFit(grid,candidate,key));
            if(next is null)return false;
        }
        grid.Placements[key]=next;return true;
    }
    public static void CreateFolder(Board board,AccessoryProjection projection,(int X,int Y)? point)
    {
        DesktopGridActions.Validate(board);
        var slot=board.Pages is null?null:DesktopGridActions.ReserveSpace(board,2,2);
        var box=new Box{Name="新文件夹 "+(board.Boxes.Count+1)};board.Boxes.Add(box);
        if(slot is not null)board.Grid!.Placements.Add(DesktopGridActions.FolderKey(box),slot);
        UnifiedDesktopGrid.Ensure(board,projection);
        TryPlace(board,DesktopGridActions.FolderKey(box),point);
    }
    public static void PlaceItems(LayoutState state,Board destination,DesktopItem[] items,AccessoryProjection projection,(int X,int Y)? point)
    {
        foreach(var item in items)foreach(var board in state.Boards){board.RootItems.RemoveAll(row=>StringComparer.OrdinalIgnoreCase.Equals(row.Target,item.Target));DesktopPages.Forget(board,DesktopGridActions.ItemKey(item));}
        destination.RootItems.AddRange(items);
        foreach(var board in state.Boards)if(board!=destination)DesktopGridActions.ReconcileBoard(board);
        UnifiedDesktopGrid.Ensure(destination,projection);
        // Vacate this batch's provisional auto-placements before choosing the
        // first anchor, so another freshly pasted file cannot occupy it.
        foreach(var item in items)DesktopPages.Forget(destination,DesktopGridActions.ItemKey(item));
        var cursor=point;
        foreach(var item in items){string key=DesktopGridActions.ItemKey(item);var grid=destination.Grid!;var slot=DesktopGridActions.FindFree(grid,1,1);
            if(slot is null){destination.Unplaced[key]=new(cursor?.X??0,cursor?.Y??0,1,1);continue;}
            grid.Placements[key]=slot;TryPlace(destination,key,cursor);var placed=grid.Placements[key];int index=placed.Y*grid.Columns+placed.X+1;cursor=(index%grid.Columns,index/grid.Columns);
        }
        DesktopGridActions.ReconcileBoard(destination);
    }
    public static void PlaceItem(LayoutState state,Board destination,DesktopItem item,AccessoryProjection projection,(int X,int Y)? point)
    {
        string key=DesktopGridActions.ItemKey(item);
        foreach(var board in state.Boards)
        {
            board.RootItems.RemoveAll(row=>StringComparer.OrdinalIgnoreCase.Equals(row.Target,item.Target));
            DesktopPages.Forget(board,key);
        }
        destination.RootItems.Add(item);
        foreach(var board in state.Boards)if(board!=destination)DesktopGridActions.ReconcileBoard(board);
        UnifiedDesktopGrid.Ensure(destination,projection);
        TryPlace(destination,key,point);
    }
}
