using System.IO;
using System.Text;
using System.Text.Json;

namespace MagicCommunity;

public static class CustomCardTests
{
    public static object Run(string root)
    {
        string fixture=Path.GetFullPath(Path.Combine(root,"evidence/desktop-cards/2026-10-19/custom-fixture-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(fixture);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        byte[] Bytes(string id,string kind,object content,object? style=null)=>JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,id,title="Fixture",kind,sizes=new[]{new{columns=4,rows=2}},content,style=style??new{tone="light",align="center"}});
        var text=Bytes("com.test.text","text",new{body="<script>not executed</script>",bodySize="l",html="ignored"},new{tone="dark",align="left",css="ignored"});
        var definition=CustomCardLibrary.Parse("com.test.text",text);
        Check("text is preserved literally and unsupported keys cannot become HTML or CSS",definition.Custom!.Content.GetProperty("body").GetString()=="<script>not executed</script>"&&!definition.Custom.Content.TryGetProperty("html",out _)&&definition.Custom.Tone=="dark");
        void Refuse(string name,Action run){bool refused=false;try{run();}catch(Exception e)when(e is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException){refused=true;}Check(name,refused);}
        Refuse("directory identity mismatch rejected",()=>CustomCardLibrary.Parse("com.other",text));
        Refuse("oversized package rejected",()=>CustomCardLibrary.Parse("com.test.text",new byte[16385]));
        Refuse("unknown kind rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","script",new{body="x"})));
        Refuse("invalid calendar date rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","countdown",new{target="2026-02-30"})));
        Refuse("out of range time rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","countdown",new{target="24:00"})));
        Refuse("script URL rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","shortcut",new{items=new[]{new{label="X",type="url",target="javascript:alert(1)"}}})));
        Refuse("relative command path rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","shortcut",new{items=new[]{new{label="X",type="path",target="cmd.exe /c command"}}})));
        Refuse("device and UNC paths rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","shortcut",new{items=new[]{new{label="X",type="path",target=@"\\server\share"}}})));
        Refuse("alternate data stream path rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","shortcut",new{items=new[]{new{label="X",type="path",target=@"C:\test.txt:payload"}}})));
        Refuse("duplicate JSON keys rejected",()=>CustomCardLibrary.Parse("com.test.text",Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(text).Replace("\"schemaVersion\":1","\"schemaVersion\":1,\"schemaVersion\":1"))));
        Refuse("unsupported style values rejected",()=>CustomCardLibrary.Parse("com.test.bad",Bytes("com.test.bad","text",new{body="x"},new{tone="url(evil)",align="left"})));
        foreach(string target in new[]{"18:00","2026-10-20","2026-10-20 18:00"})Check("valid countdown target "+target,CustomCardLibrary.Parse("com.test.timer",Bytes("com.test.timer","countdown",new{target})).Custom!.Kind=="countdown");
        Check("clock uses a sanitized fixed declaration",CustomCardLibrary.Parse("com.test.clock",Bytes("com.test.clock","clock",new{hour12=false,showSeconds=false,script="ignored"})).Custom!.Content.EnumerateObject().Count()==2);
        Check("local folder and https link accepted without opening them",CustomCardLibrary.Parse("com.test.shortcut",Bytes("com.test.shortcut","shortcut",new{items=new[]{new{label="Folder",type="path",target=fixture},new{label="Web",type="url",target="https://example.com/"}}})).Custom!.Content.GetProperty("items").GetArrayLength()==2);
        var logs=new List<string>();string library=Path.Combine(fixture,"packages");var source=new CustomCardLibrary(library,(name,value)=>logs.Add(name));Check("missing library is read only and allocates no directory",source.Scan().Count==0&&!Directory.Exists(library));
        string package=Path.Combine(library,"com.test.text");Directory.CreateDirectory(package);File.WriteAllBytes(Path.Combine(package,"card.json"),text);string bad=Path.Combine(library,"com.bad.package");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,"card.json"),"{ broken");
        Check("one malformed package does not prevent a valid package",source.Scan().Count==1&&logs.Contains("custom_card_skipped"));
        Directory.CreateDirectory(Path.Combine(package,"unexpected","nested"));Check("extra nested directories are rejected",source.Scan().Count==0);Directory.Move(Path.Combine(package,"unexpected"),Path.Combine(fixture,"owned-extra-directory"));
        var state=new LayoutState{SchemaVersion=5,Boards=[new(){Id="a",Grid=new(){Columns=8,Rows=8,NativeInitialized=true}},new(){Id="b",Grid=new(){Columns=8,Rows=8,NativeInitialized=true}}]};var catalog=new DesktopCardCatalog([definition]);
        DesktopCards.Add(state,state.Boards[0],catalog,definition.Type,4,2,JsonSerializer.SerializeToElement(new{}),0,0);DesktopCards.Add(state,state.Boards[1],catalog,definition.Type,4,2,JsonSerializer.SerializeToElement(new{}),0,0);
        Check("instances counted across all boards",CustomCardLibrary.InstanceCount(state,definition.Type)==2);
        bool recycled=false;Refuse("layout save failure restores package before recycling",()=>source.Uninstall(definition,()=>throw new InvalidDataException("injected save failure"),_=>recycled=true));Check("failed layout leaves all instances and package intact",!recycled&&Directory.Exists(package)&&CustomCardLibrary.InstanceCount(state,definition.Type)==2);
        string retained=Path.Combine(fixture,"simulated-recycle");source.Uninstall(definition,()=>CustomCardLibrary.RemoveInstances(state,definition.Type),path=>{recycled=true;Directory.Move(path,retained);});
        Check("explicit uninstall removes all instances and stages only the selected package",recycled&&!Directory.Exists(package)&&Directory.Exists(retained)&&CustomCardLibrary.InstanceCount(state,definition.Type)==0&&Directory.Exists(bad)&&!Directory.EnumerateFiles(library,".pending-*.json").Any());
        Directory.CreateDirectory(package);File.WriteAllBytes(Path.Combine(package,"card.json"),text);bool pending=false;try{source.Uninstall(definition,()=>{},_=>throw new IOException("injected recycle failure"));}catch(LayoutEditException){pending=true;}
        Check("failed recycling keeps a committed recovery record and does not reinstall",pending&&source.Scan().Count==0&&Directory.EnumerateFiles(library,".pending-*.json").Count()==1);
        source.Recover(path=>Directory.Move(path,Path.Combine(fixture,"simulated-recycle-recovery")));Check("startup recovery finishes a committed recycle once",!Directory.EnumerateFiles(library,".pending-*.json").Any()&&Directory.Exists(Path.Combine(fixture,"simulated-recycle-recovery")));
        string stage=".pending-"+Guid.NewGuid().ToString("N"),staged=Path.Combine(library,stage);Directory.CreateDirectory(Path.Combine(staged,"com.test.text"));File.WriteAllBytes(Path.Combine(staged,"com.test.text","card.json"),text);File.WriteAllText(staged+".json",JsonSerializer.Serialize(new{Id="com.test.text",Stage=stage,Committed=false}));source.Recover(_=>throw new InvalidDataException("not expected"));Check("ambiguous interrupted uninstall restores package without deleting it",Directory.Exists(package)&&source.Scan().Count==1&&!File.Exists(staged+".json"));
        return new{allPass=true,checks,fixture,scope="Isolated package parsing, scan and failure-injected uninstall/recovery. Recycling is a retaining directory move in fixtures; no real custom package, clipboard or desktop layout is modified."};
    }
}
