using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopCreationTests
{
    public static object Run(string root)
    {
        string fixture=Path.Combine(root,"artifacts","desktop-creation-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var results=new List<object>();
        void Check(string name,Action action){action();results.Add(new{name,pass=true});}
        void Expect(bool pass,string message){if(!pass)throw new InvalidDataException(message);}
        var projection=new AccessoryProjection(14,8,96,96,1,10,8);
        Board Board()=>new(){Id="one",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}};
        Check("new virtual folder prefers the requested full footprint",()=>{
            var board=Board();DesktopCreation.CreateFolder(board,projection,(6,4));
            Expect(board.Grid!.Placements[DesktopGridActions.FolderKey(board.Boxes[0])]==new GridRect(6,4,2,2),"preferred folder anchor lost");
        });
        Check("occupied footprint falls back without shifting its neighbour",()=>{
            var board=Board();DesktopCreation.CreateFolder(board,projection,(5,3));var old=board.Grid!.Placements.Values.Single();
            DesktopCreation.CreateFolder(board,projection,(6,4));
            Expect(board.Grid.Placements[DesktopGridActions.FolderKey(board.Boxes[0])]==old,"existing neighbour moved");
            Expect(board.Grid.Placements[DesktopGridActions.FolderKey(board.Boxes[1])]==new GridRect(0,0,2,2),"default fallback changed");DesktopGridActions.Validate(board);
        });
        Check("invalid or omitted preferred anchor preserves default creation",()=>{
            foreach(var point in new (int X,int Y)?[]{null,(-1,0),(9,7)}){var board=Board();DesktopCreation.CreateFolder(board,projection,point);Expect(board.Grid!.Placements.Values.Single()==new GridRect(0,0,2,2),"invalid anchor escaped grid");}
            using var body=JsonDocument.Parse("{\"x\":1}");try{DesktopCreation.Position(body.RootElement);throw new Exception("incomplete anchor accepted");}catch(InvalidDataException){}
        });
        Check("new physical item belongs to the originating secondary board",()=>{
            var primary=Board();var secondary=Board();secondary.Id="two";
            var created=new DesktopItem{Name="new",Target=Path.Combine(fixture,"owned.txt")};var unrelated=new DesktopItem{Name="unrelated",Target=Path.Combine(fixture,"other.txt")};primary.RootItems.AddRange([created,unrelated]);DesktopGridActions.ReconcileBoard(primary);var old=primary.Grid!.Placements[DesktopGridActions.ItemKey(unrelated)];
            var state=new LayoutState{Boards=[primary,secondary]};DesktopCreation.PlaceItem(state,secondary,created,projection,(7,5));
            Expect(primary.RootItems.SequenceEqual([unrelated])&&secondary.RootItems.SequenceEqual([created]),"incorrect item reassigned");Expect(primary.Grid.Placements[DesktopGridActions.ItemKey(unrelated)]==old,"unrelated anchor moved");Expect(secondary.Grid!.Placements[DesktopGridActions.ItemKey(created)]==new GridRect(7,5,1,1),"secondary anchor lost");DesktopGridActions.Validate(primary);DesktopGridActions.Validate(secondary);
        });
        Check("occupied physical-item anchor keeps the default on its destination",()=>{
            var board=Board();DesktopCreation.CreateFolder(board,projection,(6,4));var original=board.Grid!.Placements.Values.Single();
            var item=new DesktopItem{Name="new",Target=Path.Combine(fixture,"fallback.txt")};var state=new LayoutState{Boards=[board]};
            DesktopCreation.PlaceItem(state,board,item,projection,(7,5));
            Expect(board.Grid.Placements[DesktopGridActions.ItemKey(item)]==new GridRect(0,0,1,1),"physical-item fallback lost");Expect(board.Grid.Placements[DesktopGridActions.FolderKey(board.Boxes[0])]==original,"occupant shifted");DesktopGridActions.Validate(board);
        });
        Check("paged creation stays on the active page and retains menu origin",()=>{
            var board=Board();var state=new LayoutState{Boards=[board]};DesktopPages.Initialize(state,board,projection,true);DesktopPages.Add(board,false);
            DesktopCreation.CreateFolder(board,projection,(6,4));
            Expect(board.Grid!.Placements[DesktopGridActions.FolderKey(board.Boxes[0])]==new GridRect(6,4,2,2),"active-page anchor lost");
            Expect(board.Pages![0].Grid.Placements.Count==0,"creation leaked onto another page");DesktopPages.Validate(board);
        });
        Check("a full active page rejects virtual creation without adding pages or objects",()=>{
            var board=Board();var state=new LayoutState{Boards=[board]};DesktopPages.Initialize(state,board,projection,true);
            for(int i=0;i<80;i++)board.RootItems.Add(new(){Name="item"+i,Target=Path.Combine(fixture,"full"+i)});
            DesktopPages.Reconcile(board);
            try{DesktopCreation.CreateFolder(board,projection,(0,0));throw new Exception("full page accepted a folder");}catch(LayoutEditException){}
            Expect(board.Boxes.Count==0&&board.Pages!.Count==1,"failed creation changed the board");DesktopPages.Validate(board);
        });
        Check("physical creation removes stale placement from all pages before relocation",()=>{
            var board=Board();var state=new LayoutState{Boards=[board]};DesktopPages.Initialize(state,board,projection,true);
            var item=new DesktopItem{Name="new",Target=Path.Combine(fixture,"paged.txt")};board.RootItems.Add(item);DesktopPages.Reconcile(board);DesktopPages.Add(board,false);
            DesktopCreation.PlaceItem(state,board,item,projection,(7,5));
            Expect(board.Pages![0].Grid.Placements.Count==0&&board.Grid!.Placements[DesktopGridActions.ItemKey(item)]==new GridRect(7,5,1,1),"old page retained the new item");DesktopPages.Validate(board);
        });
        Check("physical overflow stays recoverable when the current page is full",()=>{
            var board=Board();var state=new LayoutState{Boards=[board]};DesktopPages.Initialize(state,board,projection,true);
            for(int i=0;i<80;i++)board.RootItems.Add(new(){Name="item"+i,Target=Path.Combine(fixture,"overflow"+i)});DesktopPages.Reconcile(board);
            var item=new DesktopItem{Name="new",Target=Path.Combine(fixture,"overflow-created.txt")};DesktopCreation.PlaceItem(state,board,item,projection,(0,0));
            Expect(board.Pages!.Count==2&&DesktopPages.Position(board,DesktopGridActions.ItemKey(item)) is not null,"physical file reference lost on overflow");DesktopPages.Validate(board);
        });
        Check("Shell callback returns exact paths including collision names",()=>{
            string[] paths=[ShellFileMove.Create(fixture,true)??"",ShellFileMove.Create(fixture,true)??"",ShellFileMove.Create(fixture,false)??"",ShellFileMove.Create(fixture,false)??""];
            Expect(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()==4,"collision-created results were not distinct");
            Expect(paths.All(path=>StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path),fixture)),"Shell result escaped fixture");Expect(paths.Take(2).All(Directory.Exists)&&paths.Skip(2).All(File.Exists),"exact creation result missing");
        });
        Check("native Shell New menu returns its exact file via INewMenuClient",()=>{
            using var menu=new DesktopContextMenu();string id=Guid.NewGuid().ToString("N");
            var opened=JsonSerializer.SerializeToElement(menu.Open(id,fixture,folderBackground:true));
            var wire=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
            var parent=opened.GetProperty("compact").Deserialize<DesktopContextMenu.Entry[]>(wire)!.Single(row=>row.Key=="new");
            var child=menu.Children(id,[parent.Index]).Single(row=>row.Key=="new-text");
            using(var lifetime=new ShellCommandLifetime())menu.Invoke(id,[parent.Index,child.Index]);
            Expect(menu.CreatedPath is { } path&&File.Exists(path)&&StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path),fixture),"native New callback did not identify its file");
        });
        Check("worker protocol preserves exact native New creation result",()=>{
            using var host=new DesktopMenuHost(root);string id=Guid.NewGuid().ToString("N");
            JsonElement Call(string route,int[]? path=null)
            {
                using var body=JsonDocument.Parse(JsonSerializer.Serialize(new{requestId=id,path=path??[]}));
                return JsonSerializer.SerializeToElement(host.Route(route,body.RootElement,CancellationToken.None,target:route=="/desktop-context-open"?fixture:null,folderBackground:route=="/desktop-context-open").GetAwaiter().GetResult());
            }
            var wire=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
            var parent=Call("/desktop-context-open").GetProperty("compact").Deserialize<DesktopContextMenu.Entry[]>(wire)!.Single(row=>row.Key=="new");
            var child=Call("/desktop-context-items",[parent.Index]).GetProperty("items").Deserialize<DesktopContextMenu.Entry[]>(wire)!.Single(row=>row.Key=="new-text");
            string? path=Call("/desktop-context-invoke",[parent.Index,child.Index]).GetProperty("createdPath").GetString();
            Expect(path is not null&&File.Exists(path)&&StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path),fixture),"worker lost exact creation result");
        });
        return new{allPass=true,results,fixture,scope="Owned layout and Shell creation fixtures only; no real desktop files, clipboard, settings or layout touched."};
    }
}
