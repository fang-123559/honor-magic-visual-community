using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MagicCommunity;
internal sealed class NativeWeatherInfoWindow : CommunityWindow
{
    private readonly Border root=new(){Padding=new(22),CornerRadius=new(NativeWpfTheme.FloatingRadius),BorderThickness=new(1)};
    private readonly TextBlock text=new(){FontSize=13,TextWrapping=TextWrapping.Wrap};
    private NativePowerSubscription? powerSubscription;private readonly bool? preview;private HwndSource? source;
    internal NativeWeatherInfoWindow(string information,bool? theme=null){
        preview=theme;Title="定位信息";Width=420;Height=330;MinWidth=330;MinHeight=260;WindowStartupLocation=WindowStartupLocation.CenterScreen;Content=root;
        var panel=new DockPanel();root.Child=panel;var title=new TextBlock{Text="定位信息",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,16)};DockPanel.SetDock(title,Dock.Top);panel.Children.Add(title);
        var close=new Button{Content="确定",MinWidth=88,Padding=new(20,8,20,8),HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0),IsDefault=true,IsCancel=true};close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Bottom);panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer{Content=text,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});text.Text=information;
        SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);source?.AddHook(Message);powerSubscription=new(this,Apply);Apply();};Closed+=(_,_)=>{powerSubscription?.Dispose();source?.RemoveHook(Message);source=null;};Apply();
    }
    internal void UpdateInformation(string value){text.Text=value;Activate();}
    private IntPtr Message(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled){if(m is 0x1a or 0x218 or 0x31a)Apply();return IntPtr.Zero;}
    private void Apply(){var palette=new NativeWpfTheme(preview);palette.Apply(this);var handle=new WindowInteropHelper(this).Handle;bool glass=false;
        if(handle!=IntPtr.Zero){bool enabled=!SystemParameters.HighContrast&&!SystemParameters.IsRemoteSession&&!CommunityPreferences.Power().Saver&&NativeWpfTheme.Personalization("EnableTransparency",0)!=0;int dark=palette.Dark?1:0,corner=2,backdrop=enabled?3:1;DwmSetWindowAttribute(handle,20,ref dark,4);DwmSetWindowAttribute(handle,33,ref corner,4);int result=DwmSetWindowAttribute(handle,38,ref backdrop,4);var margins=new Margins{Left=enabled?-1:0,Right=enabled?-1:0,Top=enabled?-1:0,Bottom=enabled?-1:0};glass=enabled&&result==0&&DwmExtendFrameIntoClientArea(handle,ref margins)==0;if(source?.CompositionTarget is {} target)target.BackgroundColor=Colors.Transparent;}
        Background=Brushes.Transparent;root.Background=palette.MaterialBackground(glass);root.BorderBrush=palette.Divider;text.Foreground=palette.Ink;
    }
    internal void Export(string path){root.Measure(new Size(420,290));root.Arrange(new Rect(0,0,420,290));root.UpdateLayout();var image=new RenderTargetBitmap(420,290,96,96,PixelFormats.Pbgra32);image.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);}
    [StructLayout(LayoutKind.Sequential)]private struct Margins{public int Left,Right,Top,Bottom;}
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr h,int attribute,ref int value,int bytes);
    [DllImport("dwmapi.dll")]private static extern int DwmExtendFrameIntoClientArea(IntPtr h,ref Margins margins);
}
