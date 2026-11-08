using System.IO;
using System.Text.Json;
namespace MagicCommunity;
internal static class DesktopGridSettingsTests
{
    internal static object Run(string root)
    {
        root=Path.Combine(Path.GetFullPath(root),"grid-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var checks=new List<object>();void Check(string name,Action test){test();checks.Add(new{name,pass=true});}
        void Expect(bool pass,string why){if(!pass)throw new InvalidDataException(why);}
        DesktopItem Item(string name)=>new(){Name=name,Target=Path.Combine(root,name)};
        Board Fixture(){
            var board=new Board{Id="a",Name="A",Grid=new(){Columns=14,Rows=9,Bounded=true,NativeInitialized=true},
                RootItems=[Item("file10.txt"),Item("file2.txt"),Item("file1.txt")],Boxes=[new(){Id="box",Name="Fixed",Columns=2,Rows=2}],
                Cards=[new(){Id="0123456789abcdef0123456789abcdef",Type="system.clock",Columns=2,Rows=2,Settings=JsonSerializer.SerializeToElement(new{})}]};
            var state=new LayoutState{SchemaVersion=5,Boards=[board]};DesktopPages.Initialize(state,board,new(0,0,96,96,1,14,9),true);
            DesktopGridActions.Place(board,"folder:box",2,2);DesktopGridActions.Place(board,"card:0123456789abcdef0123456789abcdef",5,2);
            return board;
        }
        var display=new Display("a","A","fixture",0,80,3840,2080,true){PixelX=0,PixelY=0,PixelWidth=3840,PixelHeight=2160};
        Check("secondary-portrait-landscape-mixed-DPI-match-primary-cell",()=>{
            var primary=Fixture();string original=DesktopGridLayout.Stamp(primary);double cell=DesktopGridLayout.Project(primary,display,2).StepX/2;
            var secondary=new Board{Id="secondary",Grid=new(){Columns=14,Rows=9}};
            foreach(double scale in new[]{1.0,1.5,2.0}){
                var portrait=new Display("secondary","Secondary","fixture",0,0,(int)(800*scale),(int)(1248*scale),false);
                var p=DesktopGridLayout.Project(secondary,portrait,scale,preferredCell:cell);
                Expect(p.Rows>p.Columns&&p.StepX/scale==cell,"portrait retained landscape counts or a different cell");
                var wide=DesktopGridLayout.Project(secondary,portrait with{Width=portrait.Height,Height=portrait.Width},scale,preferredCell:cell);
                Expect(wide.Columns>wide.Rows&&wide.StepX/scale==cell,"landscape not adaptive");
            }
            Expect(DesktopGridLayout.Stamp(primary)==original,"secondary calculation changed primary layout");
            secondary.IconGrid=new(){Columns=3,Rows=8,RefWidth=800,RefHeight=1248};
            Expect(!DesktopGridLayout.Project(secondary,new("secondary","Secondary","fixture",0,0,800,1248,false),1,preferredCell:cell).AdaptiveSecondary,"explicit secondary settings overwritten");
        });
        Check("secondary-rotation-preserves-valid-anchors-page-ids-and-all-content",()=>{
            var secondary=Fixture();secondary.IconGrid=null;secondary.Paged=false;
            for(int i=0;i<40;i++)secondary.RootItems.Add(Item("secondary-"+i));DesktopPages.Reconcile(secondary);
            string page=secondary.ActivePageId!;var valid=secondary.Grid!.Placements.Where(row=>row.Value.X+row.Value.W<=4&&row.Value.Y+row.Value.H<=6).ToDictionary();
            var expected=DesktopGridActions.Expected(secondary).Keys.ToHashSet();
            var p=DesktopGridLayout.Project(secondary,new("secondary","Secondary","fixture",0,0,432,592,false),1,preferredCell:96);
            DesktopGridLayout.ResizeAdaptiveSecondary(secondary,p);DesktopPages.Validate(secondary);
            Expect(secondary.ActivePageId==page&&secondary.Pages![0].Id==page,"page identity changed");
            Expect(valid.All(row=>secondary.Pages![0].Grid.Placements.GetValueOrDefault(row.Key)==row.Value),"valid anchor moved");
            Expect(secondary.Paged&&secondary.Unplaced.Count==0&&DesktopPages.Grids(secondary).SelectMany(g=>g.Placements.Keys).ToHashSet().SetEquals(expected),"rotation lost or stranded content");
            string once=DesktopGridLayout.Stamp(secondary);DesktopGridLayout.ResizeAdaptiveSecondary(secondary,p);Expect(once==DesktopGridLayout.Stamp(secondary),"repeated registration rearranges content");
        });
        Check("DPI-150-to-300-preserves-counts-and-every-anchor",()=>{
            var board=Fixture();board.IconGrid=new(){Columns=14,Rows=9,RefWidth=3840,RefHeight=2160};
            string before=DesktopGridLayout.Stamp(board);
            foreach(double scale in new[]{1.5,1.75,2,2.25,2.5,3}){
                var p=DesktopGridLayout.Project(board,display,scale,new(true,"Bottom",64));
                Expect(p.Columns==14&&p.Rows==9&&p.StepX==p.StepY,"DPI changed saved counts or square cell");
                Expect(p.OriginY>=0&&p.WorkspaceHeight==display.Height/scale-64,"work area counted incorrectly");
            }
            Expect(before==DesktopGridLayout.Stamp(board),"projection wrote board");
        });
        Check("bar-reservation-and-four-dock-edges-no-double-subtract",()=>{
            Expect(new DesktopDockFootprint(true,"Top",32).Insets(display,2).Top==0,"toolbar reserved twice");
            Expect(new DesktopDockFootprint(true,"Top",104).Insets(display,2).Top==64,"stacked toolbar dock not reserved");
            Expect(new DesktopDockFootprint(true,"Bottom",64).Insets(display,2).Bottom==64,"bottom dock");
            Expect(new DesktopDockFootprint(true,"Left",64).Insets(display,2).Left==64,"left dock");
            Expect(new DesktopDockFootprint(true,"Right",64).Insets(display,2).Right==64,"right dock");
            Expect(DesktopDockFootprint.None.Insets(display,2)==default,"disabled dock occupied space");
        });
        Check("physical-resolution-ratio-uses-fixed-reference-and-protects-content",()=>{
            var board=Fixture();board.IconGrid=new(){Columns=14,Rows=9,RefWidth=3840,RefHeight=2160};
            var up=DesktopGridLayout.Project(board,display with{Width=7680,Height=4240,PixelWidth=7680,PixelHeight=4320},2);
            Expect(up.Columns==28&&up.Rows==18,"resolution ratio not applied");
            DesktopGridActions.Place(board,DesktopGridActions.ItemKey(board.RootItems[0]),13,8);
            var down=DesktopGridLayout.Project(board,display with{Width=1920,Height=1000,PixelWidth=1920,PixelHeight=1080},1.5);
            Expect(down.Columns==14&&down.Rows==9&&down.Notice.Length>0,"smaller resolution lost anchors");
            Expect(board.IconGrid.RefWidth==3840,"reference drifted");
        });
        Check("drop-projection-and-frame-share-variable-cell-and-dock-origin",()=>{
            var b=Fixture();var p=DesktopGridLayout.Project(b,display,2,new(true,"Bottom",64));int x=3,y=4;
            var point=UnifiedDesktopGrid.ProjectDrop(display,2,(p.OriginX+x*p.StepX)/2,(p.OriginY+y*p.StepY)/2,display,2,0,0,0,0,p);
            Expect(point.Column==x&&point.Row==y,"drop cell drift");
            var frame=p.Frame(new(x,y,1,1),display);
            Expect(Math.Abs(frame.Right-frame.Left-p.StepX*(1-8d/96))<2,"frame inset not proportional");
        });
        Check("failed-shrink-never-relocates-or-unplaces",()=>{
            var b=Fixture();DesktopGridActions.Place(b,DesktopGridActions.ItemKey(b.RootItems[0]),13,8);string before=DesktopGridLayout.Stamp(b);
            try{DesktopGridLayout.ResizeWithoutMoving(b,new(0,0,96,96,1,10,6));throw new Exception("shrink accepted");}catch(LayoutEditException){}
            Expect(before==DesktopGridLayout.Stamp(b),"failed shrink changed anchors");
        });
        Check("insertion-parity-right-bottom-get-extra-odd-cell",()=>{
            Expect(DesktopGridLayout.Shift(3,"split")==1&&DesktopGridLayout.Shift(-3,"split")==-1,"odd split");
            Expect(DesktopGridLayout.Shift(3,"left")==3&&DesktopGridLayout.Shift(-3,"top")==-3,"start insertion");
            Expect(DesktopGridLayout.Shift(3,"right")==0&&DesktopGridLayout.Shift(-3,"bottom")==0,"end insertion");
        });
        var controller=new AppController(Path.Combine(root,"session"));
        string boardId=controller.Screens[0].Id;
        controller.Commit(state=>{var b=Fixture();b.Id=boardId;int at=state.Boards.FindIndex(b=>b.Id==boardId);state.Boards[at]=b;state.SchemaVersion=Math.Max(8,state.SchemaVersion);},"Fixture");
        display=display with{Id=boardId};
        var undo=new DesktopLayoutUndo();var editor=new DesktopGridEditSession(controller,undo);
        Check("secondary-editor-has-one-cell-minimum-with-overflow-protection",()=>{
            var response=JsonSerializer.SerializeToElement(editor.Handle(boardId,display with{Primary=false},2,DesktopDockFootprint.None,JsonSerializer.SerializeToElement(new{action="begin"})));
            Expect(response.GetProperty("minColumns").GetInt32()==1&&response.GetProperty("minRows").GetInt32()==1,"fixed secondary minimum remains");
            string token=response.GetProperty("editId").GetString()!;
            var small=JsonSerializer.SerializeToElement(editor.Handle(boardId,display with{Primary=false},2,DesktopDockFootprint.None,JsonSerializer.SerializeToElement(new{action="update",editId=token,columns=1,rows=1})));
            Expect(!small.GetProperty("canCommit").GetBoolean(),"smaller grid silently drops existing contents");
            editor.Handle(boardId,display,2,DesktopDockFootprint.None,JsonSerializer.SerializeToElement(new{action="cancel",editId=token}));
        });
        JsonElement Send(string action,string? id=null,int? columns=null,int? rows=null,string side="right")=>JsonSerializer.SerializeToElement(editor.Handle(boardId,display,2,new(true,"Bottom",64),JsonSerializer.SerializeToElement(new{action,editId=id,columns=columns??14,rows=rows??9,columnSide=side,rowSide="bottom"})));
        Check("begin-update-cancel-do-not-write-layout-or-revision",()=>{
            var bytes=File.ReadAllBytes(controller.Store.FilePath);long revision=controller.State.Revision;
            var start=Send("begin");string token=start.GetProperty("editId").GetString()!;
            var draft=Send("update",token,16,10);Expect(draft.GetProperty("canCommit").GetBoolean(),"valid draft rejected");
            Send("cancel",token);Expect(revision==controller.State.Revision&&bytes.SequenceEqual(File.ReadAllBytes(controller.Store.FilePath)),"cancel wrote layout");
        });
        Check("overflow-on-another-page-blocks-done-without-moving-members",()=>{
            controller.Commit(state=>{var b=state.Boards.Single(b=>b.Id==boardId);DesktopPages.Add(b,false);var key=DesktopGridActions.ItemKey(b.RootItems[0]);DesktopPages.Drop(state,b,b.Pages![0].Id,b.ActivePageId!,[key],key,null,null,13,8);DesktopPages.Select(b,b.Pages[0].Id);},"Fixture page");
            var bytes=File.ReadAllBytes(controller.Store.FilePath);var start=Send("begin");string token=start.GetProperty("editId").GetString()!;
            var draft=Send("update",token,10,6);Expect(!draft.GetProperty("canCommit").GetBoolean()&&draft.GetProperty("overflow").GetInt32()==1,"other-page overflow missed");
            try{Send("commit",token,10,6);throw new Exception("overflow committed");}catch(LayoutEditException){}
            Send("cancel",token);Expect(bytes.SequenceEqual(File.ReadAllBytes(controller.Store.FilePath)),"overflow changed layout");
        });
        Check("commit-all-pages-backup-restart-and-whole-operation-undo",()=>{
            var before=controller.State.Boards.Single(b=>b.Id==boardId);string stamp=DesktopGridLayout.Stamp(before);
            var source=File.ReadAllBytes(controller.Store.FilePath);
            var start=Send("begin");Send("commit",start.GetProperty("editId").GetString(),16,10,"split");
            var after=controller.State.Boards.Single(b=>b.Id==boardId);
            Expect(after.IconGrid is {Columns:16,Rows:10}&&DesktopPages.Grids(after).All(g=>g.Columns==16&&g.Rows==10),"all pages not committed");
            Expect(DesktopPages.Grids(after).Zip(DesktopPages.Grids(before)).All(pair=>pair.First.Placements.All(p=>p.Value==pair.Second.Placements[p.Key] with{X=pair.Second.Placements[p.Key].X+1})),"insertion moved wrong anchors");
            Expect(source.SequenceEqual(File.ReadAllBytes(controller.Store.BackupPath)),"pre-edit backup lost");
            var loaded=new StateStore(controller.Store.DirectoryPath).Load();Expect(loaded.SchemaVersion>=8&&loaded.Boards.Single(b=>b.Id==boardId).IconGrid!.Columns==16,"restart lost grid settings");
            Expect(undo.Undo(controller,boardId)&&stamp==DesktopGridLayout.Stamp(controller.State.Boards.Single(b=>b.Id==boardId)),"whole grid undo failed");
        });
        Check("stale-draft-does-not-overwrite-new-files-or-other-changes",()=>{
            var start=Send("begin");controller.Commit(state=>state.Boards.Single(b=>b.Id==boardId).Name="Changed","Fixture stale");
            var bytes=File.ReadAllBytes(controller.Store.FilePath);string token=start.GetProperty("editId").GetString()!;
            Expect(!Send("update",token,16,10).GetProperty("canCommit").GetBoolean(),"stale draft valid");
            try{Send("commit",token,16,10);throw new Exception("stale committed");}catch(LayoutEditException){}
            Send("cancel",token);Expect(bytes.SequenceEqual(File.ReadAllBytes(controller.Store.FilePath)),"stale overwrote file");
        });
        Check("sort-natural-name-stable-page-membership-fixed-card-and-folder",()=>{
            var b=Fixture();var original=DesktopGridLayout.Stamp(b);var sorted=DesktopSort.Arrange(b,"name",false,new Dictionary<string,DesktopSort.Properties>());
            var keys=sorted.Grid!.Placements.Where(p=>p.Key.StartsWith("item:")).OrderBy(p=>p.Value.Y).ThenBy(p=>p.Value.X).Select(p=>sorted.RootItems.Single(i=>DesktopGridActions.ItemKey(i)==p.Key).Name).ToArray();
            Expect(keys.SequenceEqual(new[]{"file1.txt","file2.txt","file10.txt"}),"name not natural order");
            Expect(sorted.Grid.Placements["folder:box"]==b.Grid!.Placements["folder:box"]&&sorted.Grid.Placements["card:0123456789abcdef0123456789abcdef"]==b.Grid.Placements["card:0123456789abcdef0123456789abcdef"],"fixed tile moved");
            Expect(original==DesktopGridLayout.Stamp(b),"sort mutated input");
            var paged=controller.State.Boards.Single(b=>b.Id==boardId);var result=DesktopSort.Arrange(paged,"name",true,new Dictionary<string,DesktopSort.Properties>());
            Expect(result.Pages!.Zip(paged.Pages!).All(pair=>pair.First.Grid.Placements.Keys.ToHashSet().SetEquals(pair.Second.Grid.Placements.Keys)),"sort changed page membership");
        });
        Check("size-type-date-null-last-both-directions-and-custom-preserves-layout",()=>{
            foreach(string mode in new[]{"size","type","modified"})foreach(bool descending in new[]{false,true}){
                var b=Fixture();var metadata=b.RootItems.Take(2).Select((i,n)=>new DesktopSort.Properties(i.Target,n==0?20:2,n==0?20:2,n==0?"Type20":"Type2")).ToDictionary(p=>p.Target);
                var sorted=DesktopSort.Arrange(b,mode,descending,metadata);var positions=sorted.Grid!.Placements;
                var first=sorted.RootItems.OrderBy(i=>positions[DesktopGridActions.ItemKey(i)].Y*14+positions[DesktopGridActions.ItemKey(i)].X).ToArray();
                Expect(first[^1].Name=="file1.txt","unknown metadata not last");Expect(first[0].Name==(descending?"file10.txt":"file2.txt"),"direction incorrect");
                var custom=DesktopSort.Arrange(sorted,"custom",descending,metadata);Expect(custom.Grid!.Placements.SequenceEqual(positions),"custom rearranged icons");
                DesktopSort.Manual(sorted);Expect(sorted.SortSettings!.Mode=="custom","manual drag did not reset mode");
            }
        });
        Check("new-file-normal-placement-does-not-trigger-sort",()=>{
            var b=DesktopSort.Arrange(Fixture(),"name",false,new Dictionary<string,DesktopSort.Properties>());
            var anchors=new Dictionary<string,GridRect>(b.Grid!.Placements);b.RootItems.Add(Item("aaa.txt"));DesktopPages.Reconcile(b);
            Expect(anchors.All(p=>b.Grid.Placements[p.Key]==p.Value)&&b.SortSettings!.Mode=="name","new file triggered rearrangement");
        });
        Check("sort-undo-atomic-and-invalidated-after-new-content",()=>{
            var before=controller.State.Boards.Single(b=>b.Id==boardId);var sorted=DesktopSort.Arrange(before,"name",true,new Dictionary<string,DesktopSort.Properties>());
            controller.Commit(state=>state.Boards[state.Boards.FindIndex(b=>b.Id==boardId)]=sorted,"Fixture sort");undo.Push(before,controller.State.Boards.Single(b=>b.Id==boardId));
            var page=sorted.Pages!.Last().Id;controller.Commit(state=>DesktopPages.Select(state.Boards.Single(b=>b.Id==boardId),page),"Fixture navigation");
            Expect(undo.Undo(controller,boardId),"sort undo after page navigation");var restored=controller.State.Boards.Single(b=>b.Id==boardId);
            Expect(restored.ActivePageId==page&&restored.Pages!.Zip(before.Pages!).All(pair=>pair.First.Grid.Placements.SequenceEqual(pair.Second.Grid.Placements)),"undo changed navigation or did not restore positions");
            undo.Push(before,before);controller.Commit(state=>state.Boards.Single(b=>b.Id==boardId).Name="newer","Fixture newer");
            Expect(!undo.CanUndo(controller.State.Boards.Single(b=>b.Id==boardId)),"stale undo could overwrite content");
        });
        Check("metadata-worker-reads-owned-file-properties-and-exits",()=>{
            var b=Fixture();foreach(var item in b.RootItems)File.WriteAllText(item.Target,item.Name);
            foreach(string mode in new[]{"size","type","modified"}){
                var metadata=DesktopSort.Metadata(controller.Root,b,mode,CancellationToken.None).GetAwaiter().GetResult();
                Expect(metadata.Count==3&&metadata[b.RootItems[0].Target] is {} p&&(mode=="size"?p.Size==b.RootItems[0].Name.Length:mode=="type"?!string.IsNullOrWhiteSpace(p.Type):p.Modified>0),"worker metadata mismatch");
            }
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{DesktopSort.Metadata(controller.Root,b,"size",cancelled.Token).GetAwaiter().GetResult();throw new Exception("cancel ignored");}catch(OperationCanceledException){}
        });
        return new{allPass=true,checks,scope="Owned fixture files only; no real desktop input or runtime switch",directory=root};
    }
}
