using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MagicCommunity;
public static class SelfTests
{
    public static void Run(string root,string output)
    {
        var folder=Path.Combine(root,"evidence","preview","state-tests-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(folder);
        var results=new List<object>();
        void Check(string name,Action test){test();results.Add(new{name,pass=true});}
        void Expect(bool value,string text){if(!value)throw new InvalidOperationException(text);}
        Check("application-search-discovers-portable-and-shortcut-without-indexing-documents",()=>{
            string apps=Path.Combine(folder,"search","Tools");Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(apps,"geek.exe"),"owned fixture, never executed");File.WriteAllText(Path.Combine(apps,"Editor.lnk"),"owned fixture");File.WriteAllText(Path.Combine(apps,"private.docx"),"not indexed");
            var files=ApplicationSearchFiles.Read([Path.GetDirectoryName(apps)!,Path.GetDirectoryName(apps)!],CancellationToken.None);
            Expect(files.Length==2&&files.Any(f=>Path.GetFileName(f)=="geek.exe"),"Portable executable missing or duplicate root repeated");
            Expect(ApplicationSearchFiles.Matches("Geek",files.First(f=>f.EndsWith(".exe")),"GEEK.EXE"),"Executable query cannot match file name");
        });
        var store=new StateStore(Path.Combine(folder,"normal"));
        var state=new LayoutState{Boards=[new(){Id="screen-stable-a",Name="主屏",Boxes=[new(){Id="box-a",Name="工作"},new(){Id="box-b",Name="待整理"}]}]};
        var item=new DesktopItem{Name="示例.txt",Target=Path.Combine(folder,"示例.txt")};File.WriteAllText(item.Target,"source must remain untouched");
        Check("atomic-save-and-restart",()=>{LayoutActions.Add(state.Boards[0].Boxes[0],[item]);store.Save(state);Expect(store.Load().Boards[0].Boxes[0].Items[0].Target==item.Target,"restart lost reference");});
        Check("deduplicate-and-move-between-boxes",()=>{LayoutActions.Add(state.Boards[0].Boxes[0],[item]);Expect(state.Boards[0].Boxes[0].Items.Count==1,"duplicate reference");LayoutActions.Move(state,"box-a",item.Target,"box-b");store.Save(state);Expect(state.Boards[0].Boxes[0].Items.Count==0&&state.Boards[0].Boxes[1].Items.Count==1,"move failed");});
        Check("move-to-existing-reference-keeps-one",()=>{LayoutActions.Add(state.Boards[0].Boxes[0],[item]);LayoutActions.Move(state,"box-a",item.Target,"box-b");Expect(state.Boards[0].Boxes[1].Items.Count==1,"duplicate destination");});
        Check("remove-reference-never-changes-file",()=>{state.Boards[0].Boxes[1].Items.Clear();store.Save(state);Expect(File.ReadAllText(item.Target)=="source must remain untouched","source altered");});
        Check("invalid-model-does-not-overwrite-layout",()=>{var before=File.ReadAllBytes(store.FilePath);var invalid=StateStore.Copy(state);invalid.Boards[0].Boxes[0].Items.Add(new(){Name="relative",Target="relative.txt"});try{store.Save(invalid);throw new Exception("invalid accepted");}catch(InvalidDataException){}Expect(before.SequenceEqual(File.ReadAllBytes(store.FilePath)),"invalid overwrote file");});
        Check("write-failure-keeps-prior-layout",()=>{var before=File.ReadAllBytes(store.FilePath);using(var lockFile=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){try{store.Save(state);throw new Exception("locked file accepted");}catch(IOException){}}Expect(before.SequenceEqual(File.ReadAllBytes(store.FilePath)),"write failure altered file");});
        Check("future-schema-is-readonly-and-preserved",()=>{var future=new StateStore(Path.Combine(folder,"future"));Directory.CreateDirectory(future.DirectoryPath);File.WriteAllText(future.FilePath,"{\"SchemaVersion\":10,\"Secret\":\"retain\"}");var before=File.ReadAllText(future.FilePath);var loaded=future.Load();Expect(future.ReadOnly,"future writable");try{future.Save(loaded);throw new Exception("future overwritten");}catch(InvalidOperationException){}Expect(before==File.ReadAllText(future.FilePath),"future data lost");});
        Check("corruption-recovery-is-explicit-and-retains-original",()=>{File.WriteAllText(store.FilePath,"corrupt");var recovered=new StateStore(store.DirectoryPath);var backup=recovered.Load();Expect(recovered.ReadOnly&&recovered.BackupAvailable,"corrupt data not protected");Expect(File.ReadAllText(store.FilePath)=="corrupt","silent repair");recovered.RestoreBackup();Expect(!recovered.ReadOnly&&Directory.GetFiles(store.DirectoryPath,"*.corrupt-*").Length==1,"recovery lost original");});
        Check("disconnected-board-data-survives-serialization",()=>{var restored=store.Load();restored.Boards.Add(new(){Id="screen-stable-disconnected",Name="已断开",Boxes=[new(){Name="保留",Items=[item]}]});store.Save(restored);Expect(store.Load().Boards.Any(x=>x.Id=="screen-stable-disconnected"&&x.Boxes[0].Items.Count==1),"disconnected data lost");});
        CompositionParityTests.Run(folder,Check,Expect);
        Check("shortcut-new-upgraded-disabled-corrupt-and-readonly",()=>{
            string fresh=Path.Combine(folder,"shortcut-fresh"),upgrade=Path.Combine(folder,"shortcut-upgrade");
            Expect(ShortcutPreferences.Initialize(fresh,false).Modes==0,"Fresh installations inherit Windows shortcuts.");
            var receipt=Path.Combine(upgrade,"runtime/system/native-ready.json");
            StateStore.AtomicWrite(receipt,JsonSerializer.Serialize(new{schemaVersion=1,protocol=NativeSystemClient.Protocol,version="0.4.0",adapterPid=1,backendPid=2}),null);
            Expect(ShortcutPreferences.Initialize(upgrade,false)==new ShortcutPreferences(true,false),"Verified upgrade preserves standalone Win only.");
            new ShortcutPreferences(false,true).Save(upgrade);
            Expect(ShortcutPreferences.Initialize(upgrade,false)==new ShortcutPreferences(false,true),"An explicit choice survives migration.");
            Expect(ShortcutPreferences.Initialize(upgrade,true).Modes==0,"Readonly store must fail open.");
            var settings=Path.Combine(upgrade,"data/keyboard-shortcuts.json");
            var attributes=File.GetAttributes(settings);
            try {
                File.SetAttributes(settings,attributes|FileAttributes.ReadOnly);
                Expect(ShortcutPreferences.Initialize(upgrade,false).Modes==0,"A read-only preference file cannot capture system keys.");
            } finally { File.SetAttributes(settings,attributes); }
            File.WriteAllText(settings,new string('x',8193));
            Expect(ShortcutPreferences.Initialize(upgrade,false).Modes==0,"An oversized preference file must fail open.");
            File.WriteAllText(settings,"{");Expect(ShortcutPreferences.Initialize(upgrade,false).Modes==0,"Damaged JSON must fail open.");
            File.WriteAllText(settings,"{\"schema\":1,\"win\":true}");Expect(ShortcutPreferences.Initialize(upgrade,false).Modes==0,"Missing fields must fail open.");
        });
        Check("volume-feedback-is-only-for-confirmed-output-release",()=>{
            Expect(NativeVolumeFeedback.ShouldPlay(true,"audio-action",1,false),"Confirmed master release should play.");
            foreach(var input in new[]{(false,"audio-action",.5,false),(true,"audio-action",0d,false),(true,"audio-action",.5,true),(true,"brightness-action",.5,false),(true,"microphone-action",.5,false)})
                Expect(!NativeVolumeFeedback.ShouldPlay(input.Item1,input.Item2,input.Item3,input.Item4),"Gesture or unrelated operation played feedback.");
        });
        GridSelfTests.Run(folder,Check,Expect);
        ManagedFileSelfTests.Run(folder,Check,Expect);
        HostSelectionSelfTests.Run(folder,Check,Expect);
        Check("desktop-drop-exact-reference-without-moving-source",()=>
        {
            var desktop=new[]{item};var resolved=DesktopDrop.Resolve(desktop,[Path.GetFileName(item.Target),Path.GetFileName(item.Target)]);
            Expect(resolved.Length==1&&resolved[0].Target==item.Target&&File.ReadAllText(item.Target)=="source must remain untouched","desktop drop altered or misidentified source");
        });
        Check("desktop-drop-rejects-ambiguous-missing-and-path-input",()=>
        {
            foreach(var names in new[]{new[]{Path.GetFileName(item.Target)},new[]{"missing.txt"},new[]{"../示例.txt"}})
            {
                try{DesktopDrop.Resolve([item,new(){Name="同名",Target=Path.Combine(folder,"other",Path.GetFileName(item.Target))}],names);throw new Exception("ambiguous or unknown drop accepted");}catch(LayoutEditException){}
            }
        });
        var image=ExampleImage();image.Caption="截图说明：红色方框";
        var feedbackRoot=Path.Combine(folder,"feedback");
        Check("image-report-preserves-bytes-caption-and-relative-paths",()=>
        {
            string source=Path.Combine(folder,"标注.png");File.WriteAllBytes(source,image.Bytes);
            var selected=FeedbackStore.ReadFile(source);selected.Caption=image.Caption;var original=File.ReadAllBytes(source);
            string path=FeedbackStore.Save(feedbackRoot,new{description="检查截图",version=AppController.Version},[selected,selected]);
            using var report=JsonDocument.Parse(File.ReadAllText(path));var attachments=report.RootElement.GetProperty("attachments");
            Expect(attachments.GetArrayLength()==2,"duplicate names lost an image");
            foreach(var attachment in attachments.EnumerateArray())
            {
                string relative=attachment.GetProperty("file").GetString()!;
                Expect(!Path.IsPathRooted(relative)&&!relative.Contains(".."),"absolute or escaping path");
                var saved=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!,relative));
                Expect(saved.SequenceEqual(original),"annotation pixels or image metadata changed");
                Expect(attachment.GetProperty("sha256").GetString()==Convert.ToHexString(SHA256.HashData(saved)).ToLowerInvariant(),"image digest wrong");
                Expect(attachment.GetProperty("caption").GetString()==image.Caption,"caption lost");
            }
            Expect(File.ReadAllBytes(source).SequenceEqual(original),"original image changed");
            Expect(!File.ReadAllText(path).Contains(source,StringComparison.OrdinalIgnoreCase),"source path included");
        });
        Check("invalid-images-and-count-limit-do-not-commit",()=>
        {
            int before=Directory.GetDirectories(feedbackRoot).Length;
            try{FeedbackStore.FromBytes("pretend.png",[1,2,3]);throw new Exception("invalid image accepted");}catch(NotSupportedException){}catch(System.IO.FileFormatException){}
            try{FeedbackStore.Save(feedbackRoot,new{description="too many"},Enumerable.Repeat(image,7).ToArray());throw new Exception("count limit ignored");}catch(InvalidDataException){}
            Expect(Directory.GetDirectories(feedbackRoot).Length==before,"invalid report partially committed");
        });
        Check("text-only-feedback-still-supported",()=>
        {
            string path=FeedbackStore.Save(feedbackRoot,new{description="只有文字",status="待评估"},[]);
            using var report=JsonDocument.Parse(File.ReadAllText(path));Expect(report.RootElement.GetProperty("attachments").GetArrayLength()==0,"text report failed");
        });
        Check("feedback-write-failure-keeps-input-for-retry",()=>
        {
            string blocked=Path.Combine(folder,"blocked-feedback");File.WriteAllText(blocked,"keep");var before=image.Bytes.ToArray();
            try{FeedbackStore.Save(blocked,new{description="retry"},[image]);throw new Exception("blocked write succeeded");}catch(IOException){}
            Expect(image.Caption=="截图说明：红色方框"&&image.Bytes.SequenceEqual(before)&&File.ReadAllText(blocked)=="keep","draft or prior file changed");
        });
        StateStore.AtomicWrite(Path.GetFullPath(output),JsonSerializer.Serialize(new{version=AppController.Version,at=DateTimeOffset.Now,allPass=true,checks=results,testDirectory=folder,scope="Isolated state/storage tests; no user input acceptance"},StateStore.Json),null);
    }
    internal static FeedbackImage ExampleImage()
    {
        var visual=new DrawingVisual();using(var draw=visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.AliceBlue,null,new System.Windows.Rect(0,0,640,360));
            draw.DrawRectangle(Brushes.White,new Pen(Brushes.SlateGray,2),new System.Windows.Rect(40,40,560,280));
            draw.DrawRectangle(null,new Pen(Brushes.Red,5),new System.Windows.Rect(80,90,200,100));
        }
        var bitmap=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);
        return FeedbackStore.FromBytes("标注截图示例.png",stream.ToArray());
    }
}
