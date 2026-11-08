namespace MagicCommunity;
public static class DesktopFolderOrder
{
    public static void Reorder(Board board,string folder,string[] keys,string anchor,string target,int? insertionIndex=null)
    {
        _=DesktopGroupBridgeActions.Resolve(board,keys,folder);
        if(!keys.Contains(anchor))throw new LayoutEditException("拖动锚点已变化。");
        var list=board.Boxes.Single(b=>b.Id==folder).Items;int index=list.FindIndex(i=>DesktopGridActions.ItemKey(i)==target);
        if(index<0)throw new LayoutEditException("目标项目已变化。");
        if(insertionIndex.HasValue){
            var members=keys.ToHashSet(StringComparer.Ordinal);var selected=list.Where(i=>members.Contains(DesktopGridActions.ItemKey(i))).ToList();
            if(insertionIndex.Value<0||insertionIndex.Value>list.Count-selected.Count)throw new LayoutEditException("插入位置已变化。");
            list.RemoveAll(i=>members.Contains(DesktopGridActions.ItemKey(i)));list.InsertRange(insertionIndex.Value,selected);return;
        }
        if(keys.Contains(target))return;
        if(keys.Length==1){int source=list.FindIndex(i=>DesktopGridActions.ItemKey(i)==anchor);(list[source],list[index])=(list[index],list[source]);}
        else{var members=keys.ToHashSet(StringComparer.Ordinal);var selected=list.Where(i=>members.Contains(DesktopGridActions.ItemKey(i))).ToList();int insert=list.Take(index).Count(i=>!members.Contains(DesktopGridActions.ItemKey(i)));list.RemoveAll(i=>members.Contains(DesktopGridActions.ItemKey(i)));list.InsertRange(insert,selected);}
    }
}
