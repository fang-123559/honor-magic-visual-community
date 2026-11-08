using System.IO;

namespace MagicCommunity;

// HTML File objects expose names, not trustworthy absolute paths. Resolve only exact,
// unique file names from the already enumerated native desktop; never guess by title.
public static class DesktopDrop
{
    public static DesktopItem[] Resolve(IEnumerable<DesktopItem> desktop,IReadOnlyList<string> names)
    {
        if(names.Count is <1 or >100)throw new LayoutEditException("一次请拖入 1–100 个桌面项目。");
        var catalog=desktop.Where(item=>!item.IsVirtual&&Path.IsPathFullyQualified(item.Target)).DistinctBy(item=>item.Target,StringComparer.OrdinalIgnoreCase).ToArray();
        var result=new List<DesktopItem>();
        foreach(string name in names)
        {
            if(string.IsNullOrWhiteSpace(name)||name.Length>260||name is "." or ".."||name.IndexOfAny(['\\','/',':','\0'])>=0)throw new LayoutEditException("拖入项目的名称无效，未保存引用。");
            var matches=catalog.Where(item=>StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(item.Target.TrimEnd(Path.DirectorySeparatorChar)),name)).ToArray();
            if(matches.Length!=1)throw new LayoutEditException(matches.Length>1?"用户桌面和公共桌面有同名项目，请在整理入口选择确切项目。":"未能确定拖入项目的原位置，请从 Windows 桌面拖入，或在整理入口加入。");
            if(!File.Exists(matches[0].Target)&&!Directory.Exists(matches[0].Target))throw new LayoutEditException("拖入的桌面项目已不存在，请刷新后重试。");
            result.Add(matches[0]);
        }
        return result.DistinctBy(item=>item.Target,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
