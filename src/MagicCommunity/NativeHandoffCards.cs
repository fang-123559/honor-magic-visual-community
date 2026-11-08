using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Node=MagicCommunity.NativeHandoffStorage.Node;
namespace MagicCommunity;
internal sealed class NativeHandoffCards : IDisposable{
    private readonly AppController controller;private readonly string root;private readonly Action changed;private readonly Func<DesktopItem,string?>? icon;
    private readonly Dictionary<string,bool> present=new(StringComparer.OrdinalIgnoreCase);private readonly Dictionary<string,(string Card,string Path)> retired=new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate=new();private string demandKey="";private string[] desired=[];private Task? sampling;private int copyPending;private bool disposed,listening;
    private int editorPreparing;private long cleanupRevision=-1;private int cleanupBusy;private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;private readonly HashSet<string> retiredOwners=new(StringComparer.Ordinal);
    private Window? editor;private string editing="";private HwndSource? clipboardWindow;
    private readonly List<(Image Image,string Path)> editorImages=[];private Action? saveEditor;
    internal NativeHandoffCards(AppController controller,Action changed,Func<DesktopItem,string?>? icon){this.controller=controller;root=controller.Store.DirectoryPath;this.changed=changed;this.icon=icon;ReadRetired();}
    private DesktopCard Require(string board,string id){var card=DesktopCards.Require(controller.State.Boards.Single(b=>b.Id==board),id);if(card.Type!="system.handoff")throw new LayoutEditException("交接卡片已变化。");return card;}
    private static string Stamp(JsonElement settings)=>JsonNode.Parse(settings.GetRawText())!.ToJsonString();
    internal void Demand(IEnumerable<DesktopCard> cards){
        if(disposed)return;if(cleanupRevision!=controller.State.Revision){cleanupRevision=controller.State.Revision;if(retired.Count>0)dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(CollectRetired));}
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var stamp=new StringBuilder();
        foreach(var card in cards.Where(c=>c.Type=="system.handoff")){stamp.Append(card.Id).Append(card.Settings.GetRawText());try{foreach(var n in NativeHandoffStorage.Read(card.Settings,root,card.Id).Nodes.Where(n=>n.Type=="file").Take(16))if(paths.Count<64)paths.Add(n.Path);}catch(Exception e)when(e is not OutOfMemoryException){}}
        string key=stamp.ToString();lock(gate){if(disposed||demandKey==key)return;demandKey=key;desired=paths.ToArray();foreach(string old in present.Keys.Except(paths,StringComparer.OrdinalIgnoreCase).ToArray())present.Remove(old);if(sampling is {IsCompleted:false})return;sampling=Task.Run(Sample);}
    }
    internal void ObservePresence(string path,bool exists){bool different;lock(gate){if(disposed||!desired.Contains(path,StringComparer.OrdinalIgnoreCase))return;different=!present.TryGetValue(path,out bool old)||old!=exists;present[path]=exists;}if(different)changed();}
    private void Sample(){
        while(true){string[] wanted;string version;lock(gate){if(disposed)return;wanted=desired;version=demandKey;}var found=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);foreach(string path in wanted){if(disposed)return;found[path]=File.Exists(path);}
            lock(gate){if(disposed)return;if(version!=demandKey)continue;present.Clear();foreach(var p in found)present[p.Key]=p.Value;}changed();return;
        }
    }
    internal object Projection(IEnumerable<DesktopCard> cards){
        int pngBudget=65536;var values=new List<object>();
        foreach(var card in cards.Where(c=>c.Type=="system.handoff")){
            try{var state=NativeHandoffStorage.Read(card.Settings,root,card.Id);int chars=state.Nodes.Where(n=>n.Type=="text").Sum(n=>n.Chars),images=state.Nodes.Count(n=>n.Type=="file"&&n.Kind=="image"),files=state.Nodes.Count(n=>n.Type=="file"&&n.Kind!="image"),textBudget=1024;var flow=new List<object>();
                foreach(var node in state.Nodes.Take(16)){
                    if(node.Type=="text"){string preview=node.Text[..Math.Min(node.Text.Length,textBudget)];textBudget-=preview.Length;flow.Add(new{t="text",v=preview,chars=node.Chars});if(textBudget==0)break;}
                    else{bool? exists;lock(gate)exists=present.TryGetValue(node.Path,out bool found)?found:null;string? uri=node.Kind=="image"&&exists!=false?icon?.Invoke(new DesktopItem{Name=node.Name,Target=node.Path}):null;string? png=uri is not null&&uri.StartsWith("data:image/png;base64,",StringComparison.Ordinal)&&uri.Length-22<=pngBudget&&uri.Length<22000?uri[22..]:null;if(png is not null)pngBudget-=png.Length;flow.Add(new{t="file",name=node.Name,ext=node.Extension,kind=node.Kind,bytes=node.Bytes,path=node.Path,missing=exists==false,png});}
                }values.Add(new{id=card.Id,chars,images,files,flow,editing=editing==card.Id,invalid=state.Skipped,error=state.Skipped>0?$"有 {state.Skipped} 项格式无效，原设置保留。":""});
            }catch(Exception e)when(e is not OutOfMemoryException){values.Add(new{id=card.Id,error=e.Message,flow=Array.Empty<object>(),editing=false,chars=0,images=0,files=0});}
        }return values;
    }
    internal async Task<string> Copy(string board,string id,bool onlyText){
        if(Interlocked.CompareExchange(ref copyPending,1,0)!=0)return "正在准备上一次交接，请稍候。";try{
            var state=NativeHandoffStorage.Read(Require(board,id).Settings,root,id);if(state.Nodes.Length==0)return "还没有内容";
            var packet=await Task.Run(()=>NativeHandoffClipboard.Prepare(state.Nodes,root,id,onlyText));if(disposed)return "";
            if(!onlyText)foreach(var node in state.Nodes.Where(n=>n.Type=="file"))ObservePresence(node.Path,packet.Files.Contains(node.Path,StringComparer.OrdinalIgnoreCase));
            NativeHandoffClipboard.Put(ClipboardWindow().Handle,packet,onlyText);int attachments=state.Nodes.Count(n=>n.Type=="file"),missing=attachments-packet.Files.Length;
            return onlyText?"已复制 · 仅文本":(state.Nodes.Any(n=>n.Type=="text")?"已复制 · 文本":"已复制")+(packet.Files.Length>0?$" + {packet.Files.Length} 个附件":"")+(missing>0?$" · {missing} 个缺失或重复附件未加入文件对象":"");
        }finally{Volatile.Write(ref copyPending,0);}
    }
    private HwndSource ClipboardWindow(){if(clipboardWindow is not null)return clipboardWindow;clipboardWindow=new(new HwndSourceParameters("Magic handoff clipboard"){ParentWindow=new IntPtr(-3)});clipboardWindow.AddHook((IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled)=>{if(message==0x31d&&listening)Dispatcher.CurrentDispatcher.BeginInvoke(new Action(CollectRetired));return IntPtr.Zero;});return clipboardWindow;}
    private IEnumerable<string> OwnedFiles(Node[] nodes,string id)=>nodes.Select(n=>n.Type=="text"&&n.Reference.Length>0?NativeHandoffStorage.ReferencePath(root,id,n.Reference):n.Type=="file"?n.Path:"").Where(p=>p.Length>0&&NativeHandoffStorage.Owned(root,id,p));
    private HashSet<string>? Protected(){
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{foreach(var card in controller.State.Boards.SelectMany(DesktopCards.Items).Where(c=>c.Type=="system.handoff"))foreach(string p in OwnedFiles(NativeHandoffStorage.Read(card.Settings,root,card.Id).Nodes,card.Id))result.Add(p);if(File.Exists(controller.Store.BackupPath)){if(new FileInfo(controller.Store.BackupPath).Length>8*1024*1024)return null;var backup=JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(controller.Store.BackupPath),StateStore.Json);if(backup is not null)foreach(var card in backup.Boards.SelectMany(DesktopCards.Items).Where(c=>c.Type=="system.handoff"))foreach(string p in OwnedFiles(NativeHandoffStorage.Read(card.Settings,root,card.Id).Nodes,card.Id))result.Add(p);}
            // RichTextBox undo records can still own removed inline screenshots.
            if(editor is not null&&editing.Length>0){string directory=NativeHandoffStorage.DirectoryFor(root,editing);if(Directory.Exists(directory)){NativeHandoffStorage.NoLinks(root,directory);foreach(string path in Directory.EnumerateFiles(directory).Take(1024))if(NativeHandoffStorage.Owned(root,editing,path))result.Add(path);}}
            if(Clipboard.ContainsFileDropList())foreach(string? p in Clipboard.GetFileDropList())if(p is not null)result.Add(p);
        }catch(Exception e)when(e is not OutOfMemoryException){return null;}return result;
    }
    private void PersistRetired(string id){
        string directory=NativeHandoffStorage.DirectoryFor(root,id);if(!Directory.Exists(directory))return;string path=Path.Combine(directory,".retired.json");NativeHandoffStorage.NoLinks(root,path);
        StateStore.AtomicWrite(path,JsonSerializer.Serialize(new{schemaVersion=1,cardId=id,owner=NativeHandoffStorage.Owner(id),files=retired.Values.Where(v=>v.Card==id).Select(v=>Path.GetFileName(v.Path))}),null);
    }
    private void ReadRetired(){
        string directory=Path.Combine(root,"handoff");if(!Directory.Exists(directory))return;
        try{NativeHandoffStorage.NoLinks(root,directory);foreach(string owner in Directory.EnumerateDirectories(directory).Take(128)){
            string path=Path.Combine(owner,".retired.json");NativeHandoffStorage.NoLinks(root,path);if(!File.Exists(path)||new FileInfo(path).Length>131072)continue;using var doc=JsonDocument.Parse(File.ReadAllText(path));string id=doc.RootElement.GetProperty("cardId").GetString()??"";
            if(Path.GetFileName(owner)!=NativeHandoffStorage.Owner(id))continue;retiredOwners.Add(id);foreach(var value in doc.RootElement.GetProperty("files").EnumerateArray().Take(1024)){string name=value.GetString()??"";if(name!=Path.GetFileName(name))continue;string file=Path.Combine(owner,name);if(NativeHandoffStorage.Owned(root,id,file))retired[file]=(id,file);}
        }if(retired.Count>0)dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(CollectRetired));}catch(Exception e)when(e is not OutOfMemoryException){controller.Log("handoff_retired_records_unavailable",new{type=e.GetType().Name});}
    }
    private void Retire(string id,IEnumerable<string> paths){foreach(string path in paths)if(NativeHandoffStorage.Owned(root,id,path))retired[path]=(id,path);retiredOwners.Add(id);PersistRetired(id);CollectRetired();}
    private void CollectRetired(){
        if(disposed||Interlocked.CompareExchange(ref cleanupBusy,1,0)!=0)return;var keep=Protected();var next=keep is null?[]:retired.Values.Where(v=>!keep.Contains(v.Path)&&NativeHandoffStorage.Owned(root,v.Card,v.Path)).Take(32).ToArray();
        if(retired.Count>0&&!listening)listening=AddClipboardFormatListener(ClipboardWindow().Handle);else if(retired.Count==0&&listening){RemoveClipboardFormatListener(ClipboardWindow().Handle);listening=false;}
        if(next.Length==0){Volatile.Write(ref cleanupBusy,0);return;}
        var thread=new Thread(()=>{
            try{var existing=next.Where(v=>File.Exists(v.Path)&&NativeHandoffStorage.Owned(root,v.Card,v.Path)).Select(v=>v.Path).ToArray();if(existing.Length>0)ShellFileMove.RecycleMany(existing);}
            catch(Exception e)when(e is not OutOfMemoryException){controller.Log("handoff_resource_recycle_failed",new{type=e.GetType().Name});}
            finally{if(!dispatcher.HasShutdownStarted)dispatcher.BeginInvoke(new Action(()=>{int removed=0;foreach(var v in next)if(!File.Exists(v.Path)){retired.Remove(v.Path);removed++;}foreach(string id in next.Select(v=>v.Card).Distinct())PersistRetired(id);Volatile.Write(ref cleanupBusy,0);if(!disposed&&removed>0)CollectRetired();}));else Volatile.Write(ref cleanupBusy,0);}
        }){IsBackground=true,Name="Magic handoff owned resource recycling"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }
    internal void Removed(string id){
        string directory=NativeHandoffStorage.DirectoryFor(root,id);if(!Directory.Exists(directory))return;NativeHandoffStorage.NoLinks(root,directory);var owned=Directory.EnumerateFiles(directory).Take(1024).Where(p=>NativeHandoffStorage.Owned(root,id,p)).ToArray();Retire(id,owned);
    }
    internal void RefreshImages(){foreach(var image in editorImages)image.Image.Source=Bitmap(image.Path);}
    private ImageSource? Bitmap(string path){
        string? data=icon?.Invoke(new DesktopItem{Name=Path.GetFileName(path),Target=path});if(data is null||!data.StartsWith("data:image/png;base64,",StringComparison.Ordinal)||data.Length>22000)return null;
        try{var image=new BitmapImage();image.BeginInit();image.StreamSource=new MemoryStream(Convert.FromBase64String(data[22..]),false);image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=128;image.EndInit();image.Freeze();return image;}catch(Exception e)when(e is FormatException or NotSupportedException or IOException){return null;}
    }
    internal static NativeHandoffStorage.Node[] ReadDocument(FlowDocument document){
        IEnumerable<Node> Inlines(InlineCollection values){foreach(Inline inline in values){if(inline is Run run){if(run.Text.Length>0)yield return new("text",run.Text,Chars:run.Text.Length,Original:run.Tag is JsonElement raw?raw:null);}else if(inline is LineBreak)yield return new("text","\n");else if(inline is Span span)foreach(var node in Inlines(span.Inlines))yield return node;else if(inline is InlineUIContainer container&&container.Child is FrameworkElement child&&child.Tag is Node node)yield return node;}}
        var result=new List<Node>();bool first=true;foreach(var block in document.Blocks){if(!first)result.Add(new("text","\n"));first=false;if(block is Paragraph paragraph)result.AddRange(Inlines(paragraph.Inlines));else throw new LayoutEditException("请使用文字和文件的行内交接内容。");}return result.ToArray();
    }
    internal async Task Show(string board,string id){
        if(editor is {} open&&editing==id){open.Activate();return;}if(Interlocked.CompareExchange(ref editorPreparing,1,0)!=0)throw new LayoutEditException("编辑器正在准备，请稍候。");try{
        var card=Require(board,id);string originalStamp=Stamp(card.Settings);var initial=NativeHandoffStorage.Read(card.Settings,root,id);if(initial.Skipped>0)throw new LayoutEditException("交接数据含有无法识别的条目，原内容已保留，请先检查数据。");
        var expanded=await Task.Run(()=>{int bytes=0;return initial.Nodes.Select(n=>{if(n.Type!="text")return n;string text=NativeHandoffStorage.FullText(n,root,id);bytes+=Encoding.UTF8.GetByteCount(text);if(bytes>NativeHandoffStorage.CopyTextLimit)throw new LayoutEditException("文字过大，原内容保留；请使用文件附件。");return n with{Text=text,Chars=text.Length};}).ToArray();});
        if(disposed)return;if(Stamp(Require(board,id).Settings)!=originalStamp)throw new LayoutEditException("读取期间卡片已变化，请重新打开。");
        if(editor is {} existing){if(editing==id){existing.Activate();return;}existing.Close();if(editor is not null){existing.Activate();return;}}
        var window=new CommunityWindow{Title="文本交接台",Width=540,Height=390,MinWidth=420,MinHeight=260,MaxHeight=620,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};editor=window;editing=id;var theme=new NativeWpfTheme();theme.Apply(window);
        var rootPanel=new DockPanel{Margin=new Thickness(16)};window.Content=rootPanel;var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);rootPanel.Children.Add(footer);
        var message=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=theme.Muted,Margin=new(0,6,0,6)};footer.Children.Add(message);var bar=new DockPanel();footer.Children.Add(bar);var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Right);bar.Children.Add(actions);
        Button Button(string text)=>new(){Content=text,Padding=new(10,6,10,6),Margin=new(4,0,0,0)};var insert=Button("＋ 插入文件");var copy=Button("复制");var done=Button("完成");bar.Children.Add(insert);actions.Children.Add(copy);actions.Children.Add(done);
        var paragraph=new Paragraph{Margin=new(0),LineHeight=22};var document=new FlowDocument(paragraph){PagePadding=new(8),FontFamily=CommunityFont.Family,FontSize=12,IsOptimalParagraphEnabled=false};var input=new RichTextBox(document){Background=theme.Surface,Foreground=theme.Ink,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,AcceptsReturn=true,UndoLimit=20,AllowDrop=true,MinHeight=118,MaxHeight=420,BorderBrush=theme.Divider,BorderThickness=new(1)};rootPanel.Children.Add(input);
        var uncommitted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool building=true,saving=false,dirty=false;string saved=Stamp(card.Settings);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(380)};editorImages.Clear();
        Node FileNode(string path){if(!NativeHandoffStorage.LocalFile(path)||!File.Exists(path)||Directory.Exists(path))throw new LayoutEditException("只能插入本地文件；原文件不会被移动。");var info=new FileInfo(path);string extension=info.Extension.TrimStart('.').ToUpperInvariant();string name=info.Name;if(name.Length>120)name=name[..120];if(extension.Length>8)extension=extension[..8];return new("file",Path:info.FullName,Name:name,Extension:extension,Kind:new[]{".png",".jpg",".jpeg",".bmp",".gif",".webp"}.Contains(info.Extension,StringComparer.OrdinalIgnoreCase)?"image":"file",Bytes:info.Length);}
        Inline Attachment(Node node,TextPointer? insertion=null){var panel=new StackPanel{Orientation=Orientation.Horizontal};if(node.Kind=="image"){var image=new Image{Width=64,Height=46,Stretch=Stretch.UniformToFill,Source=Bitmap(node.Path)};editorImages.Add((image,node.Path));panel.Children.Add(image);}panel.Children.Add(new TextBlock{Text=node.Kind=="image"?"":node.Extension+" "+node.Name,MaxWidth=290,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new(5,0,5,0)});
            var border=new Border{Child=panel,Tag=node,Background=theme.WindowCard,BorderBrush=theme.Divider,BorderThickness=new(1),CornerRadius=new(6),Padding=new(2),ToolTip=node.Path};return insertion is null?new InlineUIContainer(border){BaselineAlignment=BaselineAlignment.Center}:new InlineUIContainer(border,insertion){BaselineAlignment=BaselineAlignment.Center};}
        foreach(var node in expanded){if(node.Type=="text")paragraph.Inlines.Add(new Run(node.Text){Tag=node.Original});else paragraph.Inlines.Add(Attachment(node));}
        Node[] Nodes()=>ReadDocument(input.Document);
        bool Save(){
            timer.Stop();if(building||saving||!dirty)return true;saving=true;try{var current=Require(board,id);if(Stamp(current.Settings)!=saved)throw new LayoutEditException("卡片已被其它操作修改，当前草稿保留，请重新核对。");var previous=NativeHandoffStorage.Read(current.Settings,root,id).Nodes;var nodes=Nodes();var next=NativeHandoffStorage.Write(current.Settings,nodes,root,id,uncommitted);
                controller.Commit(state=>{var target=DesktopCards.Require(state.Boards.Single(b=>b.Id==board),id);if(Stamp(target.Settings)!=saved)throw new LayoutEditException("交接卡片已变化。");target.Settings=next;},"已保存交接内容。");saved=Stamp(next);dirty=false;message.Text=$"{nodes.Count(n=>n.Type=="file"&&n.Kind=="image")} 张图 · {nodes.Count(n=>n.Type=="file"&&n.Kind!="image")} 个文件";var keep=OwnedFiles(NativeHandoffStorage.Read(next,root,id).Nodes,id).ToHashSet(StringComparer.OrdinalIgnoreCase);Retire(id,OwnedFiles(previous,id).Concat(uncommitted).Where(p=>!keep.Contains(p)));uncommitted.Clear();return true;
            }catch(Exception e)when(e is not OutOfMemoryException){message.Text=e.Message;return false;}finally{saving=false;}}
        saveEditor=()=>Save();timer.Tick+=(_,_)=>Save();input.TextChanged+=(_,_)=>{if(building)return;dirty=true;timer.Stop();timer.Start();};building=false;
        void InsertPaths(IEnumerable<string> paths){try{var list=paths.Take(513).ToArray();if(list.Length>512)throw new LayoutEditException("一次最多插入 512 个文件。");var entries=list.Select(FileNode).ToArray();foreach(var node in entries){var cursor=input.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);var attachment=Attachment(node,cursor);input.CaretPosition=attachment.ElementEnd;}dirty=true;Save();}catch(Exception e)when(e is not OutOfMemoryException){message.Text=e.Message;}}
        insert.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Title="插入交接文件",Multiselect=true,CheckFileExists=true};if(dialog.ShowDialog(window)==true)InsertPaths(dialog.FileNames);};
        DataObject.AddPastingHandler(input,(_,e)=>{try{if(e.DataObject.GetDataPresent(DataFormats.FileDrop)){e.CancelCommand();InsertPaths((string[])e.DataObject.GetData(DataFormats.FileDrop));return;}
            if(e.DataObject.GetDataPresent(DataFormats.Bitmap)){e.CancelCommand();if(e.DataObject.GetData(DataFormats.Bitmap) is not BitmapSource source)throw new LayoutEditException("无法读取此图片。");if((long)source.PixelWidth*source.PixelHeight>16*1024*1024)throw new LayoutEditException("图片像素过大，请先保存成文件再插入。");var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(source));using var stream=new MemoryStream();png.Save(stream);string path=NativeHandoffStorage.SaveImage(root,id,stream.ToArray());uncommitted.Add(path);InsertPaths([path]);return;}
            if(e.DataObject.GetDataPresent(DataFormats.UnicodeText)){e.CancelCommand();string text=e.DataObject.GetData(DataFormats.UnicodeText) as string??"";string currentText=new TextRange(input.Document.ContentStart,input.Document.ContentEnd).Text;
                if(Encoding.UTF8.GetByteCount(text)>NativeHandoffStorage.CopyTextLimit||Encoding.UTF8.GetByteCount(currentText)-Encoding.UTF8.GetByteCount(input.Selection.Text)+Encoding.UTF8.GetByteCount(text)>NativeHandoffStorage.CopyTextLimit)throw new LayoutEditException("编辑文字最多 8 MiB，请改用文件附件。");input.Selection.Text=text;}
        }catch(Exception error)when(error is not OutOfMemoryException){e.CancelCommand();message.Text=error.Message;}});
        input.PreviewDrop+=(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){var position=input.GetPositionFromPoint(e.GetPosition(input),true);if(position is not null)input.CaretPosition=position;e.Handled=true;InsertPaths((string[])e.Data.GetData(DataFormats.FileDrop));}};
        copy.Click+=async(_,_)=>{if(Save())try{message.Text=await Copy(board,id,false);}catch(Exception e)when(e is not OutOfMemoryException){message.Text=e.Message;}};done.Click+=(_,_)=>{if(Save())window.Close();};
        window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape||e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;if(Save())window.Close();}};
        window.Closing+=(_,e)=>{if(!disposed&&!Save())e.Cancel=true;};window.Closed+=(_,_)=>{timer.Stop();editor=null;editing="";saveEditor=null;editorImages.Clear();CollectRetired();changed();};message.Text=initial.Skipped>0?$"有 {initial.Skipped} 项无效数据，原设置尚未改写。":"文字与附件在同一条流里；可在任意位置粘贴或插入。";changed();window.Show();input.Focus();
        }finally{Volatile.Write(ref editorPreparing,0);}
    }
    public void Dispose(){if(disposed)return;saveEditor?.Invoke();foreach(string id in retiredOwners)PersistRetired(id);disposed=true;editor?.Close();if(listening&&clipboardWindow is not null)RemoveClipboardFormatListener(clipboardWindow.Handle);clipboardWindow?.Dispose();editorImages.Clear();lock(gate){desired=[];present.Clear();}retired.Clear();}
    [DllImport("user32.dll")]private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]private static extern bool RemoveClipboardFormatListener(IntPtr window);
}
