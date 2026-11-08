using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopClipboardTimingTests
{
    public static object Run(string root)
    {
        string fixture=Path.GetFullPath(Path.Combine(root,"evidence/desktop-feedback/2026-10-17","clipboard-prepare-"+Guid.NewGuid().ToString("N")+".txt"));File.WriteAllText(fixture,"Read-only menu preparation fixture");
        var samples=new List<object>();using var host=new DesktopMenuHost(root);
        object Call(string route,string? target,string action)
        {
            using var input=JsonDocument.Parse(JsonSerializer.Serialize(new{requestId=Guid.NewGuid().ToString("N"),action}));var watch=Stopwatch.StartNew();
            var result=host.Route(route,input.RootElement,CancellationToken.None,target).GetAwaiter().GetResult();double elapsed=watch.Elapsed.TotalMilliseconds;
            if(route=="/desktop-context-open")host.Route("/menu-dismiss",input.RootElement,CancellationToken.None).GetAwaiter().GetResult();
            var value=new{route,targetKind=target is null?"desktop background":"owned file",elapsedMs=elapsed,result};samples.Add(value);return value;
        }
        for(int i=0;i<3;i++)foreach(string? target in new[]{fixture,null}){Call("/desktop-context-open",target,"copy");Call("/desktop-clipboard-prepare",target,target is null?"paste":"copy");}
        return new{allPass=true,samples,clipboardWritten=false,scope="Compare full menu query+icons with native data/drop object binding only. No Copy/Cut/Paste execution or clipboard content access. RPC times include worker startup; not mouse-to-frame acceptance"};
    }
}
