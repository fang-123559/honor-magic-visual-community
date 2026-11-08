using System.IO;

namespace MagicCommunity;

// A refresh can expose an empty/partial Explorer view before it is rebuilt.
// Reject that frame; never interpret it as authoritative deletion of positions.
public static class DesktopEnumerationGuard
{
    public static string[] ReadPhysicalTargets(IEnumerable<string> roots)
    {
        var targets=new List<string>();
        try
        {
            foreach(string root in roots.Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(!Directory.Exists(root))continue;
                foreach(string path in Directory.EnumerateFileSystemEntries(root))
                {
                    try{if((File.GetAttributes(path)&FileAttributes.Hidden)==0)targets.Add(Path.GetFullPath(path));}
                    catch(FileNotFoundException){/* A concurrent real deletion is checked by the next view event. */}
                    catch(DirectoryNotFoundException){}
                }
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){throw new DesktopEnumerationUnavailableException("桌面目录快照尚不可用。",error);}
        return targets.ToArray();
    }
    public static void Validate(IEnumerable<string> rawTargets,IEnumerable<string> visibleTargets,IEnumerable<string> physicalTargets)
    {
        var raw=rawTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);var visible=visibleTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);var physical=physicalTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!visible.IsSubsetOf(raw)||!physical.IsSubsetOf(raw)||!physical.IsSubsetOf(visible))
            throw new DesktopEnumerationUnavailableException($"桌面视图仍在刷新：Shell {raw.Count}，视图 {visible.Count}，文件系统 {physical.Count}；保留现有布局。");
    }
}
public sealed class DesktopEnumerationUnavailableException(string message,Exception? inner=null):Exception(message,inner);
