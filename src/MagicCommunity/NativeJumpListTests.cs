using System.IO;
using System.Text.Json;
namespace MagicCommunity;
internal static class NativeJumpListTests{
    internal static async Task<object> Run(string root){
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        Check("application identity control characters rejected",!NativeJumpLists.ValidId("app\n")&&!NativeJumpLists.ValidId("app\0")&&!NativeJumpLists.ValidId(new string('a',513)));
        Check("network device and relative documents are outside Recent launch scope",!NativeJumpLists.LocalPath(@"\\host\file")&&!NativeJumpLists.LocalPath(@"\\?\C:\file")&&!NativeJumpLists.LocalPath(@"C:\file:stream")&&!NativeJumpLists.LocalPath("relative.docx"));
        Check("existing launch arguments preserve quoted whitespace and empty argument",NativeSystemUiSession.SplitArguments("--profile \"Profile 1\" \"\"").SequenceEqual(["--profile","Profile 1",""]));
        bool invalid=false;try{NativeSystemUiSession.SplitArguments("x\0y");}catch(InvalidDataException){invalid=true;}Check("embedded argument terminator refused",invalid);
        var timing=System.Diagnostics.Stopwatch.StartNew();using(var helper=new DesktopMenuHost(root,reuseQueries:false)){
            var body=JsonSerializer.SerializeToElement(new{requestId=Guid.NewGuid().ToString("N"),aumid="",shortcut="",executable="",window=0});
            var data=JsonSerializer.SerializeToElement(await helper.Route("/desktop-jump-list",body,CancellationToken.None));
            Check("missing application identity returns no section and no borrowed history",data.GetProperty("recent").GetArrayLength()==0&&data.GetProperty("aumid").GetString()=="");
        }
        using(var helper=new DesktopMenuHost(root,reuseQueries:false)){
            var body=JsonSerializer.SerializeToElement(new{requestId=Guid.NewGuid().ToString("N"),aumid="",shortcut="",executable="",window=0});bool refused=false;
            try{await helper.Route("/desktop-jump-open",body,CancellationToken.None,destination:"not-offered");}catch(InvalidOperationException){refused=true;}
            Check("package activation refuses a document that was not in Windows Recent",refused);
        }
        return new{allPass=true,checks,ownedWorkerReadMs=timing.Elapsed.TotalMilliseconds,scope="Owned disposable Shell worker, argument parsing and read-only empty application identity. No user history modified or documents opened."};
    }
    internal static async Task<object> Probe(string root,string appId){
        if(!NativeJumpLists.ValidId(appId))throw new InvalidDataException("Application identity invalid.");using var helper=new DesktopMenuHost(root,reuseQueries:false);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));var watch=System.Diagnostics.Stopwatch.StartNew();
        var body=JsonSerializer.SerializeToElement(new{requestId=Guid.NewGuid().ToString("N"),aumid=appId,shortcut="",executable="",window=0});
        try{var data=JsonSerializer.SerializeToElement(await helper.Route("/desktop-jump-list",body,deadline.Token));double firstMs=watch.Elapsed.TotalMilliseconds;
            using var second=new DesktopMenuHost(root,reuseQueries:false);
            var reread=JsonSerializer.SerializeToElement(await second.Route("/desktop-jump-list",body,deadline.Token));
            string[] Ids(JsonElement result,string name)=>result.GetProperty(name).EnumerateArray().Select(item=>item.GetProperty("id").GetString()??"").ToArray();
            var groups=new[]{"pinned","frequent","recent","tasks"}.ToDictionary(name=>name,name=>Ids(data,name));
            return new{available=true,source=data.GetProperty("source").GetString(),stableOrder=groups.All(group=>group.Value.SequenceEqual(Ids(reread,group.Key))),
                counts=groups.ToDictionary(group=>group.Key,group=>group.Value.Length),orderedIds=groups,
                shellMs=data.GetProperty("milliseconds").GetDouble(),roundTripMs=firstMs,scope="Read-only Windows destinations and registered tasks; hashed identities only, no names, paths or activation."};}
        catch(Exception e)when(e is not OutOfMemoryException){return new{available=false,error=e.GetType().Name,roundTripMs=watch.Elapsed.TotalMilliseconds,scope="Read-only query failed or timed out; no activation."};}
    }
}
