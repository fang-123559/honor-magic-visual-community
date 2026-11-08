using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopCardTests
{
    public static object Run(string root)
    {
        string fixture=Path.GetFullPath(Path.Combine(root,"evidence/desktop-cards/2026-10-19","fixture-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(fixture);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        NativeTodoDataTests.Run(root,Check);NativeHonorServiceTests.Run(fixture,Check);NativeNetworkPortalTests.Run(Check);NativeHandoffTests.Run(fixture,Check);
        var a=new DesktopItem{Name="A",Target=Path.Combine(fixture,"A.txt"),FromDesktop=true};var b=new DesktopItem{Name="B",Target=Path.Combine(fixture,"B.txt"),FromDesktop=true};var box=new Box{Id="existing-folder",Name="保留"};
        var state=new LayoutState{Boards=[new(){Id="a",Name="主屏",RootItems=[a,b],Boxes=[box],Grid=new(){Columns=10,Rows=8,NativeInitialized=true}},new(){Id="b",Name="副屏",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}}]};
        var board=state.Boards[0];board.Grid!.Placements=new(){[DesktopGridActions.ItemKey(a)]=new(0,0,1,1),[DesktopGridActions.ItemKey(b)]=new(1,0,1,1),[DesktopGridActions.FolderKey(box)]=new(5,4,2,2)};
        var store=new StateStore(Path.Combine(fixture,"state"));store.Save(state);byte[] original=File.ReadAllBytes(store.FilePath);var loaded=store.Load();
        Check("no-card layout stays version 4 and is not rewritten",loaded.SchemaVersion==4&&!store.NeedsMigration&&original.SequenceEqual(File.ReadAllBytes(store.FilePath))&&!JsonSerializer.Serialize(loaded,StateStore.Json).Contains("\"Cards\""));
        var catalog=new DesktopCardCatalog([new(){Type="fixture.basic",Title="Fixture",Sizes=[new(2,2),new(4,2),new(4,4)],DataSources=["fixture.source"]}]);
        var settings=JsonSerializer.SerializeToElement(new{text="Fixture content"});
        void Refuse(string name,Action action){string before=JsonSerializer.Serialize(state,StateStore.Json);bool rejected=false;try{action();}catch(Exception error)when(error is LayoutEditException or InvalidDataException){rejected=true;}Check(name,rejected&&before==JsonSerializer.Serialize(state,StateStore.Json));}
        var builtins=DesktopCardCatalog.Load(Path.Combine(AppContext.BaseDirectory,"desktop-widget","card-catalog.json"));
        Check("EncoDeck dependency preserves approved wording and pinned release",builtins.Require("system.encodeck").Dependency is {Title:"需先安装 EncoDeck 桌面端",Description:"此卡片由社区开源项目提供，安装并运行 EncoDeck 后才能读取耳机电量与降噪状态。"}&&builtins.DependencyUri("system.encodeck").AbsoluteUri=="https://github.com/littletaro97-arch/EncoDeck-MagicCommunity/releases/tag/v1.14.0-magic.1");
        var clockBoard=new Board{Id="clock-fixture",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}};
        var clockState=new LayoutState{Boards=[clockBoard]};
        var clock=DesktopCards.Add(clockState,clockBoard,builtins,"system.clock",4,2,JsonSerializer.SerializeToElement(new{fontSize=108,fontWeight=300,inkOpacity=.7,extension="keep"}));
        DesktopCards.Resize(clockBoard,builtins,clock.Id,2,1);
        Check("compact clock clamps font and preserves weight and extension",clock.Settings.GetProperty("fontSize").GetInt32()==56&&clock.Settings.GetProperty("fontWeight").GetInt32()==300&&clock.Settings.GetProperty("extension").GetString()=="keep");
        Refuse("compact clock rejects an unreadable font without changing settings",()=>DesktopCards.Configure(clockBoard,clock.Id,JsonSerializer.SerializeToElement(new{fontSize=108,fontWeight=300})));
        Check("refused compact font leaves previous settings intact",clock.Settings.GetProperty("fontSize").GetInt32()==56&&clock.Settings.GetProperty("extension").GetString()=="keep");
        DesktopCards.Resize(clockBoard,builtins,clock.Id,4,2);
        DesktopCards.Configure(clockBoard,clock.Id,JsonSerializer.SerializeToElement(new{fontSize=true,fontWeight="bad",extension="keep"}));
        Check("clock optional malformed fields fall back independently",clock.Settings.GetProperty("fontSize").GetInt32()==82&&clock.Settings.GetProperty("fontWeight").GetInt32()==900&&clock.Settings.GetProperty("extension").GetString()=="keep");
        Check("ordinary cards have no download requirement",builtins.Types.Where(d=>d.Type!="system.encodeck").All(d=>d.Dependency is null));
        Refuse("ordinary cards cannot open a dependency URL",()=>builtins.DependencyUri("system.clock"));
        foreach(var url in new[]{"file:///C:/Windows/notepad.exe","https://github.com.evil.example/a/b/releases/latest","https://user@github.com/a/b/releases/latest","http://github.com/a/b/releases/latest","https://github.com/a/b/releases/latest?run=1","https://github.com:444/a/b/releases/latest"})
            Refuse("reject untrusted dependency URL "+url,()=>new DesktopCardDependency{Url=url}.ReleaseUri());
        Refuse("first card collision cannot upgrade or alter an existing layout",()=>DesktopCards.Add(state,board,catalog,"fixture.basic",2,2,settings,0,0));
        Refuse("unknown types cannot be instantiated",()=>DesktopCards.Add(state,board,catalog,"unregistered.type",2,2,settings));
        Refuse("unsupported sizes cannot be instantiated",()=>DesktopCards.Add(state,board,catalog,"fixture.basic",3,3,settings));
        var beforePositions=new Dictionary<string,GridRect>(board.Grid.Placements);var card=DesktopCards.Add(state,board,catalog,"fixture.basic",4,2,settings,2,2);
        Check("first card creates a genuine third tile and upgrades only then",state.SchemaVersion==5&&board.Cards?.Count==1&&board.Grid.Placements[DesktopCards.Key(card)]==new GridRect(2,2,4,2));
        Check("card creation leaves every icon and folder anchor unchanged",beforePositions.All(row=>board.Grid.Placements[row.Key]==row.Value));
        store.Save(state);var backups=Directory.GetFiles(store.DirectoryPath,"*.before-cards-v5-*.json");
        Check("first version-5 save preserves an immutable exact version-4 snapshot",backups.Length==1&&File.ReadAllBytes(backups[0]).SequenceEqual(original));
        DesktopCards.Configure(board,card.Id,JsonSerializer.SerializeToElement(new{text="Updated"}));store.Save(state);
        Check("settings update does not repeatedly archive the version-4 file",Directory.GetFiles(store.DirectoryPath,"*.before-cards-v5-*.json").Length==1&&card.Settings.GetProperty("text").GetString()=="Updated");
        Refuse("oversized settings leave the saved card unchanged",()=>DesktopCards.Configure(board,card.Id,JsonSerializer.SerializeToElement(new{text=new string('x',17000)})));
        Refuse("non-object settings are rejected",()=>DesktopCards.Configure(board,card.Id,JsonSerializer.SerializeToElement("invalid")));
        Refuse("overlapping resize does not displace neighbours",()=>DesktopCards.Resize(board,catalog,card.Id,4,4));
        DesktopCards.Resize(board,catalog,card.Id,2,2);Check("allowed resize preserves the exact anchor",board.Grid.Placements[DesktopCards.Key(card)]==new GridRect(2,2,2,2));
        DesktopGridActions.Reconcile(state,[a,b],[]);Check("desktop refresh retains card content and placement",board.Cards?.Count==1&&card.Settings.GetProperty("text").GetString()=="Updated"&&board.Grid.Placements[DesktopCards.Key(card)]==new GridRect(2,2,2,2));
        Refuse("card cannot be moved over an existing icon",()=>DesktopGridActions.Place(board,DesktopCards.Key(card),0,0));
        DesktopGridActions.Place(board,DesktopCards.Key(card),3,2);Check("card uses the same grid move command",board.Grid.Placements[DesktopCards.Key(card)]==new GridRect(3,2,2,2));
        Refuse("group file movement respects a card's occupied cells",()=>DesktopGroupOperations.Place(board,[DesktopGridActions.ItemKey(a),DesktopGridActions.ItemKey(b)],DesktopGridActions.ItemKey(a),3,2));
        DesktopGroupOperations.Place(board,[DesktopGridActions.ItemKey(a),DesktopGridActions.ItemKey(b)],DesktopGridActions.ItemKey(a),0,6);Check("group file movement remains valid with a card present",board.Grid.Placements[DesktopCards.Key(card)]==new GridRect(3,2,2,2));
        DesktopCards.Transfer(board,state.Boards[1],card.Id,2,3);Check("cross-board transfer preserves the one instance and footprint",!DesktopCards.Items(board).Any()&&state.Boards[1].Cards?.Single().Id==card.Id&&state.Boards[1].Grid!.Placements[DesktopCards.Key(card)]==new GridRect(2,3,2,2));
        store.Save(state);var reopened=new StateStore(store.DirectoryPath).Load();Check("version-5 restart preserves card settings and board",reopened.SchemaVersion==5&&reopened.Boards[1].Cards?.Single().Settings.GetProperty("text").GetString()=="Updated");
        var emptyCatalog=new DesktopCardCatalog([]);bool unavailable=false;try{emptyCatalog.Require(card.Type);}catch(LayoutEditException){unavailable=true;}StateStore.Validate(reopened);Check("missing type remains stored and validates without deleting its instance",unavailable&&reopened.Boards[1].Cards?.Single().Id==card.Id);
        bool legacyRefused=false;try{LayoutCompatibility.ExportV1(reopened);}catch(InvalidDataException){legacyRefused=true;}Check("legacy export cannot silently discard a card's settings",legacyRefused&&reopened.Boards[1].Cards?.Count==1);
        var narrow=new Board{Id="narrow",Grid=new(){Columns=4,Rows=4,NativeInitialized=true},RootItems=[a]};narrow.Grid.Placements[DesktopGridActions.ItemKey(a)]=new(0,0,1,1);string narrowBefore=JsonSerializer.Serialize(narrow,StateStore.Json);bool noRoom=false;try{DesktopCards.Add(state,narrow,catalog,"fixture.basic",4,4,settings);}catch(LayoutEditException){noRoom=true;}Check("auto placement rejects full boards without growing or shuffling",noRoom&&narrowBefore==JsonSerializer.Serialize(narrow,StateStore.Json));
        var bad=StateStore.Copy(reopened);bad.Boards[1].Cards![0].Settings=JsonSerializer.SerializeToElement(1);byte[] protectedFile=File.ReadAllBytes(store.FilePath);bool invalid=false;try{store.Save(bad);}catch(InvalidDataException){invalid=true;}Check("malformed persisted settings cannot overwrite the accepted layout",invalid&&protectedFile.SequenceEqual(File.ReadAllBytes(store.FilePath)));
        DesktopCards.Remove(reopened.Boards[1],card.Id);Check("uninstalled cards can be removed without file operations",!DesktopCards.Items(reopened.Boards[1]).Any()&&!reopened.Boards[1].Grid!.Placements.ContainsKey(DesktopCards.Key(card)));StateStore.Validate(reopened);
        return new{allPass=true,checks,fixture,scope="Real model/grid/state persistence fixtures including lazy schema migration; no user layout mutation, timers, providers or installed card activation"};
    }
}
