using System.Diagnostics;
using System.IO;

namespace MagicCommunity;

internal static class ApplicationSearchFiles
{
    internal static string[] Read(IEnumerable<string> roots,CancellationToken cancellation)
    {
        var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending=new Queue<(string Path,int Depth)>();
        foreach(string root in roots.Where(Directory.Exists))pending.Enqueue((Path.GetFullPath(root),0));
        int entries=0;var clock=Stopwatch.StartNew();
        while(pending.TryDequeue(out var directory)&&entries<12000&&found.Count<2048&&clock.ElapsedMilliseconds<1500){
            cancellation.ThrowIfCancellationRequested();
            if(!visited.Add(directory.Path))continue;
            try{
                if((File.GetAttributes(directory.Path)&FileAttributes.ReparsePoint)!=0)continue;
                foreach(string path in Directory.EnumerateFileSystemEntries(directory.Path)){
                    cancellation.ThrowIfCancellationRequested();
                    if(++entries>12000||found.Count>=2048||clock.ElapsedMilliseconds>=1500)break;
                    var flags=File.GetAttributes(path);
                    if((flags&(FileAttributes.ReparsePoint|FileAttributes.Hidden|FileAttributes.System))!=0)continue;
                    if((flags&FileAttributes.Directory)!=0){if(directory.Depth<4)pending.Enqueue((path,directory.Depth+1));continue;}
                    if(Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".appref-ms")found.Add(path);
                }
            }catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){}
        }
        return found.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static bool Matches(string name,string target,string query)=>name.Contains(query,StringComparison.CurrentCultureIgnoreCase)||Path.GetFileName(target).Contains(query,StringComparison.CurrentCultureIgnoreCase);
}
