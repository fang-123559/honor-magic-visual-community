using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MagicCommunity;

public sealed class FeedbackWindow : CommunityWindow
{
    private readonly AppController controller;
    private readonly List<FeedbackImage> images=[];
    private readonly TextBox description=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=20000,Height=130,Margin=new(0,8,0,8)};
    private readonly StackPanel imageList=new();
    private readonly TextBlock count=new(){Margin=new(8),VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock error=new(){Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,6)};
    public FeedbackWindow(AppController controller, Window owner)
    {
        this.controller=controller;Owner=owner;Title="记录问题 / 建议";Width=760;Height=760;MinWidth=620;MinHeight=540;WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=owner.FontFamily;
        Background=Brushes.White;Foreground=Brushes.Black;
        var layout=new DockPanel{Margin=new(20),Background=Brushes.White};
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);
        top.Children.Add(new TextBlock{Text="描述操作步骤、实际结果和期望结果",FontSize=20});
        top.Children.Add(new TextBlock{Text="可附上已标注截图，并为每张图填写说明。反馈与图片只保存到项目本地，修复按计划排期。",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,2),Foreground=Brushes.SlateGray});
        top.Children.Add(description);
        var toolbar=new WrapPanel();toolbar.Children.Add(MakeButton("添加图片",AddFiles));toolbar.Children.Add(MakeButton("粘贴截图",PasteImage));toolbar.Children.Add(count);top.Children.Add(toolbar);
        top.Children.Add(new TextBlock{Text="PNG / JPG / BMP · 最多 6 张 · 单张 ≤10 MB · 总计 ≤30 MB",FontSize=12,Foreground=Brushes.SlateGray});
        top.Children.Add(error);layout.Children.Add(top);
        var bottom=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);
        bottom.Children.Add(MakeButton("打开反馈目录",()=>controller.OpenFolder("feedback")));
        bottom.Children.Add(MakeButton("保存反馈",()=>
        {
            string path=controller.Feedback(description.Text,images);
            controller.SetStatus("反馈已保存："+Path.GetFileName(Path.GetDirectoryName(path)));Close();
        }));layout.Children.Add(bottom);
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=imageList,AllowDrop=true};
        scroll.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
        scroll.Drop+=(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)Try(()=>ReadFiles(paths));e.Handled=true;};
        layout.Children.Add(scroll);Content=layout;RefreshImages();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.V&&(Keyboard.Modifiers&ModifierKeys.Control)!=0)Try(()=>{if(Clipboard.ContainsImage()){e.Handled=true;PasteImage();}});};
    }
    private Button MakeButton(string label,Action action)
    {
        var button=new Button{Content=label,Padding=new(12,7,12,7),Margin=new(0,0,8,0)};button.Click+=(_,_)=>Try(action);return button;
    }
    private void Try(Action action)
    {try{action();error.Text="";}catch(Exception ex){error.Text=ex.Message;controller.Log("feedback_error",new{type=ex.GetType().Name,message=ex.Message});}}
    private void AddFiles()
    {
        var picker=new OpenFileDialog{Title="选择标注后的截图或图片",Filter="图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",Multiselect=true};
        if(picker.ShowDialog(this)==true)ReadFiles(picker.FileNames);
    }
    private void ReadFiles(string[] paths)
    {
        if(images.Count+paths.Length>FeedbackStore.MaxImages)throw new InvalidDataException("最多添加 6 张图片。请减少所选文件。");
        long total=images.Sum(x=>(long)x.Bytes.Length);var added=new List<FeedbackImage>();
        foreach(string path in paths)
        {
            long length=new FileInfo(path).Length;
            if(length>FeedbackStore.MaxTotalBytes-total)throw new InvalidDataException("图片总大小须在 30 MB 以内。");
            var image=FeedbackStore.ReadFile(path);total+=image.Bytes.Length;added.Add(image);
        }
        Add(added);
    }
    private void PasteImage()
    {
        if(!Clipboard.ContainsImage())throw new InvalidOperationException("剪贴板中没有图片。请先截图或复制图片。");
        var bitmap=Clipboard.GetImage()??throw new InvalidOperationException("无法读取剪贴板图片，请重试。");
        if((long)bitmap.PixelWidth*bitmap.PixelHeight>40_000_000)throw new InvalidDataException("截图像素过大，请先裁剪。");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);
        Add([FeedbackStore.FromBytes("截图-"+DateTime.Now.ToString("HHmmss")+".png",stream.ToArray())]);
    }
    private void Add(IReadOnlyList<FeedbackImage> added)
    {FeedbackStore.Validate(images.Concat(added).ToArray());images.AddRange(added);RefreshImages();}
    private void RefreshImages()
    {
        count.Text=$"{images.Count} / 6 张 · {images.Sum(x=>(long)x.Bytes.Length)/1048576.0:F1} MB";imageList.Children.Clear();
        if(images.Count==0)imageList.Children.Add(new TextBlock{Text="尚未添加截图。可点击添加图片、粘贴截图，或将图片文件拖到这里。",TextWrapping=TextWrapping.Wrap,Margin=new(8,24,8,24),Foreground=Brushes.SlateGray});
        foreach(var image in images)
        {
            var row=new DockPanel{Margin=new(0,8,0,8)};
            var preview=new Image{Source=image.Preview,Width=150,Height=100,Stretch=Stretch.Uniform,Margin=new(0,0,12,0)};DockPanel.SetDock(preview,Dock.Left);row.Children.Add(preview);
            var remove=MakeButton("移除",()=>{images.Remove(image);RefreshImages();});remove.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
            var details=new StackPanel();details.Children.Add(new TextBlock{Text=image.Name+$" · {image.Width} × {image.Height}",TextWrapping=TextWrapping.Wrap});
            details.Children.Add(new TextBlock{Text="图片说明 / 标注补充",Foreground=Brushes.SlateGray,Margin=new(0,4,0,4)});
            var caption=new TextBox{Text=image.Caption,MaxLength=1000,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=58,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};caption.TextChanged+=(_,_)=>image.Caption=caption.Text;details.Children.Add(caption);row.Children.Add(details);imageList.Children.Add(row);
        }
    }
    internal void PopulateSmoke(FeedbackImage image)
    {description.Text="示例：从资源管理器拖入副屏收纳盒后，检查项目是否加入正确位置。\n实际结果：请填写。期望结果：原文件保留，引用进入指定盒。";image.Caption="红色标记处为需要说明的位置（自动生成的界面检查样本）。";Add([image]);}
}
