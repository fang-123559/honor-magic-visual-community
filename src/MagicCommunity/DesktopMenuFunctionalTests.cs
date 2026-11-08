using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopMenuFunctionalTests
{
    public static object Run(string root)
    {
        string fixture=Path.Combine(root,"evidence","desktop-feedback","2026-10-17","file-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<object>();var timings=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        using var host=new DesktopMenuHost(root);
        JsonElement Call(string route,string id,string? target=null,string? destination=null,string? action=null,int[]? path=null,bool background=false)
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();using var body=JsonDocument.Parse(JsonSerializer.Serialize(new{requestId=id,action,path=path??[]}));var task=host.Route(route,body.RootElement,CancellationToken.None,target,destination,background);
            if(!task.IsCompleted){var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;var frame=new System.Windows.Threading.DispatcherFrame();_=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);}
            var result=JsonSerializer.SerializeToElement(task.GetAwaiter().GetResult());timings.Add(new{route,action,milliseconds=watch.Elapsed.TotalMilliseconds});return result;
        }
        var wire=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        string source=Path.Combine(fixture,"A-"+Guid.NewGuid().ToString("N")+".txt"),folder=Path.Combine(fixture,"B");File.WriteAllText(source,"Magic isolated functional fixture");Directory.CreateDirectory(folder);
        string id=Guid.NewGuid().ToString("N");var fileMenu=Call("/desktop-context-open",id,source);
        var rows=fileMenu.GetProperty("more").Deserialize<DesktopContextMenu.Entry[]>(wire)!;
        Check("file menu exposes real copy cut delete rename properties",new[]{"copy","cut","delete","rename","properties"}.All(key=>rows.Any(row=>row.Key==key)));
        Call("/menu-dismiss",id);
        // Clipboard is shared with the user. Do not overwrite it in unattended
        // regression tests; copy/cut/paste require manual acceptance.
        id=Guid.NewGuid().ToString("N");var directoryMenu=Call("/desktop-context-open",id,folder);Check("folder menu has native extensions beyond old three entries",directoryMenu.GetProperty("more").GetArrayLength()>6);Call("/menu-dismiss",id);
        string destination=Path.Combine(folder,Path.GetFileName(source));Call("/desktop-file-operation",Guid.NewGuid().ToString("N"),source,destination,"move");
        Check("actual A to B move changes filesystem",!File.Exists(source)&&File.ReadAllText(destination)=="Magic isolated functional fixture");
        string renamed=Path.Combine(folder,"renamed-"+Guid.NewGuid().ToString("N")+".txt");Call("/desktop-file-operation",Guid.NewGuid().ToString("N"),destination,renamed,"rename");
        Check("actual rename changes filesystem",!File.Exists(destination)&&File.Exists(renamed));
        string collision=Path.Combine(folder,"collision.txt");File.WriteAllText(collision,"preserve");bool refused=false;
        try{DesktopFileChanges.Prepare(fixture,"rename",renamed,null,"collision.txt");}catch(LayoutEditException){refused=true;}
        Check("collision rejected without overwriting",refused&&File.ReadAllText(collision)=="preserve"&&File.Exists(renamed));
        var state=new LayoutState();var board=new Board{Id="fixture",RootItems=[new(){Name="old",Target=renamed}],Grid=new(){Columns=4,Rows=4}};state.Boards.Add(board);board.Grid.Placements[DesktopGridActions.ItemKey(board.RootItems[0])]=new(1,1,1,1);
        string final=Path.Combine(folder,"final.txt");var intent=DesktopFileChanges.Prepare(fixture,"rename",renamed,null,"final.txt");Call("/desktop-file-operation",intent.Id,renamed,final,"rename");
        var store=new StateStore(Path.Combine(fixture,"data"));DesktopFileChanges.Recover(fixture,store,state);
        Check("durable rename intent preserves virtual reference and grid position",state.Boards[0].RootItems[0].Target==final&&state.Boards[0].Grid!.Placements[DesktopGridActions.ItemKey(state.Boards[0].RootItems[0])]==new GridRect(1,1,1,1)&&Directory.GetFiles(Path.Combine(fixture,"data","shell-changes")).Length==0);
        string recycledName="magic-recycle-fixture-"+Guid.NewGuid().ToString("N")+".txt",recycled=Path.Combine(fixture,recycledName);File.WriteAllText(recycled,"Isolated recycle verification");
        Call("/desktop-file-operation",Guid.NewGuid().ToString("N"),recycled,null,"recycle");Check("recycle removes isolated source",!File.Exists(recycled));
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;object? recycleFolder=null,recycleItems=null;bool found=false;
        try{recycleFolder=shell.NameSpace(10);recycleItems=((dynamic)recycleFolder!).Items();foreach(object item in (System.Collections.IEnumerable)recycleItems){try{if(((string)((dynamic)item).Name).StartsWith(Path.GetFileNameWithoutExtension(recycledName),StringComparison.Ordinal))found=true;}finally{Marshal.ReleaseComObject(item);}}}
        finally{if(recycleItems is not null)Marshal.ReleaseComObject(recycleItems);if(recycleFolder is not null)Marshal.ReleaseComObject(recycleFolder);Marshal.ReleaseComObject(shell);}
        Check("recycled fixture is present in Windows Recycle Bin",found);
        {
            id=Guid.NewGuid().ToString("N");var rootMenu=Call("/desktop-context-open",id,fixture,background:true);var list=rootMenu.GetProperty("more").Deserialize<DesktopContextMenu.Entry[]>(wire)!;var create=list.Single(row=>row.Key=="new");
            var children=Call("/desktop-context-items",id,path:[create.Index]).GetProperty("items").Deserialize<DesktopContextMenu.Entry[]>(wire)!;Check("New submenu images have real type icons",children.Where(row=>!row.Separator).All(row=>row.Icon is not null));
            Check("New folder icon preserves transparent pixels",HasAlpha(children[0].Icon!));
            int folderIndex=children.First(row=>!row.Separator).Index;Call("/desktop-context-invoke",id,path:[create.Index,folderIndex]);
            Check("native New Folder actually creates in isolated target",Directory.GetDirectories(fixture).Any(path=>Path.GetFileName(path).StartsWith("新建文件夹",StringComparison.Ordinal)||Path.GetFileName(path).StartsWith("New folder",StringComparison.OrdinalIgnoreCase)));
            id=Guid.NewGuid().ToString("N");var textRoot=Call("/desktop-context-open",id,fixture,background:true).GetProperty("more").Deserialize<DesktopContextMenu.Entry[]>(wire)!;create=textRoot.Single(row=>row.Key=="new");
            children=Call("/desktop-context-items",id,path:[create.Index]).GetProperty("items").Deserialize<DesktopContextMenu.Entry[]>(wire)!;
            var text=children.Single(row=>row.Label.StartsWith("文本文档",StringComparison.Ordinal)||row.Label.StartsWith("Text Document",StringComparison.OrdinalIgnoreCase));Call("/desktop-context-invoke",id,path:[create.Index,text.Index]);
            Check("native New Text Document actually creates a text file",Directory.GetFiles(fixture,"*.txt").Any(path=>Path.GetFileName(path).StartsWith("新建",StringComparison.Ordinal)||Path.GetFileName(path).StartsWith("New ",StringComparison.OrdinalIgnoreCase)));
        }
        var bin=new DesktopItem{IsVirtual=true,Target="::{645FF040-5081-101B-9F08-00AA002F954E}"};board.RootItems.Add(bin);board.Grid!.Placements[DesktopGridActions.ItemKey(bin)]=new(2,1,1,1);
        var real=new DesktopItem{Target=folder,IsFolder=true};board.RootItems.Add(real);board.Grid.Placements[DesktopGridActions.ItemKey(real)]=new(3,1,1,1);
        Check("cross-screen canvas hit resolves Recycle Bin and real folder",ReferenceEquals(DesktopFileDrop.Hit(board,200,100),bin)&&ReferenceEquals(DesktopFileDrop.Hit(board,300,100),real)&&DesktopFileDrop.Hit(board,287,100) is null);
        int beforeFolders=Directory.GetDirectories(fixture).Length,beforeText=Directory.GetFiles(fixture,"*.txt").Length;
        Call("/desktop-create-item",Guid.NewGuid().ToString("N"),fixture,action:"folder");Call("/desktop-create-item",Guid.NewGuid().ToString("N"),fixture,action:"text");
        Check("fast Windows New Folder and Text paths create real distinct items",Directory.GetDirectories(fixture).Length==beforeFolders+1&&Directory.GetFiles(fixture,"*.txt").Length==beforeText+1);
        return new{allPass=true,checks,timings,fixture,manualRequired=new[]{"copy/cut/paste on current build", "properties/open dialogs", "third-party extension invocation"},scope="Real Shell menus, file operations and native creation on isolated project fixtures; no user desktop files moved or recycled; timings are RPC completion not physical input-to-frame latency"};
    }
    private static bool HasAlpha(string uri)
    {
        using var stream=new MemoryStream(Convert.FromBase64String(uri[(uri.IndexOf(',')+1)..]));var frame=System.Windows.Media.Imaging.BitmapDecoder.Create(stream,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var bitmap=new System.Windows.Media.Imaging.FormatConvertedBitmap(frame,System.Windows.Media.PixelFormats.Bgra32,null,0);var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return Enumerable.Range(0,pixels.Length/4).Any(index=>pixels[index*4+3]==0);
    }
}
