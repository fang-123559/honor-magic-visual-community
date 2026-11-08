using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MagicCommunity;
public sealed class MainWindow : CommunityWindow
{
    private readonly AppController controller;
    private readonly string? fixedBoard;
    private readonly ComboBox boardPicker = new() { MinWidth = 180, Margin = new(0,0,12,0) };
    private readonly ComboBox boxPicker = new() { MinWidth = 140 };
    private readonly ListBox library = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new(0), Background = Brushes.Transparent };
    private readonly StackPanel boxes = new();
    private readonly TextBlock status = Text("", 12, "#59677E");
    private readonly TextBlock warning = Text("", 12, "#9B4500");
    private readonly TextBox search = new() { Margin = new(0,10,0,10), Padding = new(8,6,8,6), ToolTip = "筛选桌面项目" };
    private bool refreshing;
    private string? selectedBoard;
    private string? selectedBox;
    private Point dragStart;
    private const string InternalDrag = "MagicCommunity.Reference.v1";
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Text(string value, double size=14, string color="#253650") => new() { Text=value, FontSize=size, Foreground=Brush(color), TextWrapping=TextWrapping.Wrap };
    public MainWindow(AppController controller, string? board = null)
    {
        this.controller = controller; fixedBoard = board; selectedBoard = board;
        Title = board is null ? "Magic 视界社区版" : "Magic 视界 · 收纳窗";
        Width = board is null ? 1060 : 580; Height = 700; MinWidth = 400; MinHeight = 340;
        FontFamily = CommunityFont.Family; FontSize = 13; Background = Brush("#F4F6FB");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new(22), Background = Background };
        var header = new StackPanel();
        header.Children.Add(Text(board is null ? "Magic 视界社区版" : "我的收纳盒", 24));
        header.Children.Add(Text(AppController.Version+" · 桌面收纳设置与调试", 12, "#64748B"));
        var actions = new WrapPanel { Margin = new(0,14,0,10) };
        if (board is null)
        {
            actions.Children.Add(Button("刷新桌面", () => { controller.RefreshDisplays(); controller.RefreshDesktop(); }));
            actions.Children.Add(Button("添加收纳盒", AddBox));
            actions.Children.Add(Button("打开收纳窗", OpenBoard));
            actions.Children.Add(Button("记录问题 / 建议", ShowFeedback));
            actions.Children.Add(Button("项目目录", () => controller.OpenFolder(".")));
            actions.Children.Add(Button("调试日志", () => controller.OpenFolder("logs")));
            actions.Children.Add(Button("设置", ShowSettings));
        }
        else
        {
            actions.Children.Add(Button("添加收纳盒", AddBox));
            actions.Children.Add(Button("刷新", controller.RefreshDesktop));
            actions.Children.Add(Button("记录问题", ShowFeedback));
        }
        header.Children.Add(actions);
        header.Children.Add(warning);
        var selectors = new WrapPanel { Margin = new(0,12,0,12) };
        selectors.Children.Add(Text("屏幕  ")); selectors.Children.Add(boardPicker);
        if (board is null) { selectors.Children.Add(Text("加入  ")); selectors.Children.Add(boxPicker); }
        header.Children.Add(selectors); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new(0,14,0,0) };
        footer.Children.Add(status);
        footer.Children.Add(Text("此整理窗口添加引用；桌面大文件夹的拖入采用移动收纳。已移动项目请先移出并恢复，再移除引用。", 11, "#64748B"));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        if (board is null)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(310) }); grid.ColumnDefinitions.Add(new() { Width = new(18) }); grid.ColumnDefinitions.Add(new());
            var left = new DockPanel();
            var label = Text("桌面可用项目", 16); DockPanel.SetDock(label, Dock.Top); left.Children.Add(label);
            var searchLabel = Text("搜索名称 · Ctrl 可多选",11,"#64748B"); DockPanel.SetDock(searchLabel,Dock.Top);left.Children.Add(searchLabel);
            DockPanel.SetDock(search, Dock.Top); left.Children.Add(search);
            var add = Button("将选中项目加入收纳盒 →", AddSelected); DockPanel.SetDock(add, Dock.Bottom); left.Children.Add(add);
            left.Children.Add(library); grid.Children.Add(left);
            var scroll = new ScrollViewer { Content = boxes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 2); grid.Children.Add(scroll); root.Children.Add(grid);
        }
        else root.Children.Add(new ScrollViewer { Content = boxes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        boardPicker.SelectionChanged += (_, _) => { if (!refreshing) { selectedBoard = (boardPicker.SelectedItem as Choice)?.Id; selectedBox = null; Refresh(); } };
        boxPicker.SelectionChanged += (_, _) => { if (!refreshing) selectedBox = (boxPicker.SelectedItem as Choice)?.Id; };
        search.TextChanged += (_, _) => RefreshLibrary();
        library.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(library);
        library.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(library)-dragStart).Length < 6 || library.SelectedItems.Count == 0) return;
            var items = library.SelectedItems.Cast<ListBoxItem>().Select(x => (DesktopItem)x.Tag).ToArray();
            var data = new DataObject(); data.SetData(InternalDrag, new ReferenceDrag(null, items));
            DragDrop.DoDragDrop(library, data, DragDropEffects.Copy);
        };
        void RefreshVisible(){if(IsVisible)Refresh();}
        controller.Changed += RefreshVisible;
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
        Closed += (_, _) => controller.Changed -= RefreshVisible;
        Closing += (_,e)=>{if(fixedBoard is null&&controller.DesktopActive){e.Cancel=true;Hide();}};
        Loaded += (_, _) => Refresh();
        if(IsVisible)Refresh();
    }
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private sealed record ReferenceDrag(string? SourceBox, DesktopItem[] Items);
    private Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new(10,6,10,6), Margin = new(0,0,7,5), Background = Brushes.White, BorderBrush = Brush("#D5DDEC"), BorderThickness = new(1), Cursor = Cursors.Hand };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { controller.Error(title, ex); } };
        return button;
    }
    private Board? CurrentBoard => controller.State.Boards.FirstOrDefault(x => x.Id == selectedBoard);
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Refresh); return; }
        refreshing = true;
        try
        {
            var screenIds = controller.Screens.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
            var choices = controller.State.Boards.Select(x => new Choice(x.Id, x.Name + (screenIds.Contains(x.Id) ? "" : " · 已断开"))).ToList();
            selectedBoard ??= controller.State.Boards.FirstOrDefault()?.Id;
            boardPicker.ItemsSource = choices;
            boardPicker.SelectedItem = choices.FirstOrDefault(x=>x.Id==selectedBoard);
            boardPicker.IsEnabled = fixedBoard is null;
            var current = CurrentBoard;
            var boxChoices = current?.Boxes.Select(x=>new Choice(x.Id,x.Name)).ToList() ?? [];
            if (!boxChoices.Any(x=>x.Id==selectedBox)) selectedBox = boxChoices.FirstOrDefault()?.Id;
            boxPicker.ItemsSource = boxChoices; boxPicker.SelectedItem = boxChoices.FirstOrDefault(x=>x.Id==selectedBox);
            warning.Text = controller.Store.ReadOnly ? controller.Store.Problem : controller.DesktopActive ? "桌面大文件夹已连接；Windows 原有桌面和图标保留。此窗口用于整理与调试。" : "设置与调试窗口；从固定入口进入桌面大文件夹。";
            status.Text = controller.Status;
            boxes.Children.Clear();
            if (current is not null)
                foreach (var box in current.Boxes) boxes.Children.Add(BoxView(box));
            if (current?.Boxes.Count == 0) boxes.Children.Add(Text("添加一个收纳盒，然后从左侧或资源管理器拖入项目。",14,"#64748B"));
            RefreshLibrary();
        }
        finally { refreshing = false; }
    }
    private void RefreshLibrary()
    {
        if (fixedBoard is not null) return;
        var selected = library.SelectedItems.Cast<ListBoxItem>().Select(x=>((DesktopItem)x.Tag).Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        library.Items.Clear();
        foreach (var item in controller.Desktop.Concat(controller.State.Boards.SelectMany(board=>board.RootItems)).DistinctBy(item=>item.Target,StringComparer.OrdinalIgnoreCase).Where(x=>x.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
        {
            var row = new ListBoxItem { Content = ItemContent(item), Tag = item, Padding = new(7), Margin = new(0,0,0,3), ToolTip = item.Target };
            row.IsSelected = selected.Contains(item.Target);
            row.MouseDoubleClick += (_, _) => controller.Open(item.Target);
            library.Items.Add(row);
        }
    }
    private FrameworkElement ItemContent(DesktopItem item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = ShellDesktop.Icon(item);
        if (icon is not null) panel.Children.Add(new Image { Source = icon, Width = 22, Height = 22, Margin = new(0,0,8,0), VerticalAlignment = VerticalAlignment.Center });
        else panel.Children.Add(Text(item.IsFolder ? "▣  " : "▪  ", 17));
        panel.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = fixedBoard is null ? 220 : 280, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
    private FrameworkElement BoxView(Box box)
    {
        var border = new Border { Background = Brushes.White, BorderBrush = Brush("#DFE5F0"), BorderThickness = new(1), CornerRadius = new(10), Padding = new(12), Margin = new(0,0,0,12), AllowDrop = !controller.Store.ReadOnly };
        var stack = new StackPanel(); var title = new DockPanel();
        var ops = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(ops, Dock.Right);
        ops.Children.Add(Button("改名", () => RenameBox(box)));
        ops.Children.Add(Button("移除盒", () => RemoveBox(box)));
        title.Children.Add(ops);
        var fold = Button((box.Collapsed ? "▸ " : "▾ ") + box.Name + $"  ({box.Items.Count})", () => controller.Change(s => FindBox(s,box.Id).Collapsed = !box.Collapsed, "收纳盒状态已保存。"));
        fold.FontWeight = FontWeights.SemiBold; fold.HorizontalContentAlignment=HorizontalAlignment.Left; fold.BorderThickness = new(0); title.Children.Add(fold); stack.Children.Add(title);
        if (!box.Collapsed)
        {
            foreach (var item in box.Items)
            {
                var row = new Border { Child = ItemContent(item), Padding = new(7), Margin = new(0,2,0,0), Background = Brush("#F7F9FD"), CornerRadius = new(5), ToolTip = item.Target, Cursor = Cursors.Hand };
                Point start = default;
                row.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(row); if (e.ClickCount == 2) controller.Open(item.Target); };
                row.MouseMove += (_, e) =>
                {
                    if (controller.Store.ReadOnly || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(row)-start).Length < 6) return;
                    var data = new DataObject(); data.SetData(InternalDrag,new ReferenceDrag(box.Id,[item]));
                    // FileDrop advertises COPY only; Explorer cannot move/delete the actual source.
                    if (!item.IsVirtual && (File.Exists(item.Target) || Directory.Exists(item.Target))) data.SetData(DataFormats.FileDrop,new[] { item.Target });
                    DragDrop.DoDragDrop(row,data,DragDropEffects.Copy);
                };
                var menu = new ContextMenu();
                var open = new MenuItem { Header = "打开" }; open.Click += (_,_)=>controller.Open(item.Target); menu.Items.Add(open);
                var reveal = new MenuItem { Header = "打开所在目录" }; reveal.Click += (_,_)=>controller.Reveal(item); menu.Items.Add(reveal);
                var remove = new MenuItem { Header = "移除引用（保留原文件）", IsEnabled = !controller.Store.ReadOnly };
                remove.Click += (_,_) => controller.Change(s=>FindBox(s,box.Id).Items.RemoveAll(x=>StringComparer.OrdinalIgnoreCase.Equals(x.Target,item.Target)),"已移除引用，原文件未改动。");
                menu.Items.Add(remove); row.ContextMenu = menu; stack.Children.Add(row);
            }
            if (box.Items.Count == 0) stack.Children.Add(Text("把项目拖到这里",12,"#8290A8"));
        }
        border.DragOver += (_,e) =>
        {
            e.Effects = e.Data.GetDataPresent(InternalDrag) ? DragDropEffects.Copy : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        border.Drop += (_,e) =>
        {
            if (controller.Store.ReadOnly) return;
            try
            {
                if (e.Data.GetData(InternalDrag) is ReferenceDrag drag)
                    controller.Change(s => { foreach (var item in drag.Items) { if (drag.SourceBox is null) LayoutActions.Add(FindBox(s,box.Id),[item]); else LayoutActions.Move(s,drag.SourceBox,item.Target,box.Id); } },"分组已保存。");
                else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                {
                    var items = paths.Select(ShellDesktop.FromPath).ToArray();
                    controller.Change(s=>LayoutActions.Add(FindBox(s,box.Id),items),"已加入文件引用，原文件未改动。");
                }
                e.Effects = DragDropEffects.Copy; e.Handled = true;
            }
            catch (Exception ex) { controller.Error("拖入失败",ex); }
        };
        border.Child = stack; return border;
    }
    private static Box FindBox(LayoutState state,string id) => state.Boards.SelectMany(x=>x.Boxes).Single(x=>x.Id==id);
    private void AddSelected()
    {
        if (selectedBox is null) { controller.SetStatus("先添加或选择一个收纳盒。"); return; }
        var items = library.SelectedItems.Cast<ListBoxItem>().Select(x=>(DesktopItem)x.Tag).ToArray();
        if (items.Length == 0) { controller.SetStatus("先在桌面列表选中项目，可按 Ctrl 多选。"); return; }
        var box = selectedBox; controller.Change(s=>LayoutActions.Add(FindBox(s,box),items),$"已加入 {items.Length} 个选中项目；重复引用自动合并。");
    }
    private void AddBox()
    {
        var board = selectedBoard; if (board is null) return;
        var name = Prompt("添加收纳盒","给收纳盒起个名字","新收纳盒"); if (name is null) return;
        controller.Change(s=>s.Boards.Single(x=>x.Id==board).Boxes.Add(new(){Name=name}),"新收纳盒已保存。");
    }
    private void RenameBox(Box box)
    {
        var name=Prompt("修改名称","收纳盒名称",box.Name); if (name is null) return;
        controller.Change(s=>FindBox(s,box.Id).Name=name,"名称已保存。");
    }
    private void RemoveBox(Box box)
    {
        if (MessageBox.Show(this,$"移除“{box.Name}”及其中 {box.Items.Count} 个引用？原文件会保留。","移除收纳盒",MessageBoxButton.OKCancel,MessageBoxImage.Question) != MessageBoxResult.OK) return;
        controller.Change(s=>s.Boards.Single(x=>x.Boxes.Any(b=>b.Id==box.Id)).Boxes.RemoveAll(x=>x.Id==box.Id),"收纳盒已移除，原文件保留。");
    }
    private string? Prompt(string title,string label,string initial)
    {
        var dialog=new CommunityWindow{Title=title,Owner=this,Width=360,Height=190,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=FontFamily};
        var panel=new StackPanel{Margin=new(18)}; panel.Children.Add(Text(label));
        var input=new TextBox{Text=initial,MaxLength=80,Margin=new(0,10,0,12),Padding=new(5)}; panel.Children.Add(input);
        panel.Children.Add(Button("保存",()=>{if(!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult=true;})); dialog.Content=panel;
        dialog.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};
        return dialog.ShowDialog()==true?input.Text.Trim():null;
    }
    private void OpenBoard()
    {
        if(selectedBoard is null) return;
        var display=controller.Screens.FirstOrDefault(x=>x.Id==selectedBoard);
        if(display is null){controller.SetStatus("该屏幕已断开；引用仍保留，可在主窗口继续整理。");return;}
        var existing=Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(x=>x.fixedBoard==selectedBoard);
        if(existing is not null){existing.Show();existing.Activate();return;}
        var window=new MainWindow(controller,selectedBoard);
        window.SourceInitialized+=(_,_)=>{try{Displays.Place(window,display);}catch(Exception ex){controller.Error("收纳窗定位失败",ex);}};
        window.Show();
    }
    public void ShowFeedback()
    {
        var existing=Application.Current.Windows.OfType<FeedbackWindow>().FirstOrDefault();
        if(existing is not null){existing.Activate();return;}
        new FeedbackWindow(controller,this).ShowDialog();
    }
    private void ShowSettings()
    {
        var dialog=new CommunityWindow{Title="设置与调试",Owner=this,Width=480,Height=350,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=FontFamily};
        var panel=new StackPanel{Margin=new(20)};
        panel.Children.Add(Text("渲染方式",18));panel.Children.Add(Text("本机默认使用软件渲染。切换后重新打开应用生效；可在真实拖拽体验中比较。",12,"#64748B"));
        var software=new CheckBox{Content="软件渲染",IsChecked=controller.State.SoftwareRendering,Margin=new(0,15,0,15),IsEnabled=!controller.Store.ReadOnly};panel.Children.Add(software);
        panel.Children.Add(Button("保存设置",()=>{controller.Change(s=>s.SoftwareRendering=software.IsChecked==true,"渲染设置已保存，重启应用后生效。");dialog.Close();}));
        panel.Children.Add(Button("打开数据与备份",()=>controller.OpenFolder("data")));
        panel.Children.Add(Button("打开反馈目录",()=>controller.OpenFolder("feedback")));
        if(controller.Store.BackupAvailable)panel.Children.Add(Button("恢复可用布局备份",()=>{controller.RestoreBackup();dialog.Close();}));
        dialog.Content=panel;dialog.ShowDialog();
    }
}
