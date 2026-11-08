using System.Globalization;
using System.IO;
using System.Text.Json;
namespace MagicCommunity;
public sealed partial class CompositionDesktopRuntime
{
    private readonly SemaphoreSlim fileMetadata=new(1,1);
    private async Task<object> FileHoverInfo(JsonElement request){
        var item=await ui.InvokeAsync(()=>Item(Text(request,"board"),Optional(request,"folder"),Text(request,"key")));
        string name=item.Name,path=item.Target;
        if(!item.IsVirtual&&Path.IsPathFullyQualified(path)&&!path.StartsWith(@"\\",StringComparison.Ordinal))name=Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if(name.Length>512)name=name[..512];string modified="不可用";
        if(item.IsVirtual||!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||!await fileMetadata.WaitAsync(0,stop.Token))return new{name,modified};
        // A timed-out filesystem call retains this sole permit until it ends;
        // moving the pointer can never create an unbounded set of stat workers.
        var read=Task.Run(()=>{
            try{var info=new FileInfo(path);DateTime time=info.LastWriteTime;if(time.Year>=1980)return time.ToString("yyyy/M/d HH:mm:ss",CultureInfo.InvariantCulture);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){}
            finally{fileMetadata.Release();}return "不可用";
        });
        try{modified=await read.WaitAsync(TimeSpan.FromMilliseconds(800),stop.Token);}catch(TimeoutException){}
        return new{name,modified};
    }
}
