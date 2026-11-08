using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MagicCommunity;

// On-demand native settings. Reading is explicit; restoring requires a fresh
// target/revision-bound confirmation. This window never initializes a guard.
public sealed class SecondaryWallpaperWindow:CommunityWindow
{
    private readonly AppController controller;
    private readonly StackPanel body=new(){Margin=new(24)};
    private readonly NativeWpfTheme theme=new();
    private SecondaryWallpaperGuard? subscribed;
    private bool busy,closed;
    public SecondaryWallpaperWindow(AppController controller)
    {
        this.controller=controller;Title="副屏壁纸保护";Width=650;Height=520;MinWidth=480;MinHeight=320;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;theme.Apply(this);Background=theme.MaterialBackground(false);
        Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        Closed+=(_,_)=>{closed=true;if(subscribed is not null)subscribed.Changed-=Changed;};
        Activated+=(_,_)=>Render();Render();
    }
    private void Changed(){if(!closed)Render();}
    private void Text(string value,double size=13,Brush? color=null)=>body.Children.Add(new TextBlock{Text=value,FontSize=size,Foreground=color??theme.Ink,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
    private void Render()
    {
        if(closed)return;
        var guard=controller.WallpaperGuard;
        if(subscribed!=guard){if(subscribed is not null)subscribed.Changed-=Changed;subscribed=guard;if(guard is not null)guard.Changed+=Changed;}
        body.Children.Clear();Text("副屏壁纸保护",22);Text("记录静态壁纸的原图片路径与校验值。Windows 换图时跟随更新；开机与重连出现差异时，由你选择是否恢复。",13,theme.Muted);
        if(guard is null){Text("未连接副屏或桌面未启动。当前不读取壁纸、不建立记录，也没有后台轮询。");return;}
        Text(guard.Current.Status,13,theme.Muted);
        var refresh=new Button{Content="刷新壁纸状态",HorizontalAlignment=HorizontalAlignment.Left,Padding=new(14,7,14,7),Margin=new(0,0,0,18),IsEnabled=!busy};
        refresh.Click+=async(_,_)=>{busy=true;Render();try{await guard.RefreshAsync();}finally{busy=false;if(!closed)Render();}};body.Children.Add(refresh);
        foreach(var target in guard.Current.Targets){
            var box=new StackPanel{Margin=new(14)};
            box.Children.Add(new TextBlock{Text=target.Label,FontSize=15,Foreground=theme.Ink});
            box.Children.Add(new TextBlock{Text="当前："+(string.IsNullOrEmpty(target.CurrentImage)?"无法读取":target.CurrentImage),TextWrapping=TextWrapping.Wrap,Foreground=theme.Muted,Margin=new(0,8,0,4)});
            box.Children.Add(new TextBlock{Text="已保存："+(string.IsNullOrEmpty(target.SavedImage)?"尚无记录":target.SavedImage),TextWrapping=TextWrapping.Wrap,Foreground=theme.Muted});
            var restore=new Button{Content="恢复此副屏",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,12,0,0),Padding=new(14,7,14,7),IsEnabled=target.CanRestore&&!busy};
            restore.Click+=async(_,_)=>{
                if(MessageBox.Show(this,$"仅恢复以下副屏：{target.Label}\n\n图片：{target.SavedImage}\n\n主屏与壁纸显示方式保持不变。是否恢复？","恢复副屏壁纸",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                busy=true;Render();try{string result=await guard.RestoreAsync(target);if(!closed)MessageBox.Show(this,result,"副屏壁纸保护");}finally{busy=false;if(!closed)Render();}
            };box.Children.Add(restore);body.Children.Add(new Border{Background=theme.WindowCard,CornerRadius=new(10),Child=box,Margin=new(0,0,0,12)});
        }
        Text("仅支持可确认身份的在线副屏及静态图片。纯色、幻灯片、跨屏、Windows 聚焦及不可读取的图片不执行恢复。记录不复制图片；原文件被改写或删除后不会用旧记录强行恢复。",12,theme.Muted);
    }
}
