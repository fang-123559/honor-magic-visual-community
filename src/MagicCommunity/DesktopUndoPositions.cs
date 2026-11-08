using System.IO;

namespace MagicCommunity;

// Session-scoped Shell undo anchors. No writes, timer or layout schema change.
public sealed class DesktopUndoPositions
{
    private sealed record Anchor(string Board,string? Page,string? Folder,int Index,GridRect? Rect,long Age);
    private readonly Dictionary<string,Anchor> anchors=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pending=new(StringComparer.OrdinalIgnoreCase);
    private long age;
    private DateTime deadline;
    public void Capture(LayoutState state)
    {
        foreach(var board in state.Boards){
            foreach(var item in board.RootItems.Where(i=>!i.IsVirtual)){
                string key=DesktopGridActions.ItemKey(item);var page=board.Pages?.FirstOrDefault(p=>p.Grid.Placements.ContainsKey(key));
                var rect=page?.Grid.Placements.GetValueOrDefault(key)??(board.Pages is null?board.Grid?.Placements.GetValueOrDefault(key):null);
                if(rect is not null)anchors[item.Target]=new(board.Id,page?.Id,null,0,rect,++age);
            }
            foreach(var box in board.Boxes)for(int i=0;i<box.Items.Count;i++)if(!box.Items[i].IsVirtual)anchors[box.Items[i].Target]=new(board.Id,null,box.Id,i,null,++age);
        }
        foreach(var key in anchors.OrderByDescending(row=>row.Value.Age).Skip(1024).Select(row=>row.Key).ToArray()){anchors.Remove(key);pending.Remove(key);}
    }
    public void Begin(LayoutState state)
    {
        Capture(state);pending.Clear();
        foreach(var target in anchors.Keys)if(!File.Exists(target)&&!Directory.Exists(target))pending.Add(target);
        deadline=DateTime.UtcNow.AddSeconds(15);
    }
    public void Cancel()=>pending.Clear();
    public void Restore(LayoutState state,IReadOnlyList<DesktopItem> desktop,IReadOnlyList<Display> screens)
    {
        if(DateTime.UtcNow>deadline){pending.Clear();return;}
        foreach(var item in desktop.Where(i=>pending.Contains(i.Target)).ToArray()){
            pending.Remove(item.Target);if(!anchors.TryGetValue(item.Target,out var prior))continue;
            var board=state.Boards.FirstOrDefault(b=>b.Id==prior.Board&&screens.Any(s=>s.Id==b.Id));if(board is null)continue;
            var box=prior.Folder is null?null:board.Boxes.FirstOrDefault(b=>b.Id==prior.Folder);
            var grid=board.Pages is null?board.Grid:board.Pages.FirstOrDefault(p=>p.Id==prior.Page)?.Grid;
            if(prior.Folder is not null&&box is null)continue;
            if(prior.Folder is null&&(grid is null||prior.Rect is null||!DesktopGridActions.CanFit(grid,prior.Rect,DesktopGridActions.ItemKey(item))))continue;
            string key=DesktopGridActions.ItemKey(item);
            foreach(var current in state.Boards){current.RootItems.RemoveAll(i=>StringComparer.OrdinalIgnoreCase.Equals(i.Target,item.Target));foreach(var folder in current.Boxes)folder.Items.RemoveAll(i=>StringComparer.OrdinalIgnoreCase.Equals(i.Target,item.Target));DesktopPages.Forget(current,key);}
            if(box is not null){
                // An occupied list slot falls back to the folder's normal end.
                box.Items.Add(item);
            }else{board.RootItems.Add(item);grid!.Placements[key]=prior.Rect!;}
        }
    }
}
