using System.IO;

namespace MagicCommunity;

public sealed class DesktopPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DesktopGrid Grid { get; set; } = new();
}

// Page membership belongs to placements, while file references, folders and
// card instances remain board-wide. Switching pages never duplicates objects.
public static class DesktopPages
{
    public const int MaximumPages=128;
    public static IEnumerable<DesktopGrid> Grids(Board board)=>board.Pages is null?(board.Grid is {} grid?[grid]:[]):board.Pages.Select(page=>page.Grid);
    public static void Forget(Board board,string key){foreach(var grid in Grids(board))grid.Placements.Remove(key);board.Unplaced.Remove(key);}
    public static GridRect? Position(Board board,string key)=>Grids(board).Select(grid=>grid.Placements.GetValueOrDefault(key)).FirstOrDefault(rect=>rect is not null)??board.Unplaced.GetValueOrDefault(key);
    private static DesktopGrid Empty(DesktopGrid grid)=>new(){Columns=grid.Columns,Rows=grid.Rows,NativeInitialized=true,Bounded=true};
    public static void Initialize(LayoutState state,Board board,AccessoryProjection projection,bool primary)
    {
        if(board.Pages is null)
        {
            var grid=board.Grid??new();
            var first=new DesktopPage{Grid=grid};board.Pages=[first];board.ActivePageId=first.Id;board.Paged=primary;
        }
        state.SchemaVersion=Math.Max(6,state.SchemaVersion);
        foreach(var grid in Grids(board)){grid.Columns=projection.Columns;grid.Rows=projection.Rows;grid.FoldersOnly=false;grid.NativeInitialized=true;grid.Bounded=true;}
        Reconcile(board);
    }
    public static void Reconcile(Board board)
    {
        var pages=board.Pages!;
        if(pages.Count==0||pages.Count>MaximumPages||pages.Any(page=>page is null||page.Grid is null)||!pages.Any(page=>page.Id==board.ActivePageId))throw new InvalidDataException("桌面分页结构无效。");
        foreach(var box in board.Boxes.Where(box=>box.Columns==2&&box.Rows==4)){box.Columns=4;box.Rows=2;}
        var expected=DesktopGridActions.Expected(board);
        var prior=new Dictionary<string,(DesktopPage? Page,GridRect Rect)>(StringComparer.Ordinal);
        foreach(var page in pages)foreach(var row in page.Grid.Placements)prior.TryAdd(row.Key,(page,row.Value));
        foreach(var row in board.Unplaced)prior.TryAdd(row.Key,(null,row.Value));
        board.Unplaced.Clear();
        var placed=new HashSet<string>(StringComparer.Ordinal);
        // Preserve every still-valid anchor before looking for replacement slots.
        foreach(var page in pages)
        {
            var old=page.Grid.Placements;page.Grid.Placements=new(StringComparer.Ordinal);
            foreach(var row in old)
                if(expected.TryGetValue(row.Key,out var size)&&row.Value is {} rect&&rect.W==size.W&&rect.H==size.H&&!placed.Contains(row.Key)&&DesktopGridActions.CanFit(page.Grid,rect))
                {page.Grid.Placements.Add(row.Key,rect);placed.Add(row.Key);}
        }
        foreach(var row in expected.Where(row=>!placed.Contains(row.Key)).OrderByDescending(row=>row.Value.W*row.Value.H))
        {
            var old=prior.GetValueOrDefault(row.Key);
            var candidates=new[]{old.Page,pages.Single(page=>page.Id==board.ActivePageId)}.Where(page=>page is not null).Cast<DesktopPage>().Concat(pages).Distinct().ToArray();
            DesktopPage? target=null;GridRect? slot=null;
            foreach(var page in candidates)if((slot=DesktopGridActions.FindFree(page.Grid,row.Value.W,row.Value.H)) is not null){target=page;break;}
            if(target is null&&board.Paged&&pages.Count<MaximumPages&&row.Value.W<=pages[0].Grid.Columns&&row.Value.H<=pages[0].Grid.Rows)
            {target=new(){Grid=Empty(pages[0].Grid)};pages.Add(target);slot=new(0,0,row.Value.W,row.Value.H);}
            if(target is not null)target.Grid.Placements[row.Key]=slot!;
            else board.Unplaced[row.Key]=new(old.Rect?.X??0,old.Rect?.Y??0,row.Value.W,row.Value.H);
        }
        Validate(board);
    }
    public static void Validate(Board board)
    {
        var pages=board.Pages!;var ids=new HashSet<string>(StringComparer.Ordinal);
        if(pages.Count is <1 or >MaximumPages||(!board.Paged&&pages.Count!=1)||board.Unplaced is null)throw new InvalidDataException("桌面分页数量无效。");
        var expected=DesktopGridActions.Expected(board);var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var page in pages)
        {
            if(page is null||string.IsNullOrWhiteSpace(page.Id)||!ids.Add(page.Id)||page.Grid is not {} grid||grid.Columns is <1 or >128||grid.Rows is <1 or >128||grid.FoldersOnly||!grid.NativeInitialized||grid.Placements is null)throw new InvalidDataException("桌面页网格无效。");
            if(grid.Columns!=pages[0].Grid.Columns||grid.Rows!=pages[0].Grid.Rows)throw new InvalidDataException("同一屏幕的页面尺寸不一致。");
            foreach(var row in grid.Placements)if(row.Value is not {} rect||!expected.TryGetValue(row.Key,out var size)||rect.W!=size.W||rect.H!=size.H||!seen.Add(row.Key)||!DesktopGridActions.CanFit(grid,rect,row.Key))throw new InvalidDataException("分页网格重叠、越界或重复。");
        }
        foreach(var row in board.Unplaced)if(row.Value is not {} rect||!expected.TryGetValue(row.Key,out var size)||size.W!=rect.W||size.H!=rect.H||!seen.Add(row.Key))throw new InvalidDataException("待放置项目无效。");
        if(!ids.Contains(board.ActivePageId??"")||seen.Count!=expected.Count)throw new InvalidDataException("分页占位与项目不一致。");
    }
    public static void OnSourcePage(Board board,string? page,Action action)
    {
        if(page is null||page==board.ActivePageId){action();return;}
        string active=board.ActivePageId??throw new LayoutEditException("来源页面已变化。");
        Select(board,page);
        try{action();}finally{Select(board,active);}
    }
    public static void Select(Board board,string id)
    {if(board.Pages?.Any(page=>page.Id==id)!=true)throw new LayoutEditException("目标页面已变化，请重试。");board.ActivePageId=id;}
    public static void Add(Board board,bool before,bool edge=false)
    {
        if(!board.Paged||board.Pages is null)throw new LayoutEditException("此屏幕使用单页桌面。");
        if(board.Pages.Count>=MaximumPages)throw new LayoutEditException("页面已达上限，请先整理空页。");
        var page=new DesktopPage{Grid=Empty(board.Grid!)};int index=edge?(before?0:board.Pages.Count):board.Pages.FindIndex(row=>row.Id==board.ActivePageId)+(before?0:1);
        board.Pages.Insert(index,page);board.ActivePageId=page.Id;
    }
    public static void Delete(Board board,string id)
    {
        var page=board.Pages?.SingleOrDefault(page=>page.Id==id)??throw new LayoutEditException("页面已变化。");
        if(!board.Paged||board.Pages!.Count<=1||page.Grid.Placements.Count!=0)throw new LayoutEditException("只能删除空页，且至少保留一页。");
        int index=board.Pages.IndexOf(page);board.Pages.Remove(page);
        if(board.ActivePageId==id)board.ActivePageId=board.Pages[Math.Min(index,board.Pages.Count-1)].Id;
    }
    public static void Drop(LayoutState state,Board board,string sourcePage,string destinationPage,string[] keys,string anchor,string? sourceFolder,string? destinationFolder,int x,int y)
    {
        if(board.Pages is null||keys.Length is <1 or >256||keys.Any(string.IsNullOrWhiteSpace)||keys.Distinct().Count()!=keys.Length||!keys.Contains(anchor))throw new LayoutEditException("拖动项目已变化。");
        var source=board.Pages.SingleOrDefault(page=>page.Id==sourcePage)??throw new LayoutEditException("来源页面已变化。");
        var target=board.Pages.SingleOrDefault(page=>page.Id==destinationPage)??throw new LayoutEditException("目标页面已变化。");
        if(board.ActivePageId!=destinationPage)throw new LayoutEditException("当前页面已变化，请重新拖动。");
        var expected=DesktopGridActions.Expected(board);
        if(keys.Any(key=>!expected.ContainsKey(key)&&sourceFolder is null))throw new LayoutEditException("拖动项目不存在。");
        DesktopItem[]? items=null;
        if(keys.All(key=>key.StartsWith("item:",StringComparison.Ordinal)))items=DesktopGroupBridgeActions.Resolve(board,keys,sourceFolder);
        else if(keys.Length!=1||sourceFolder is not null||destinationFolder is not null)throw new LayoutEditException("请选择单个卡片或文件夹。");
        if(sourceFolder is not null&&!source.Grid.Placements.ContainsKey("folder:"+sourceFolder))throw new LayoutEditException("来源文件夹不在来源页面。");
        if(sourceFolder is null&&keys.Any(key=>!source.Grid.Placements.ContainsKey(key)))throw new LayoutEditException("来源项目已离开该页面。");
        if(destinationFolder is not null)
        {
            if(items is null||!target.Grid.Placements.ContainsKey("folder:"+destinationFolder))throw new LayoutEditException("目标文件夹已变化。");
            UnifiedDesktopGrid.Collect(state,board,destinationFolder,items);return;
        }
        if(sourceFolder is not null&&items!.Any(item=>state.Boards.SelectMany(other=>other.Boxes).Where(box=>box.Id!=sourceFolder).SelectMany(box=>box.Items).Any(other=>StringComparer.OrdinalIgnoreCase.Equals(other.Target,item.Target))))
            throw new LayoutEditException("所选项目仍在其它文件夹中，请先移除重复引用。");
        int columns=Math.Min(4,Math.Min(target.Grid.Columns,(int)Math.Ceiling(Math.Sqrt(keys.Length))));
        var rects=keys.Select((key,i)=>sourceFolder is null?source.Grid.Placements[key]:new GridRect(i%columns,i/columns,1,1)).ToArray();
        var origin=rects[Array.IndexOf(keys,anchor)];
        var ignore=keys.ToHashSet(StringComparer.Ordinal);
        bool Fits(int cx,int cy)=>rects.All(rect=>{var r=rect with{X=cx+rect.X-origin.X,Y=cy+rect.Y-origin.Y};return r.X>=0&&r.Y>=0&&r.X+r.W<=target.Grid.Columns&&r.Y+r.H<=target.Grid.Rows&&target.Grid.Placements.All(row=>ignore.Contains(row.Key)||!UnifiedDesktopGrid.Overlap(r,row.Value));});
        var slot=Enumerable.Range(0,target.Grid.Rows).SelectMany(cy=>Enumerable.Range(0,target.Grid.Columns).Select(cx=>(X:cx,Y:cy))).OrderBy(point=>Math.Pow((double)point.X-x,2)+Math.Pow((double)point.Y-y,2)).FirstOrDefault(point=>Fits(point.Item1,point.Item2),(-1,-1));
        if(slot.Item1<0)throw new LayoutEditException("目标页面没有足够空位，原位置已保留。");
        if(sourceFolder is not null)foreach(var item in items!){board.Boxes.Single(box=>box.Id==sourceFolder).Items.Remove(item);board.RootItems.Add(item);}
        for(int i=0;i<keys.Length;i++){Forget(board,keys[i]);target.Grid.Placements[keys[i]]=rects[i] with{X=slot.Item1+rects[i].X-origin.X,Y=slot.Item2+rects[i].Y-origin.Y};}
        Validate(board);
    }
}
