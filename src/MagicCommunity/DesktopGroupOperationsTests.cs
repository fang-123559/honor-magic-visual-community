using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopGroupOperationsTests
{
    public static object Run(string root,bool physical=false,bool recycle=false)
    {
        object layout=TestLayout(),journals=TestJournals(root),recyclePresentation=TestRecyclePresentation(root);object? transfers=physical?TestTransfers(root,recycle):null;
        return new{allPass=true,layout,journals,recyclePresentation,transfers,systemClipboardWritten=false,scope="Pure atomic group placement and temporary owned journal fixtures; optional native multi-item drops on newly created project fixtures only"};
    }
    private static object TestRecyclePresentation(string root)
    {
        string fixture=Path.Combine(root,"artifacts","recycle-presentation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        string empty=Path.Combine(fixture,"empty"),nested=Path.Combine(fixture,"nested"),small=Path.Combine(fixture,"small.txt"),large=Path.Combine(fixture,"large.bin");
        Directory.CreateDirectory(empty);Directory.CreateDirectory(nested);File.WriteAllText(Path.Combine(nested,"keep.txt"),"Retained fixture");File.WriteAllText(small,"Retained fixture");
        using(var stream=File.Create(large))stream.SetLength(1024*1024+1);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        const uint normal=0x40u|0x20000000u|0x80000u|0x4000u,silent=normal|0x4u;
        Check("empty local folder omits progress only",ShellFileMove.RecycleOperationFlags([empty])==silent);
        Check("small mixed batch omits progress only",ShellFileMove.RecycleOperationFlags([empty,small])==silent);
        Check("nonempty folder retains progress and cancellation",ShellFileMove.RecycleOperationFlags([nested])==normal);
        Check("larger file retains progress",ShellFileMove.RecycleOperationFlags([large])==normal);
        Check("large batch retains progress",ShellFileMove.RecycleOperationFlags(Enumerable.Repeat(small,9).ToArray())==normal);
        Check("missing path retains Windows error handling",ShellFileMove.RecycleOperationFlags([Path.Combine(fixture,"missing")])==normal);
        Check("relative path never opts into silent progress",ShellFileMove.RecycleOperationFlags(["relative.txt"])==normal);
        Check("UNC path is rejected before probing network",ShellFileMove.RecycleOperationFlags([@"\\invalid-host\share\missing.txt"])==normal);
        Check("empty selection does not opt into silence",ShellFileMove.RecycleOperationFlags([])==normal);
        Check("no fixture recycled and contents preserved",Directory.Exists(empty)&&File.ReadAllText(small)=="Retained fixture"&&File.Exists(Path.Combine(nested,"keep.txt")));
        return new{allPass=true,checks,fixture,physicalOperationPerformed=false};
    }
    public static object TestRecycleFolders(string root)
    {
        string fixture=Path.Combine(root,"evidence","desktop-feedback",DateTime.Now.ToString("yyyy-MM-dd"),"group-recycle-completion-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        string unselected=Path.Combine(fixture,"unselected");Directory.CreateDirectory(unselected);File.WriteAllText(Path.Combine(unselected,"keep.txt"),"Unselected owned fixture");
        foreach(string kind in new[]{"empty-folders","nested-folders","mixed-selection"})
        {
            string unique="magic-recycle-completion-"+Guid.NewGuid().ToString("N"),scope=Path.Combine(fixture,kind);Directory.CreateDirectory(scope);
            string first=Path.Combine(scope,unique+"-A"),second=Path.Combine(scope,unique+"-B");Directory.CreateDirectory(first);
            if(kind=="mixed-selection"){second+=".txt";File.WriteAllText(second,"Owned mixed fixture");}else Directory.CreateDirectory(second);
            if(kind=="nested-folders")foreach(string dir in new[]{first,second}){string child=Path.Combine(dir,"child");Directory.CreateDirectory(child);for(int i=0;i<8;i++)File.WriteAllText(Path.Combine(child,i+".txt"),"Owned nested recycle fixture "+i);}
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var result=JsonSerializer.SerializeToElement(ShellClipboardCommands.TransferSelection([first,second],ShellClipboardCommands.RecycleBinTarget,false,true));
            Check(kind+": returns success for both members without a delayed retry",result.GetProperty("ok").GetBoolean()&&result.GetProperty("handled").GetInt32()==2&&result.GetProperty("recycled").GetBoolean());
            Check(kind+": both sources already absent when the call completes",!Directory.Exists(first)&&!File.Exists(second)&&!Directory.Exists(second));
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;object? bin=null,entries=null;var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try{bin=shell.NameSpace(10);entries=((dynamic)bin!).Items();foreach(object item in (System.Collections.IEnumerable)entries){try{string name=(string)((dynamic)item).Name;if(name.StartsWith(unique,StringComparison.Ordinal))found.Add(name);}finally{Marshal.ReleaseComObject(item);}}}
            finally{if(entries is not null)Marshal.ReleaseComObject(entries);if(bin is not null)Marshal.ReleaseComObject(bin);Marshal.ReleaseComObject(shell);}
            Check(kind+": both members are in the real Recycle Bin",found.Any(name=>name.StartsWith(unique+"-A",StringComparison.Ordinal))&&found.Any(name=>name.StartsWith(unique+"-B",StringComparison.Ordinal)));
            Check(kind+": unselected directory and contents are preserved",File.ReadAllText(Path.Combine(unselected,"keep.txt"))=="Unselected owned fixture");
            checks.Add(new{name=kind+": elapsed",pass=true,milliseconds=timer.Elapsed.TotalMilliseconds});
        }
        return new{allPass=true,checks,fixture,systemClipboardWritten=false,scope="Real empty/nested directories and mixed file-directory batches; newly created project fixtures only; no sleep, retry or desktop user files"};
    }
    public static object TestLayout()
    {
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        var items=new[]{"A","B","C"}.Select(name=>new DesktopItem{Name=name,Target=Path.Combine(Path.GetTempPath(),"magic-group-pure-"+name+".txt")}).ToArray();
        var folder=new Box{Id="test-folder",Columns=2,Rows=2};
        var board=new Board{Id="pure",RootItems=items.ToList(),Boxes=[folder],Grid=new(){Columns=8,Rows=8}};
        string a=DesktopGridActions.ItemKey(items[0]),b=DesktopGridActions.ItemKey(items[1]),c=DesktopGridActions.ItemKey(items[2]),f=DesktopGridActions.FolderKey(folder);
        board.Grid.Placements=new(StringComparer.Ordinal){[a]=new(1,1,1,1),[b]=new(2,1,1,1),[c]=new(6,1,1,1),[f]=new(4,4,2,2)};
        DesktopGroupOperations.Place(board,[a,b],a,2,1);
        Check("group may occupy selected items' old positions without self collision",board.Grid.Placements[a]==new GridRect(2,1,1,1)&&board.Grid.Placements[b]==new GridRect(3,1,1,1));
        Check("unselected neighbours do not move",board.Grid.Placements[c]==new GridRect(6,1,1,1)&&board.Grid.Placements[f]==new GridRect(4,4,2,2));
        void Refuse(string label,IEnumerable<string> keys,string anchor,int x,int y)
        {
            string before=JsonSerializer.Serialize(board.Grid.Placements);bool rejected=false;
            try{DesktopGroupOperations.Place(board,keys,anchor,x,y);}catch(LayoutEditException){rejected=true;}
            Check(label,rejected&&JsonSerializer.Serialize(board.Grid.Placements)==before);DesktopGridActions.Validate(board);
        }
        Refuse("any neighbour collision rejects the whole group atomically",[a,b],a,5,1);
        Refuse("negative edge rejects all items atomically",[a,b],a,-1,1);
        Refuse("large integer coordinates cannot wrap into a legal position",[a,b],a,int.MaxValue,int.MinValue);
        Refuse("missing anchor cannot move a different selection",[a,b],c,2,2);
        Refuse("duplicate selection cannot move a tile twice",[a,a],a,2,2);
        Refuse("stale selected key preserves every existing placement",[a,"item:missing"],a,2,2);
        Refuse("empty group cannot mutate layout",[],a,2,2);
        DesktopGroupOperations.Place(board,[a,f],f,3,3);
        Check("folder footprint and sparse relative offsets are preserved",board.Grid.Placements[f]==new GridRect(3,3,2,2)&&board.Grid.Placements[a]==new GridRect(1,0,1,1));
        DesktopGridActions.Validate(board);
        board.Grid.Placements[a]=new(6,1,1,1);board.Grid.Placements[b]=new(7,1,1,1);
        DesktopGroupOperations.Place(board,[a,b],a,1,5);
        Check("staged source positions may overlap destination neighbours before final translation",board.Grid.Placements[a]==new GridRect(1,5,1,1)&&board.Grid.Placements[b]==new GridRect(2,5,1,1)&&board.Grid.Placements[c]==new GridRect(6,1,1,1));
        board.Grid.Placements[a]=new(20,20,1,1);board.Grid.Placements[b]=new(21,20,1,1);
        DesktopGroupOperations.Place(board,[a,b],a,1,6);
        Check("staged source coordinates outside destination bounds do not reject a legal final placement",board.Grid.Placements[a]==new GridRect(1,6,1,1)&&board.Grid.Placements[b]==new GridRect(2,6,1,1));
        DesktopGridActions.Validate(board);
        Check("same-volume group defaults to move",ShellClipboardCommands.SelectionEffect([@"C:\left\a.txt",@"C:\right\b.txt"],@"C:\target",false,false)==2);
        Check("mixed-volume group conservatively defaults to copy",ShellClipboardCommands.SelectionEffect([@"C:\left\a.txt",@"D:\right\b.txt"],@"C:\target",false,false)==1);
        Check("Control forces the whole group to copy",ShellClipboardCommands.SelectionEffect([@"C:\left\a.txt"],@"C:\target",true,false)==1);
        Check("Shift forces the whole group to move",ShellClipboardCommands.SelectionEffect([@"D:\left\a.txt"],@"C:\target",false,true)==2);
        Check("Recycle Bin always uses normal move to recycle",ShellClipboardCommands.SelectionEffect([@"C:\left\a.txt"],ShellClipboardCommands.RecycleBinTarget,true,true)==2);
        var returnFolder=new Box{Id="return-folder",Items=items.ToList(),Columns=2,Rows=2};
        var returnBoard=new Board{Id="return-board",Boxes=[returnFolder],Grid=new(){Columns=8,Rows=8,NativeInitialized=true},SortSettings=new(){Mode="name"}};
        returnBoard.Grid.Placements[DesktopGridActions.FolderKey(returnFolder)]=new(1,1,2,2);
        var returnState=new LayoutState{SchemaVersion=7,Boards=[returnBoard]};string original=JsonSerializer.Serialize(returnState,StateStore.Json);
        foreach(var keys in new[]{new[]{a},new[]{a,c}}){
            DesktopGroupBridgeActions.Reference(returnState,returnBoard,returnBoard,keys,a,returnFolder.Id,returnFolder.Id,0,0);
            Check("return held group to source cover preserves layout order and sorting "+keys.Length,JsonSerializer.Serialize(returnState,StateStore.Json)==original);
        }
        bool staleReturnRejected=false;try{DesktopGroupBridgeActions.Reference(returnState,returnBoard,returnBoard,["item:missing"],"item:missing",returnFolder.Id,returnFolder.Id,0,0);}catch(LayoutEditException){staleReturnRejected=true;}
        Check("return to source still validates stale drag members",staleReturnRejected&&JsonSerializer.Serialize(returnState,StateStore.Json)==original);
        var otherFolder=new Box{Id="other-folder",Columns=2,Rows=2};returnBoard.Boxes.Add(otherFolder);returnBoard.Grid.Placements[DesktopGridActions.FolderKey(otherFolder)]=new(4,4,2,2);
        DesktopGroupBridgeActions.Reference(returnState,returnBoard,returnBoard,[a],a,returnFolder.Id,otherFolder.Id,0,0);
        Check("dragging to another cover uses normal collection",otherFolder.Items.Count==1&&otherFolder.Items[0].Target==items[0].Target&&returnFolder.Items.Select(item=>item.Target).SequenceEqual(items.Skip(1).Select(item=>item.Target)));
        return new{allPass=true,checks,filesWritten=false};
    }
    private static object TestJournals(string root)
    {
        string fixture=Path.Combine(root,"evidence","desktop-feedback",DateTime.Now.ToString("yyyy-MM-dd"),"group-journals-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        (string Root,StateStore Store,LayoutState State,string[] Paths,DesktopFileChanges.Intent[] Intents) Create(string name,int count,long revision,bool splitBox=false)
        {
            string directory=Path.Combine(fixture,name);Directory.CreateDirectory(directory);
            string[] paths=Enumerable.Range(0,count).Select(index=>Path.Combine(directory,"source-"+index+".txt")).ToArray();
            foreach(string path in paths)File.WriteAllText(path,"Owned journal fixture "+Path.GetFileName(path));
            var items=paths.Select(path=>new DesktopItem{Name=Path.GetFileName(path),Target=path}).ToArray();
            var board=new Board{Id="journal-fixture",Name=name,RootItems=splitBox?items[..2].ToList():items.ToList(),Boxes=splitBox?[new(){Id="journal-folder",Name="Owned folder",Items=items[2..].ToList()}]:[]};
            var state=new LayoutState{Revision=revision,Boards=[board]};var store=new StateStore(Path.Combine(directory,"data"));store.Save(state);
            var intents=paths.Select(path=>DesktopFileChanges.PrepareExternalDrag(directory,path)).ToArray();
            return(directory,store,state,paths,intents);
        }
        string Record(string directory,DesktopFileChanges.Intent intent)=>Path.Combine(directory,"data","shell-changes",intent.Id+".json");
        void Move(string directory,string source){string destination=Path.Combine(directory,"simulated-destination");Directory.CreateDirectory(destination);File.Move(source,Path.Combine(destination,Path.GetFileName(source)));}
        bool Contains(LayoutState state,string path)=>state.Boards.SelectMany(board=>board.RootItems.Concat(board.Boxes.SelectMany(box=>box.Items))).Any(item=>StringComparer.OrdinalIgnoreCase.Equals(item.Target,path));
        {
            var sample=Create("partial-move",4,41,true);Move(sample.Root,sample.Paths[0]);Move(sample.Root,sample.Paths[2]);
            var proposed=StateStore.Copy(sample.State);int deferred=DesktopFileChanges.FinishMany(sample.Root,sample.Store,proposed,sample.Intents.Select(intent=>intent.Id));
            var saved=sample.Store.Load();
            Check("partial group completion increments revision exactly once and saves all removals",deferred==0&&proposed.Revision==42&&saved.Revision==42&&!Contains(saved,sample.Paths[0])&&!Contains(saved,sample.Paths[2])&&Contains(saved,sample.Paths[1])&&Contains(saved,sample.Paths[3]));
            Check("removed sources disappear from root and folder references while unmoved sources remain",!Contains(proposed,sample.Paths[0])&&!Contains(proposed,sample.Paths[2])&&Contains(proposed,sample.Paths[1])&&Contains(proposed,sample.Paths[3]));
            Check("only caller publication can replace the old in-memory state",sample.State.Revision==41&&sample.Paths.All(path=>Contains(sample.State,path)));
            Check("successful partial completion cleans every processed intent",sample.Intents.All(intent=>!File.Exists(Record(sample.Root,intent))));
            string before=File.ReadAllText(sample.Store.FilePath);int repeated;
            using(var locked=new FileStream(sample.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                repeated=DesktopFileChanges.FinishMany(sample.Root,sample.Store,proposed,sample.Intents.Select(intent=>intent.Id));
            Check("repeated completed group cleanup is idempotent without attempting a save",repeated==0&&proposed.Revision==42&&File.ReadAllText(sample.Store.FilePath)==before);
            Check("metadata cleanup never replays physical file operations",File.ReadAllText(Path.Combine(sample.Root,"simulated-destination",Path.GetFileName(sample.Paths[0]))).StartsWith("Owned journal fixture",StringComparison.Ordinal)&&File.ReadAllText(sample.Paths[1]).StartsWith("Owned journal fixture",StringComparison.Ordinal));
        }
        {
            var sample=Create("cancelled",2,80);var proposed=StateStore.Copy(sample.State);string before=File.ReadAllText(sample.Store.FilePath);
            int deferred;
            // Deny replacement as well as writes: identical output bytes alone
            // would not prove that cancellation avoided a disk commit.
            using(var locked=new FileStream(sample.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                deferred=DesktopFileChanges.FinishMany(sample.Root,sample.Store,proposed,sample.Intents.Select(intent=>intent.Id));
            Check("cancelled group keeps all references without attempting a save or incrementing revision",deferred==0&&proposed.Revision==80&&sample.Paths.All(path=>Contains(proposed,path)&&File.Exists(path))&&File.ReadAllText(sample.Store.FilePath)==before);
            Check("cancelled group safely cleans all no-op intents",sample.Intents.All(intent=>!File.Exists(Record(sample.Root,intent))));
        }
        {
            var sample=Create("save-failure",2,91);Move(sample.Root,sample.Paths[0]);string before=File.ReadAllText(sample.Store.FilePath);bool refused=false;
            using(var locked=new FileStream(sample.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try{DesktopFileChanges.FinishMany(sample.Root,sample.Store,StateStore.Copy(sample.State),sample.Intents.Select(intent=>intent.Id));}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){refused=true;}
                Check("atomic state-write failure preserves every journal and the old disk state",refused&&sample.Intents.All(intent=>File.Exists(Record(sample.Root,intent)))&&File.ReadAllText(sample.Store.FilePath)==before);
                Check("state-write failure cannot publish a partially modified caller state",sample.State.Revision==91&&sample.Paths.All(path=>Contains(sample.State,path)));
            }
            var retry=StateStore.Copy(sample.State);int deferred=DesktopFileChanges.FinishMany(sample.Root,sample.Store,retry,sample.Intents.Select(intent=>intent.Id));
            Check("retry after state-write failure commits once and then cleans journals",deferred==0&&retry.Revision==92&&sample.Store.Load().Revision==92&&!Contains(retry,sample.Paths[0])&&Contains(retry,sample.Paths[1])&&sample.Intents.All(intent=>!File.Exists(Record(sample.Root,intent))));
        }
        {
            var sample=Create("cleanup-failure",2,101);Move(sample.Root,sample.Paths[0]);var proposed=StateStore.Copy(sample.State);string held=Record(sample.Root,sample.Intents[0]);
            using(var locked=new FileStream(held,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                int deferred=DesktopFileChanges.FinishMany(sample.Root,sample.Store,proposed,sample.Intents.Select(intent=>intent.Id));
                Check("journal-delete failure returns deferred without reverting the saved layout",deferred==1&&proposed.Revision==102&&sample.Store.Load().Revision==102&&!Contains(proposed,sample.Paths[0])&&Contains(proposed,sample.Paths[1])&&File.Exists(held)&&!File.Exists(Record(sample.Root,sample.Intents[1])));
            }
            string before=File.ReadAllText(sample.Store.FilePath);var retry=StateStore.Copy(proposed);int repeated;
            using(var locked=new FileStream(sample.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                repeated=DesktopFileChanges.FinishMany(sample.Root,sample.Store,retry,sample.Intents.Select(intent=>intent.Id));
            Check("retry of deferred journal cleanup is idempotent without attempting a new save",repeated==0&&retry.Revision==102&&File.ReadAllText(sample.Store.FilePath)==before&&!File.Exists(held));
        }
        return new{allPass=true,checks,fixture,physicalOperation="File.Move on newly created fixture files only; no Shell, clipboard or user files",scope="Partial success, no-op cancellation, one revision per batch, failed state write preservation and idempotent deferred cleanup"};
    }
    private static object TestTransfers(string root,bool recycle)
    {
        string fixture=Path.Combine(root,"evidence","desktop-feedback",DateTime.Now.ToString("yyyy-MM-dd"),"group-drop-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        foreach(bool copy in new[]{false,true})
        {
            string scope=Path.Combine(fixture,copy?"copy":"move"),left=Path.Combine(scope,"left"),right=Path.Combine(scope,"right"),destination=Path.Combine(scope,"destination");
            Directory.CreateDirectory(left);Directory.CreateDirectory(right);Directory.CreateDirectory(destination);
            string a=Path.Combine(left,"a.txt"),b=Path.Combine(right,"b.txt"),unselected=Path.Combine(left,"unselected.txt");
            File.WriteAllText(a,"Owned group first");File.WriteAllText(b,"Owned group second");File.WriteAllText(unselected,"Untouched sibling");
            ShellClipboardCommands.TransferSelection([a,b],destination,copy,false);
            Check("one cross-parent group "+(copy?"copy":"move")+" transfers both files",File.ReadAllText(Path.Combine(destination,"a.txt"))=="Owned group first"&&File.ReadAllText(Path.Combine(destination,"b.txt"))=="Owned group second"&&File.Exists(a)==copy&&File.Exists(b)==copy&&File.ReadAllText(unselected)=="Untouched sibling");
            if(copy)
            {
                ShellClipboardCommands.TransferSelection([a,unselected],left,true,false);
                Check("Control-copy into the original directory creates two distinct siblings",Directory.GetFiles(left).Length==4&&File.ReadAllText(a)=="Owned group first"&&File.ReadAllText(unselected)=="Untouched sibling");
            }
        }
        string parent=Path.Combine(fixture,"validate-parent"),child=Path.Combine(parent,"child");Directory.CreateDirectory(child);string nested=Path.Combine(child,"nested.txt");File.WriteAllText(nested,"Owned nested validation");
        bool nestedRejected=false;try{ShellClipboardCommands.ValidateSelectionTransfer([parent,nested],fixture);}catch(LayoutEditException){nestedRejected=true;}
        Check("nested selected folder and child reject before any file operation",nestedRejected&&File.ReadAllText(nested)=="Owned nested validation");
        bool selfRejected=false;try{ShellClipboardCommands.ValidateSelectionTransfer([parent],child);}catch(LayoutEditException){selfRejected=true;}
        Check("destination inside a selected folder rejects before any file operation",selfRejected&&Directory.Exists(child));
        string sequence=Path.Combine(fixture,"ten-folders"),sequenceTarget=Path.Combine(sequence,"destination");Directory.CreateDirectory(sequenceTarget);
        var sequenceTimes=new List<double>();
        for(int i=0;i<10;i++){
            string name="owned-folder-"+i,source=Path.Combine(sequence,name);Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"keep.txt"),"Owned sequential payload "+i);
            var clock=System.Diagnostics.Stopwatch.StartNew();var moved=JsonSerializer.SerializeToElement(ShellClipboardCommands.TransferSelection([source],sequenceTarget,false,true));sequenceTimes.Add(clock.Elapsed.TotalMilliseconds);
            Check("consecutive physical folder "+i+" completes once without losing payload",moved.GetProperty("ok").GetBoolean()&&!Directory.Exists(source)&&File.ReadAllText(Path.Combine(sequenceTarget,name,"keep.txt"))=="Owned sequential payload "+i);
        }
        Check("all ten physical folders remain independently present",Directory.GetDirectories(sequenceTarget).Length==10);
        checks.Add(new{name="ten real Shell folder drops elapsed",pass=true,milliseconds=sequenceTimes});
        string failure=Path.Combine(fixture,"failed-source");Directory.CreateDirectory(failure);File.WriteAllText(Path.Combine(failure,"keep.txt"),"Owned failed payload");
        bool invalidDestination=false;try{ShellClipboardCommands.TransferSelection([failure],Path.Combine(fixture,"missing-destination"),false,true);}catch(LayoutEditException){invalidDestination=true;}
        Check("failed destination leaves real folder and payload at source",invalidDestination&&File.ReadAllText(Path.Combine(failure,"keep.txt"))=="Owned failed payload");
        if(recycle)
        {
            string left=Path.Combine(fixture,"recycle-left"),right=Path.Combine(fixture,"recycle-right");Directory.CreateDirectory(left);Directory.CreateDirectory(right);
            string unique="magic-group-recycle-"+Guid.NewGuid().ToString("N"),a=Path.Combine(left,unique+"-A.txt"),b=Path.Combine(right,unique+"-B.txt");
            File.WriteAllText(a,"Owned recycle first");File.WriteAllText(b,"Owned recycle second");
            // Include Shift explicitly to prove it cannot request permanent
            // deletion in this community group-drop path.
            ShellClipboardCommands.TransferSelection([a,b],ShellClipboardCommands.RecycleBinTarget,false,true);
            Check("normal recycle removes both cross-parent source files",!File.Exists(a)&&!File.Exists(b));
            string c=Path.Combine(left,unique+"-C.txt"),d=Path.Combine(right,unique+"-D.txt");File.WriteAllText(c,"Owned explicit recycle first");File.WriteAllText(d,"Owned explicit recycle second");
            bool incompleteRejected=false;
            try{ShellFileMove.RecycleMany([c,Path.Combine(right,unique+"-missing.txt")]);}catch(LayoutEditException){incompleteRejected=true;}
            Check("explicit recycle rejects an invalid complete selection before moving any member",incompleteRejected&&File.Exists(c)&&File.Exists(d));
            ShellFileMove.RecycleMany([c,d]);
            Check("explicit IFileOperation batch removes both cross-parent sources",!File.Exists(c)&&!File.Exists(d));
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;object? bin=null,entries=null;var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                bin=shell.NameSpace(10);entries=((dynamic)bin!).Items();
                foreach(object item in (System.Collections.IEnumerable)entries)
                {
                    try{string name=(string)((dynamic)item).Name;if(name.StartsWith(unique,StringComparison.Ordinal))found.Add(name);}
                    finally{Marshal.ReleaseComObject(item);}
                }
            }
            finally{if(entries is not null)Marshal.ReleaseComObject(entries);if(bin is not null)Marshal.ReleaseComObject(bin);Marshal.ReleaseComObject(shell);}
            Check("both fixtures are present in Recycle Bin when Shift is requested",found.Any(name=>name.StartsWith(unique+"-A",StringComparison.Ordinal))&&found.Any(name=>name.StartsWith(unique+"-B",StringComparison.Ordinal)));
            Check("explicit IFileOperation batch fixtures are present in Recycle Bin",found.Any(name=>name.StartsWith(unique+"-C",StringComparison.Ordinal))&&found.Any(name=>name.StartsWith(unique+"-D",StringComparison.Ordinal)));
        }
        return new{allPass=true,checks,fixture,recycleInvoked=recycle,systemClipboardWritten=false,scope="Real one-selection/one-target native drops and explicit IFileOperation recycling on owned fixtures; no actual desktop files touched"};
    }
}
