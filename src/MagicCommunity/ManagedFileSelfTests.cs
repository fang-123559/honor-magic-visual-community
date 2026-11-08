using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class ManagedFileSelfTests
{
    public static void Run(string fixture,Action<string,Action> check,Action<bool,string> expect)
    {
        (string Desktop,ManagedDesktopFiles Manager,LayoutState State) Setup(string name)
        {
            string root=Path.Combine(fixture,"managed",name),desktop=Path.Combine(root,"Desktop");Directory.CreateDirectory(desktop);
            return(desktop,new(Path.Combine(root,"data"),[desktop]),new(){Boards=[new(){Id="fixture",Boxes=[new(){Id="box",Name="收纳"}]}]});
        }
        static void Add(LayoutState state,IReadOnlyList<CollectedFile> files)=>state.Boards[0].Boxes[0].Items.AddRange(files.Select(file=>file.Item()));
        check("managed-file-and-directory-rename-and-restore",()=>
        {
            var test=Setup("roundtrip");string file=Path.Combine(test.Desktop,"测试.lnk"),folder=Path.Combine(test.Desktop,"资料");File.WriteAllText(file,"shortcut fixture bytes");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"内容.txt"),"directory retained");
            var identity=ManagedDesktopFiles.NativeIdentity(file);
            test.Manager.Collect([ShellDesktop.FromPath(file),ShellDesktop.FromPath(folder)],files=>Add(test.State,files),()=>test.State);
            var items=test.State.Boards[0].Boxes[0].Items.ToArray();expect(!File.Exists(file)&&!Directory.Exists(folder)&&items.Length==2,"source remained or layout missing");
            expect(ManagedDesktopFiles.NativeIdentity(items[0].Target)==identity&&File.ReadAllText(items[0].Target)=="shortcut fixture bytes","file copied or altered");
            var restarted=new ManagedDesktopFiles(Path.Combine(Path.GetDirectoryName(test.Desktop)!,"data"),[test.Desktop]);restarted.Recover(test.State);expect(restarted.Problem is null&&restarted.IsManaged(items[0].Target),"restart lost ownership");
            restarted.Restore(items,_=>test.State.Boards[0].Boxes[0].Items.Clear(),()=>test.State);
            expect(File.ReadAllText(file)=="shortcut fixture bytes"&&File.ReadAllText(Path.Combine(folder,"内容.txt"))=="directory retained"&&test.State.Boards[0].Boxes[0].Items.Count==0,"restore lost data");
        });
        check("managed-layout-save-failure-restores-entire-batch",()=>
        {
            var test=Setup("save-failure");var files=new[]{"a.txt","b.txt"}.Select(name=>Path.Combine(test.Desktop,name)).ToArray();foreach(var file in files)File.WriteAllText(file,"keep");
            try{test.Manager.Collect(files.Select(ShellDesktop.FromPath).ToArray(),_=>throw new IOException("injected layout save failure"),()=>test.State);throw new Exception("save failure accepted");}catch(LayoutEditException){}
            expect(files.All(file=>File.ReadAllText(file)=="keep")&&test.Manager.Problem is null&&test.State.Boards[0].Boxes[0].Items.Count==0,"batch rollback incomplete");
        });
        check("managed-restore-name-conflict-never-overwrites",()=>
        {
            var test=Setup("name-conflict");string path=Path.Combine(test.Desktop,"same.txt");File.WriteAllText(path,"collected");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>Add(test.State,files),()=>test.State);var item=test.State.Boards[0].Boxes[0].Items[0];File.WriteAllText(path,"new desktop file");
            try{test.Manager.Restore([item],_=>test.State.Boards[0].Boxes[0].Items.Clear(),()=>test.State);throw new Exception("conflict accepted");}catch(LayoutEditException){}
            expect(File.ReadAllText(path)=="new desktop file"&&File.ReadAllText(item.Target)=="collected"&&test.State.Boards[0].Boxes[0].Items.Count==1,"conflict overwrote data");
            var after=StateStore.Copy(test.State);after.Boards[0].Boxes[0].Items.Clear();try{test.Manager.ProtectReferences(test.State,after);throw new Exception("owned reference silently removed");}catch(LayoutEditException){}
        });
        check("managed-partial-batch-sharing-failure-reverts-first-file",()=>
        {
            var test=Setup("sharing");string first=Path.Combine(test.Desktop,"first.txt"),second=Path.Combine(test.Desktop,"second.txt");File.WriteAllText(first,"first");File.WriteAllText(second,"second");
            using(var locked=new FileStream(second,FileMode.Open,FileAccess.Read,FileShare.Read))
            {try{test.Manager.Collect([ShellDesktop.FromPath(first),ShellDesktop.FromPath(second)],files=>Add(test.State,files),()=>test.State);throw new Exception("locked move accepted");}catch(Exception error)when(error is LayoutEditException or System.ComponentModel.Win32Exception){} }
            expect(File.ReadAllText(first)=="first"&&File.ReadAllText(second)=="second"&&test.State.Boards[0].Boxes[0].Items.Count==0,"partial failure lost source");
            string journal=Directory.GetFiles(Path.Combine(Path.GetDirectoryName(test.Desktop)!,"data","collection-operations"),"*.json").Single();
            expect(JsonSerializer.Deserialize<CollectionOperation>(File.ReadAllText(journal),StateStore.Json)!.Status=="rolled_back","sharing check did not reach transaction rollback");
        });
        check("managed-prepared-journal-recovers-before-and-after-layout-commit",()=>
        {
            foreach(bool saved in new[]{false,true})
            {
                var test=Setup("crash-"+saved);string path=Path.Combine(test.Desktop,"recover.txt");File.WriteAllText(path,"keep");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>Add(test.State,files),()=>test.State);string stored=test.State.Boards[0].Boxes[0].Items[0].Target;
                string data=Path.Combine(Path.GetDirectoryName(test.Desktop)!,"data"),journal=Directory.GetFiles(Path.Combine(data,"collection-operations"),"*.json").Single();var operation=JsonSerializer.Deserialize<CollectionOperation>(File.ReadAllText(journal),StateStore.Json)!;operation.Status="prepared";File.WriteAllText(journal,JsonSerializer.Serialize(operation,StateStore.Json));
                if(!saved)test.State.Boards[0].Boxes[0].Items.Clear();var restarted=new ManagedDesktopFiles(data,[test.Desktop]);restarted.Recover(test.State);
                expect(restarted.Problem is null&&(saved?File.Exists(stored)&&!File.Exists(path):File.Exists(path)&&!File.Exists(stored)),"prepared recovery chose wrong direction");
            }
        });
        check("managed-observer-error-after-durable-layout-does-not-undo-files",()=>
        {
            var test=Setup("observer");string path=Path.Combine(test.Desktop,"observer.txt");File.WriteAllText(path,"keep");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>{Add(test.State,files);throw new InvalidOperationException("observer error after save");},()=>test.State);
            expect(!File.Exists(path)&&File.ReadAllText(test.State.Boards[0].Boxes[0].Items[0].Target)=="keep","committed files rolled back behind layout");
        });
        check("managed-restore-layout-failure-keeps-collected-file",()=>
        {
            var test=Setup("restore-failure");string path=Path.Combine(test.Desktop,"restore.txt");File.WriteAllText(path,"retained");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>Add(test.State,files),()=>test.State);var item=test.State.Boards[0].Boxes[0].Items[0];
            try{test.Manager.Restore([item],_=>throw new IOException("injected restore save failure"),()=>test.State);throw new Exception("restore save failure accepted");}catch(LayoutEditException){}
            expect(!File.Exists(path)&&File.ReadAllText(item.Target)=="retained"&&test.State.Boards[0].Boxes[0].Items.Count==1,"restore failure did not revert");
        });
        check("managed-restore-prepared-journal-recovers-both-directions",()=>
        {
            foreach(bool saved in new[]{false,true})
            {
                var test=Setup("restore-crash-"+saved);string path=Path.Combine(test.Desktop,"restore.txt");File.WriteAllText(path,"retained");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>Add(test.State,files),()=>test.State);var item=test.State.Boards[0].Boxes[0].Items[0];test.Manager.Restore([item],_=>test.State.Boards[0].Boxes[0].Items.Clear(),()=>test.State);
                string data=Path.Combine(Path.GetDirectoryName(test.Desktop)!,"data");var row=Directory.GetFiles(Path.Combine(data,"collection-operations"),"*.json").Select(file=>(Path:file,Operation:JsonSerializer.Deserialize<CollectionOperation>(File.ReadAllText(file),StateStore.Json)!)).Single(row=>row.Operation.Kind=="restore");row.Operation.Status="prepared";File.WriteAllText(row.Path,JsonSerializer.Serialize(row.Operation,StateStore.Json));
                if(!saved)test.State.Boards[0].Boxes[0].Items.Add(item);var restarted=new ManagedDesktopFiles(data,[test.Desktop]);restarted.Recover(test.State);
                expect(restarted.Problem is null&&(saved?File.ReadAllText(path)=="retained"&&!File.Exists(item.Target):!File.Exists(path)&&File.ReadAllText(item.Target)=="retained"),"restore recovery chose wrong direction");
            }
        });
        check("managed-tampered-journal-blocks-without-moving-files",()=>
        {
            var test=Setup("tamper");string path=Path.Combine(test.Desktop,"keep.txt");File.WriteAllText(path,"keep");test.Manager.Collect([ShellDesktop.FromPath(path)],files=>Add(test.State,files),()=>test.State);string stored=test.State.Boards[0].Boxes[0].Items[0].Target,data=Path.Combine(Path.GetDirectoryName(test.Desktop)!,"data"),journal=Directory.GetFiles(Path.Combine(data,"collection-operations"),"*.json").Single();var operation=JsonSerializer.Deserialize<CollectionOperation>(File.ReadAllText(journal),StateStore.Json)!;operation.Status="prepared";operation.Files[0].Stored=Path.Combine(test.Desktop,"other.txt");File.WriteAllText(journal,JsonSerializer.Serialize(operation,StateStore.Json));
            var restarted=new ManagedDesktopFiles(data,[test.Desktop]);restarted.Recover(test.State);expect(restarted.Problem is not null&&!File.Exists(path)&&File.ReadAllText(stored)=="keep","untrusted journal caused a move");
        });
        check("managed-rejects-non-desktop-and-unowned-storage",()=>
        {
            var test=Setup("boundaries");string parent=Path.GetDirectoryName(test.Desktop)!,outside=Path.Combine(parent,"outside.txt");File.WriteAllText(outside,"outside");
            try{test.Manager.Collect([ShellDesktop.FromPath(outside)],_=>{},()=>test.State);throw new Exception("non desktop accepted");}catch(LayoutEditException){}
            Directory.CreateDirectory(Path.Combine(parent,"Magic 视界收纳"));string path=Path.Combine(test.Desktop,"keep.txt");File.WriteAllText(path,"keep");try{test.Manager.Collect([ShellDesktop.FromPath(path)],_=>{},()=>test.State);throw new Exception("unowned storage accepted");}catch(LayoutEditException){}
            expect(File.ReadAllText(path)=="keep"&&File.ReadAllText(outside)=="outside","boundary altered files");
        });
        check("shell-file-operation-single-file-and-folder-without-elevation",()=>
        {
            var test=Setup("shell-move");string target=Path.Combine(Path.GetDirectoryName(test.Desktop)!,"destination");Directory.CreateDirectory(target);
            string file=Path.Combine(test.Desktop,"shell.txt");File.WriteAllText(file,"unchanged");var identity=ManagedDesktopFiles.NativeIdentity(file);ShellFileMove.Move(file,Path.Combine(target,"shell.txt"));
            expect(!File.Exists(file)&&File.ReadAllText(Path.Combine(target,"shell.txt"))=="unchanged"&&ManagedDesktopFiles.NativeIdentity(Path.Combine(target,"shell.txt"))==identity,"Shell move copied or lost file");
            string folder=Path.Combine(test.Desktop,"folder");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"inside.txt"),"inside");ShellFileMove.Move(folder,Path.Combine(target,"folder"));expect(!Directory.Exists(folder)&&File.ReadAllText(Path.Combine(target,"folder","inside.txt"))=="inside","Shell folder move failed");
            File.WriteAllText(file,"source");try{ShellFileMove.Move(file,Path.Combine(target,"shell.txt"));throw new Exception("Shell collision accepted");}catch(LayoutEditException){}expect(File.ReadAllText(file)=="source"&&File.ReadAllText(Path.Combine(target,"shell.txt"))=="unchanged","Shell collision overwrote data");
        });
        check("managed-reentrant-move-is-rejected-and-busy-clears",()=>
        {
            var test=Setup("reentrant");string first=Path.Combine(test.Desktop,"first.txt"),second=Path.Combine(test.Desktop,"second.txt");File.WriteAllText(first,"first");File.WriteAllText(second,"second");
            test.Manager.Collect([ShellDesktop.FromPath(first)],files=>{expect(test.Manager.IsBusy,"transaction not busy");try{test.Manager.Collect([ShellDesktop.FromPath(second)],_=>{},()=>test.State);throw new Exception("nested move accepted");}catch(LayoutEditException){}Add(test.State,files);},()=>test.State);
            expect(!test.Manager.IsBusy&&File.ReadAllText(second)=="second"&&test.State.Boards[0].Boxes[0].Items.Count==1,"reentrant move changed data or busy stuck");
        });
    }
}
