using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// Run explicitly through an isolated command entrypoint, never on application
// startup. Every actual transfer/recycle target is created below this fixture.
public static class DesktopMultiSelectionTests
{
    public static object Run(string root,bool recycle=false)
    {
        string fixture=Path.Combine(root,"evidence","desktop-feedback",DateTime.Now.ToString("yyyy-MM-dd"),"multiselect-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<object>();var timings=new List<object>();
        void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        using var host=new DesktopMenuHost(root);
        JsonElement Call(string route,string id,string[]? targets=null,int[]? path=null)
        {
            using var body=JsonDocument.Parse(JsonSerializer.Serialize(new{requestId=id,path=path??[]}));var watch=System.Diagnostics.Stopwatch.StartNew();
            var result=host.Route(route,body.RootElement,CancellationToken.None,targets:targets).GetAwaiter().GetResult();timings.Add(new{route,milliseconds=watch.Elapsed.TotalMilliseconds});return JsonSerializer.SerializeToElement(result);
        }
        string left=Path.Combine(fixture,"user-parent"),right=Path.Combine(fixture,"public-parent");Directory.CreateDirectory(left);Directory.CreateDirectory(right);
        string first=Path.Combine(left,"first.txt"),second=Path.Combine(left,"second.txt"),other=Path.Combine(right,"third.txt");
        foreach(string path in new[]{first,second,other})File.WriteAllText(path,"Owned multi-selection menu fixture");
        var wire=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        foreach(var selection in new[]{new[]{first,second},new[]{first,other}})
        {
            string id=Guid.NewGuid().ToString("N");var opened=Call("/desktop-context-open",id,selection);
            var rows=opened.GetProperty("more").Deserialize<DesktopContextMenu.Entry[]>(wire)!;
            Check((selection[1]==second?"same":"cross")+" parent menu retains selection and native common commands",opened.GetProperty("selectionCount").GetInt32()==2&&new[]{"copy","cut","delete","properties"}.All(key=>rows.Any(row=>row.Key==key&&!row.Disabled)));
            Check("no single-item rename is advertised for multiple items",opened.GetProperty("renameName").ValueKind==JsonValueKind.Null&&rows.Where(row=>row.Key=="rename").All(row=>row.Disabled));
            Call("/menu-dismiss",id);
        }
        var payloads=ShellClipboardCommands.TestSelectionPayloads(Path.Combine(fixture,"payloads"));
        if(recycle)
        {
            // Explicit opt-in only. These uniquely owned files are sent to the
            // real Recycle Bin via one aggregate Shell Delete command.
            string id=Guid.NewGuid().ToString("N");var opened=Call("/desktop-context-open",id,[first,other]);
            var command=opened.GetProperty("more").Deserialize<DesktopContextMenu.Entry[]>(wire)!.Single(row=>row.Key=="delete"&&!row.Disabled);
            Call("/desktop-context-invoke",id,path:[command.Index]);Check("one native Delete handles both parents and leaves unselected sibling",!File.Exists(first)&&!File.Exists(other)&&File.Exists(second));
        }
        return new{allPass=true,checks,timings,payloads,fixture,recycleInvoked=recycle,systemClipboardWritten=false,manualRequired=new[]{"Ctrl/marquee selection and desktop rendering","copy/cut/paste on the current build","merged multi-file Properties window","mixed real user/public desktop selection and third-party extension behavior"},scope="Bounded temporary Shell menu workers; same/cross-parent aggregate menus plus actual in-memory Shell data-object transfers of owned fixtures only"};
    }
}
