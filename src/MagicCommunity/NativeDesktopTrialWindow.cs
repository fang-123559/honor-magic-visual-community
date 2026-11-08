using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MagicCommunity;

public sealed class NativeDesktopTrialWindow : CommunityWindow
{
    private static readonly Dictionary<string,SolidColorBrush> brushes=new(StringComparer.Ordinal);
    internal static SolidColorBrush Palette(string color){if(!brushes.TryGetValue(color,out var brush)){brush=new((Color)ColorConverter.ConvertFromString(color));brush.Freeze();brushes[color]=brush;}return brush;}
    internal NativeDesktopTrial Trial { get; }
    public string BoardId { get; private set; }
    private readonly Grid workspace=new();
    private readonly Canvas desktop=new(){Background=Brushes.Transparent};
    private readonly ScrollViewer desktopScroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Border backdrop=new(){Background=Palette("#55172B45"),Visibility=Visibility.Collapsed};
    private readonly Border panel=new(){Background=Palette("#F5F7FB"),CornerRadius=new(20),Padding=new(16),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    private readonly ListBox folderList=new(){BorderThickness=new(0),Background=Brushes.Transparent,HorizontalContentAlignment=HorizontalAlignment.Stretch};
    private readonly TextBlock folderTitle=new(){FontSize=21,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock status=new(){Margin=new(12,8,12,8),TextWrapping=TextWrapping.Wrap};
    private readonly ComboBox boardChoice=new(){MinWidth=160,MaxWidth=260,Margin=new(4)};
    private readonly HashSet<string> selected=new(StringComparer.Ordinal);
    private readonly Dictionary<string,NativeDesktopTrialTile> desktopTiles=new(StringComparer.Ordinal);
    private readonly ScaleTransform scale=new(1,1);
    private readonly TranslateTransform translation=new();
    private string? folderId;
    private NativeDesktopTrial.Tile[] folderItems=[];
    private List<NativeDesktopTrial.Row> rows=[];
    private int columns=5,animationGeneration;
    private bool refreshing,dragging,closed;
    private HwndSource? source;
    private EventHandler? firstRender;
    private string? requestedFocus;
    public NativeDesktopTrialWindow(NativeDesktopTrial trial,string board)
    {
        FontFamily=CommunityFont.Family;
        Trial=trial;BoardId=board;Title="Magic 原生桌面试点 · 隔离数据";Width=1100;Height=800;MinWidth=360;MinHeight=420;Background=Palette("#E9EFF7");Foreground=Palette("#22364D");
        var root=new DockPanel();Content=root;
        var bar=new WrapPanel{Margin=new(8)};DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        bar.Children.Add(new TextBlock{Text="原生绘制试点",FontSize=18,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new(8)});
        boardChoice.ItemsSource=trial.State.Boards.Select(value=>new{value.Id,value.Name}).ToArray();boardChoice.DisplayMemberPath="Name";boardChoice.SelectedValuePath="Id";boardChoice.SelectedValue=BoardId;bar.Children.Add(boardChoice);
        bar.Children.Add(Button("打开另一显示区",OpenOther));bar.Children.Add(Button("导出资源快照",()=>Run(()=>{var path=Trial.Snapshot();Note("资源快照已保存："+path);})));
        bar.Children.Add(new TextBlock{Text="双击打开 · Ctrl 多选 · F2 重命名 · 拖放移动",VerticalAlignment=VerticalAlignment.Center,Margin=new(8),TextWrapping=TextWrapping.Wrap});
        DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);root.Children.Add(workspace);
        desktopScroll.Content=desktop;workspace.Children.Add(desktopScroll);workspace.Children.Add(backdrop);workspace.Children.Add(panel);
        KeyboardNavigation.SetTabNavigation(panel,KeyboardNavigationMode.Cycle);KeyboardNavigation.SetDirectionalNavigation(panel,KeyboardNavigationMode.Contained);
        var content=new DockPanel();panel.Child=content;
        var heading=new DockPanel{Margin=new(0,0,0,12)};DockPanel.SetDock(heading,Dock.Top);content.Children.Add(heading);
        var close=Button("关闭",()=>CloseFolder());DockPanel.SetDock(close,Dock.Right);heading.Children.Add(close);heading.Children.Add(folderTitle);
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,10,0,0)};DockPanel.SetDock(footer,Dock.Bottom);content.Children.Add(footer);
        footer.Children.Add(Button("移出选中",MoveOut));content.Children.Add(folderList);
        var transforms=new TransformGroup();transforms.Children.Add(scale);transforms.Children.Add(translation);panel.RenderTransform=transforms;panel.RenderTransformOrigin=new(.5,.5);
        VirtualizingPanel.SetIsVirtualizing(folderList,true);VirtualizingPanel.SetVirtualizationMode(folderList,VirtualizationMode.Recycling);VirtualizingPanel.SetScrollUnit(folderList,ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(folderList,true);ScrollViewer.SetHorizontalScrollBarVisibility(folderList,ScrollBarVisibility.Disabled);
        var stack=new FrameworkElementFactory(typeof(VirtualizingStackPanel));folderList.ItemsPanel=new ItemsPanelTemplate(stack);
        var tileFactory=new FrameworkElementFactory(typeof(NativeDesktopTrialTile));tileFactory.SetBinding(WidthProperty,new Binding(nameof(NativeDesktopTrial.Tile.Width)));tileFactory.SetValue(HeightProperty,112.0);tileFactory.SetValue(MarginProperty,new Thickness(4,0,4,0));
        var template=new DataTemplate{VisualTree=tileFactory};
        var items=new FrameworkElementFactory(typeof(ItemsControl));items.SetBinding(ItemsControl.ItemsSourceProperty,new Binding(nameof(NativeDesktopTrial.Row.Items)));items.SetValue(ItemsControl.ItemTemplateProperty,template);
        var horizontal=new FrameworkElementFactory(typeof(StackPanel));horizontal.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);items.SetValue(ItemsControl.ItemsPanelProperty,new ItemsPanelTemplate(horizontal));
        folderList.ItemTemplate=new DataTemplate{VisualTree=items};
        var rowStyle=new Style(typeof(ListBoxItem));rowStyle.Setters.Add(new Setter(PaddingProperty,new Thickness(0)));rowStyle.Setters.Add(new Setter(MarginProperty,new Thickness(0)));rowStyle.Setters.Add(new Setter(FocusableProperty,false));rowStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));folderList.ItemContainerStyle=rowStyle;
        boardChoice.SelectionChanged+=(_,_)=>{if(!refreshing&&boardChoice.SelectedValue is string id&&id!=BoardId){CloseFolder(true);BoardId=id;selected.Clear();Refresh();PlaceOnBoard();}};
        backdrop.MouseLeftButtonDown+=(_,eventArgs)=>{eventArgs.Handled=true;CloseFolder();};
        desktop.MouseLeftButtonDown+=(_,eventArgs)=>{if(eventArgs.OriginalSource==desktop){selected.Clear();RefreshSelection();}};
        desktopScroll.ScrollChanged+=(_,_)=>RefreshDesktop();workspace.SizeChanged+=(_,_)=>{RefreshDesktop();if(folderId is not null)RefreshFolder();};
        PreviewKeyDown+=(_,eventArgs)=>{if(eventArgs.Key==Key.Escape&&folderId is not null&&!eventArgs.Handled){eventArgs.Handled=true;CloseFolder();}};
        AllowDrop=true;DragOver+=OnDragOver;Drop+=OnDrop;
        Trial.Changed+=Refresh;Trial.IconsChanged+=RefreshSelection;
        SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);source?.AddHook(WindowMessage);PlaceOnBoard();};
        Loaded+=(_,_)=>{Refresh();Note("隔离试点：没有接管 Explorer，所有文件位于 "+Trial.Root);};
        Closed+=(_,_)=>{closed=true;animationGeneration++;StopAnimations();source?.RemoveHook(WindowMessage);Trial.Changed-=Refresh;Trial.IconsChanged-=RefreshSelection;};
    }
    private Button Button(string text,Action action){var button=new Button{Content=text,Padding=new(10,5,10,5),Margin=new(4),MinHeight=30};button.Click+=(_,_)=>Run(action);return button;}
    private void Note(string text)=>status.Text=text;
    private void Run(Action action){try{action();}catch(Exception error){Note(error.Message);Trial.Record("operation-error",new{type=error.GetType().Name,message=error.Message});}}
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){if(message==0x02E0)Dispatcher.BeginInvoke(new Action(()=>{if(!closed){StopAnimations();RefreshDesktop();RefreshSelection();Note("DPI 已更新："+Displays.GetDpiForWindow(hwnd));}}));return IntPtr.Zero;}
    private void PlaceOnBoard(){var screen=Trial.Screens.FirstOrDefault(value=>value.Id==BoardId);if(screen is null||!IsLoaded&&source is null)return;Displays.Place(this,screen);double dpi=Displays.GetDpiForWindow(new WindowInteropHelper(this).Handle)/96.0;if(dpi<=0)dpi=1;Width=Math.Max(MinWidth,Math.Min(1100,(screen.Width-48)/dpi));Height=Math.Max(MinHeight,Math.Min(800,(screen.Height-48)/dpi));}
    private void OpenOther(){var next=Trial.State.Boards.FirstOrDefault(value=>value.Id!=BoardId);if(next is null)return;var existing=Application.Current.Windows.OfType<NativeDesktopTrialWindow>().FirstOrDefault(window=>window.BoardId==next.Id);if(existing is not null){existing.Activate();return;}new NativeDesktopTrialWindow(Trial,next.Id).Show();}
    private void Refresh()
    {
        if(closed)return;refreshing=true;
        try{
            var board=Trial.Board(BoardId);var valid=(folderId is null?NativeDesktopTrial.DesktopTiles(board):NativeDesktopTrial.FolderTiles(board.Boxes.FirstOrDefault(box=>box.Id==folderId)??new())).Select(tile=>tile.Key).ToHashSet(StringComparer.Ordinal);selected.IntersectWith(valid);
            RefreshDesktop();if(folderId is not null){if(board.Boxes.All(box=>box.Id!=folderId))CloseFolder(true);else RefreshFolder();}
        }finally{refreshing=false;}
    }
    private void RefreshDesktop()
    {
        if(closed)return;var board=Trial.Board(BoardId);if(board.Grid is not {} grid)return;
        desktop.Width=grid.Columns*96+28;desktop.Height=grid.Rows*96+16;
        var visible=new Rect(desktopScroll.HorizontalOffset,desktopScroll.VerticalOffset,Math.Max(1,desktopScroll.ViewportWidth),Math.Max(1,desktopScroll.ViewportHeight));visible.Inflate(96,96);
        var live=new HashSet<string>(StringComparer.Ordinal);
        foreach(var tile in NativeDesktopTrial.DesktopTiles(board)){
            if(!grid.Placements.TryGetValue(tile.Key,out var cell))continue;
            var rect=new Rect(14+cell.X*96,8+cell.Y*96,cell.W*96-8,cell.H*96-8);if(!visible.IntersectsWith(rect))continue;live.Add(tile.Key);
            if(!desktopTiles.TryGetValue(tile.Key,out var control)){control=new();desktopTiles[tile.Key]=control;desktop.Children.Add(control);}
            control.DataContext=tile;control.Width=rect.Width;control.Height=rect.Height;Canvas.SetLeft(control,rect.Left);Canvas.SetTop(control,rect.Top);control.InvalidateVisual();
        }
        foreach(string key in desktopTiles.Keys.Where(key=>!live.Contains(key)).ToArray()){desktop.Children.Remove(desktopTiles[key]);desktopTiles.Remove(key);}
    }
    private void RefreshFolder()
    {
        if(folderId is null)return;var box=Trial.Board(BoardId).Boxes.SingleOrDefault(value=>value.Id==folderId);if(box is null)return;
        panel.Width=Math.Max(280,Math.Min(560,workspace.ActualWidth-32));double available=Math.Max(100,panel.Width-32-SystemParameters.VerticalScrollBarWidth-6);columns=Math.Max(1,(int)(available/100));double tileWidth=Math.Floor(available/columns)-8;folderItems=NativeDesktopTrial.FolderTiles(box).Select(tile=>tile with{Width=tileWidth}).ToArray();
        var next=folderItems.Chunk(columns).Select(items=>new NativeDesktopTrial.Row(items)).ToList();
        // Keep the ItemsSource on icon/selection changes. Only state/column
        // changes rebuild row data; virtualization owns all item controls.
        rows=next;folderList.ItemsSource=rows;folderList.Height=Math.Max(112,Math.Min(Math.Ceiling(folderItems.Length/(double)columns)*112,workspace.ActualHeight-160));
        folderTitle.Text=box.Name+" · "+box.Items.Count+" 项";
        if(requestedFocus is {} wanted){requestedFocus=null;Dispatcher.BeginInvoke(new Action(()=>FocusKey(wanted)));}
    }
    internal bool Selected(string key)=>selected.Contains(key);
    internal bool PreserveSelectionForDrag(string key)=>selected.Count>1&&selected.Contains(key);
    internal void SelectTile(NativeDesktopTrial.Tile tile,bool additive)
    {
        if(!additive||tile.Folder is not null||selected.Any(key=>key.StartsWith("folder:",StringComparison.Ordinal)))selected.Clear();
        if(!selected.Add(tile.Key)&&additive)selected.Remove(tile.Key);RefreshSelection();
    }
    private void RefreshSelection(){if(closed)return;foreach(var tile in Descendants<NativeDesktopTrialTile>(workspace))tile.InvalidateVisual();}
    internal void InvokeTile(NativeDesktopTrial.Tile tile)=>Run(()=>{if(Trial.Busy)return;if(tile.Folder is {} box)OpenFolder(box.Id);else if(tile.Item is {} item)Trial.Open(item);});
    private void OpenFolder(string id)
    {
        long input=Stopwatch.GetTimestamp();animationGeneration++;StopAnimations();selected.Clear();folderId=id;RefreshFolder();panel.Visibility=Visibility.Visible;backdrop.Visibility=Visibility.Visible;panel.UpdateLayout();
        var start=AnchorTransform();Animate(start.Sx,start.Sy,start.X,start.Y,.35,1,1,0,0,1,180,false);
        firstRender=(_,_)=>{CancelRenderObservation();if(!closed)Trial.Record("open-render-callback",new{inputToCallbackMs=Stopwatch.GetElapsedTime(input).TotalMilliseconds,items=folderItems.Length,materializedTiles=Descendants<NativeDesktopTrialTile>(folderList).Count(),scope="Pre-presentation callback, not GPU completion or input-to-present latency."});};CompositionTarget.Rendering+=firstRender;
        requestedFocus=folderItems.FirstOrDefault()?.Key;if(requestedFocus is {} focus){requestedFocus=null;Dispatcher.BeginInvoke(new Action(()=>FocusKey(focus)));}
    }
    private void CloseFolder(bool immediate=false)
    {
        if(folderId is null)return;string anchor="folder:"+folderId;
        if(immediate){animationGeneration++;StopAnimations();FinishClose(anchor);return;}
        double sx=scale.ScaleX,sy=scale.ScaleY,dx=translation.X,dy=translation.Y,opacity=panel.Opacity;
        animationGeneration++;StopAnimations();var end=AnchorTransform();Animate(sx,sy,dx,dy,opacity,end.Sx,end.Sy,end.X,end.Y,.35,140,true);
    }
    private void FinishClose(string anchor){panel.Visibility=Visibility.Collapsed;backdrop.Visibility=Visibility.Collapsed;panel.CacheMode=null;folderId=null;selected.Clear();if(desktopTiles.TryGetValue(anchor,out var tile))tile.Focus();RefreshSelection();}
    private (double Sx,double Sy,double X,double Y) AnchorTransform()
    {
        if(folderId is not null&&desktopTiles.TryGetValue("folder:"+folderId,out var anchor)&&panel.ActualWidth>0&&panel.ActualHeight>0){
            var start=anchor.TransformToAncestor(workspace).TransformBounds(new Rect(4,4,Math.Max(1,anchor.ActualWidth-8),Math.Max(1,anchor.ActualHeight-32)));
            var offset=VisualTreeHelper.GetOffset(panel);return(start.Width/panel.ActualWidth,start.Height/panel.ActualHeight,start.Left+start.Width/2-offset.X-panel.ActualWidth/2,start.Top+start.Height/2-offset.Y-panel.ActualHeight/2);
        }
        return(.96,.96,0,0);
    }
    private void CancelRenderObservation(){if(firstRender is not null){CompositionTarget.Rendering-=firstRender;firstRender=null;}}
    private void StopAnimations(){CancelRenderObservation();scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);translation.BeginAnimation(TranslateTransform.XProperty,null);translation.BeginAnimation(TranslateTransform.YProperty,null);panel.BeginAnimation(OpacityProperty,null);panel.CacheMode=null;scale.ScaleX=scale.ScaleY=1;translation.X=translation.Y=0;panel.Opacity=1;}
    private void Animate(double sx,double sy,double dx,double dy,double opacity,double ex,double ey,double tx,double ty,double endOpacity,int milliseconds,bool close)
    {
        int generation=animationGeneration;string anchor="folder:"+folderId;
        if(!SystemParameters.ClientAreaAnimation){if(close)FinishClose(anchor);return;}
        panel.CacheMode=new BitmapCache();
        var duration=TimeSpan.FromMilliseconds(milliseconds);var ease=new CubicEase{EasingMode=close?EasingMode.EaseIn:EasingMode.EaseOut};
        DoubleAnimation Clock(double from,double to)=>new(from,to,duration){EasingFunction=ease,FillBehavior=FillBehavior.HoldEnd};
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,Clock(sx,ex));scale.BeginAnimation(ScaleTransform.ScaleYProperty,Clock(sy,ey));translation.BeginAnimation(TranslateTransform.XProperty,Clock(dx,tx));translation.BeginAnimation(TranslateTransform.YProperty,Clock(dy,ty));
        var fade=Clock(opacity,endOpacity);fade.Completed+=(_,_)=>{if(generation!=animationGeneration||closed)return;StopAnimations();if(close)FinishClose(anchor);};panel.BeginAnimation(OpacityProperty,fade);
    }
    internal void TileKey(NativeDesktopTrialTile control,NativeDesktopTrial.Tile tile,KeyEventArgs eventArgs)
    {
        if(eventArgs.Key==Key.ImeProcessed)return;
        if(eventArgs.Key==Key.Enter){eventArgs.Handled=true;InvokeTile(tile);return;}
        if(eventArgs.Key==Key.F2){eventArgs.Handled=true;Rename(tile);return;}
        if(eventArgs.Key==Key.Apps||eventArgs.Key==Key.F10&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0){eventArgs.Handled=true;ShowTileMenu(control,tile);return;}
        var tiles=folderId is null?NativeDesktopTrial.DesktopTiles(Trial.Board(BoardId)):folderItems;
        if(eventArgs.Key==Key.A&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){selected.Clear();foreach(var item in tiles.Where(item=>item.Item is not null))selected.Add(item.Key);RefreshSelection();eventArgs.Handled=true;return;}
        int index=Array.FindIndex(tiles,item=>item.Key==tile.Key);
        if(eventArgs.Key==Key.Tab&&folderId is not null&&(Keyboard.Modifiers&ModifierKeys.Control)==0){int next=index+((Keyboard.Modifiers&ModifierKeys.Shift)!=0?-1:1);if(next>=0&&next<tiles.Length){eventArgs.Handled=true;SelectTile(tiles[next],false);FocusKey(tiles[next].Key);}return;}
        if(folderId is null&&eventArgs.Key is Key.Left or Key.Right or Key.Up or Key.Down){
            var grid=Trial.Board(BoardId).Grid!;var origin=grid.Placements[tile.Key];double ox=origin.X+origin.W/2.0,oy=origin.Y+origin.H/2.0;
            var candidates=tiles.Where(value=>value.Key!=tile.Key&&grid.Placements.ContainsKey(value.Key)).Select(value=>{var cell=grid.Placements[value.Key];return new{Tile=value,X=cell.X+cell.W/2.0-ox,Y=cell.Y+cell.H/2.0-oy};});
            var chosen=candidates.Where(value=>eventArgs.Key switch{Key.Left=>value.X<0,Key.Right=>value.X>0,Key.Up=>value.Y<0,_=>value.Y>0}).OrderBy(value=>eventArgs.Key is Key.Left or Key.Right?Math.Abs(value.Y)*1000+Math.Abs(value.X):Math.Abs(value.X)*1000+Math.Abs(value.Y)).FirstOrDefault();
            if(chosen is not null){SelectTile(chosen.Tile,false);FocusKey(chosen.Tile.Key);}eventArgs.Handled=true;return;
        }
        int step=eventArgs.Key switch{Key.Left=>-1,Key.Right=>1,Key.Up=>-columns,Key.Down=>columns,Key.PageUp=>-columns*4,Key.PageDown=>columns*4,_=>0};
        int target=eventArgs.Key==Key.Home?0:eventArgs.Key==Key.End?tiles.Length-1:step==0?-1:Math.Clamp(index+step,0,tiles.Length-1);
        if(target>=0&&tiles.Length>0){eventArgs.Handled=true;SelectTile(tiles[target],false);FocusKey(tiles[target].Key);}
    }
    private void FocusKey(string key)
    {
        if(closed)return;
        if(folderId is null){if(desktopTiles.TryGetValue(key,out var tile)){tile.BringIntoView();tile.Focus();}return;}
        int index=Array.FindIndex(folderItems,item=>item.Key==key);if(index<0)return;
        folderList.ScrollIntoView(rows[index/columns]);folderList.UpdateLayout();Descendants<NativeDesktopTrialTile>(folderList).FirstOrDefault(tile=>tile.Model?.Key==key)?.Focus();
    }
    internal void ShowTileMenu(NativeDesktopTrialTile control,NativeDesktopTrial.Tile tile)
    {
        var menu=new ContextMenu();void Add(string text,Action action){var item=new MenuItem{Header=text};item.Click+=(_,_)=>Run(action);menu.Items.Add(item);}
        Add("打开",()=>InvokeTile(tile));Add("重命名",()=>Rename(tile));
        if(tile.Folder is {} folder)foreach(var (w,h) in new[]{(2,2),(4,2),(4,4)})Add($"{w}列 × {h}排",()=>Trial.Resize(BoardId,folder.Id,w,h));
        else if(folderId is not null)Add("移出到桌面",MoveOut);
        foreach(var board in Trial.State.Boards.Where(board=>board.Id!=BoardId))Add("移到 "+board.Name,()=>MoveTo(board.Id,tile));
        menu.PlacementTarget=control;menu.IsOpen=true;
    }
    private void Rename(NativeDesktopTrial.Tile tile)
    {
        var dialog=new CommunityWindow{Owner=this,FontFamily=CommunityFont.Family,Title="重命名隔离项目",Width=400,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        var content=new StackPanel{Margin=new(18)};dialog.Content=content;content.Children.Add(new TextBlock{Text=tile.Folder is null?"仅修改试点目录中的文件。":"修改虚拟文件夹名称。",Margin=new(0,0,0,10)});
        var input=new TextBox{Text=tile.Name,MinHeight=30};content.Children.Add(input);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};content.Children.Add(buttons);
        bool composing=false;TextCompositionManager.AddPreviewTextInputStartHandler(input,(_,_)=>composing=true);TextCompositionManager.AddPreviewTextInputUpdateHandler(input,(_,_)=>composing=true);TextCompositionManager.AddPreviewTextInputHandler(input,(_,_)=>composing=false);
        var problem=new TextBlock{Foreground=Palette("#A83030"),TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};content.Children.Insert(2,problem);
        void Save(){if(composing)return;try{string key=Trial.Rename(BoardId,folderId,tile,input.Text);dialog.DialogResult=true;Dispatcher.BeginInvoke(new Action(()=>{selected.Clear();selected.Add(key);RefreshSelection();FocusKey(key);}));}catch(Exception error){problem.Text=error.Message;}}
        buttons.Children.Add(Button("保存",Save));buttons.Children.Add(Button("取消",()=>dialog.DialogResult=false));
        input.KeyDown+=(_,key)=>{if(key.Key==Key.ImeProcessed||composing)return;if(key.Key==Key.Enter){key.Handled=true;Save();}else if(key.Key==Key.Escape){key.Handled=true;dialog.DialogResult=false;}};
        dialog.ContentRendered+=(_,_)=>{input.Focus();int dot=tile.Folder is null?input.Text.LastIndexOf('.'):-1;input.Select(0,dot>0?dot:input.Text.Length);};dialog.ShowDialog();
    }
    private NativeDesktopTrial.DragPayload Payload(NativeDesktopTrial.Tile tile)
    {
        if(!selected.Contains(tile.Key))SelectTile(tile,false);
        string[] keys=tile.Folder is not null?[tile.Key]:selected.Where(key=>key.StartsWith("item:",StringComparison.Ordinal)).ToArray();
        return new(Trial.Session,Trial.State.Revision,BoardId,folderId,keys,tile.Key);
    }
    internal void StartDrag(NativeDesktopTrialTile control,NativeDesktopTrial.Tile tile)=>Run(()=>{
        if(Trial.Busy||dragging)return;dragging=true;var payload=Payload(tile);string[] intents=[];
        try{
            var data=new DataObject();data.SetData(NativeDesktopTrial.DragFormat,JsonSerializer.Serialize(payload));
            if(tile.Item is not null){var files=Trial.Resolve(payload).Select(item=>Trial.GuardTarget(item.Target)).ToArray();data.SetData(DataFormats.FileDrop,files);intents=Trial.PrepareOutbound(payload);}
            DragDrop.DoDragDrop(control,data,DragDropEffects.Copy|DragDropEffects.Move);
        }finally{dragging=false;if(intents.Length>0)Trial.FinishOutbound(intents);}
    });
    private NativeDesktopTrial.DragPayload? ReadDrag(IDataObject data)
    {
        if(Trial.Busy||!data.GetDataPresent(NativeDesktopTrial.DragFormat))return null;
        try{var raw=data.GetData(NativeDesktopTrial.DragFormat) as string;if(raw is null||raw.Length>65536)return null;var payload=JsonSerializer.Deserialize<NativeDesktopTrial.DragPayload>(raw);if(payload is null)return null;Trial.ValidateDrag(payload);return payload;}catch(Exception error)when(error is JsonException or InvalidOperationException or LayoutEditException or ArgumentException){return null;}
    }
    private (string? Folder,int X,int Y)? DropTarget(DragEventArgs args)
    {
        if(folderId is not null){var point=args.GetPosition(panel);return point.X>=0&&point.Y>=0&&point.X<panel.ActualWidth&&point.Y<panel.ActualHeight?(folderId,0,0):null;}
        var hit=(args.OriginalSource as DependencyObject);while(hit is not null&&hit is not NativeDesktopTrialTile)hit=VisualTreeHelper.GetParent(hit);
        var p=args.GetPosition(desktop);return ((hit as NativeDesktopTrialTile)?.Model?.Folder?.Id,(int)Math.Floor((p.X-14)/96),(int)Math.Floor((p.Y-8)/96));
    }
    private void OnDragOver(object sender,DragEventArgs args){args.Handled=true;var dragPayload=ReadDrag(args.Data);args.Effects=dragPayload is not null&&DropTarget(args) is not null?DragDropEffects.Move:DragDropEffects.None;}
    private void OnDrop(object sender,DragEventArgs args){args.Handled=true;args.Effects=DragDropEffects.None;var payload=ReadDrag(args.Data);if(payload is null||DropTarget(args) is not {} target)return;Run(()=>{Trial.Move(payload,BoardId,target.Folder,target.X,target.Y);args.Effects=DragDropEffects.Move;});}
    private void MoveOut()=>Run(()=>{if(folderId is null||selected.Count==0)return;var tile=folderItems.FirstOrDefault(item=>selected.Contains(item.Key));if(tile is not null)MoveTo(BoardId,tile);});
    private void MoveTo(string boardId,NativeDesktopTrial.Tile tile)
    {
        var destination=Trial.Board(boardId);int w=tile.Folder?.Columns??1,h=tile.Folder?.Rows??1;var slot=DesktopGridActions.ReserveSpace(destination,w,h);var payload=Payload(tile);Trial.Move(payload,boardId,null,slot.X,slot.Y);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;foreach(var next in Descendants<T>(child))yield return next;}
    }
}
