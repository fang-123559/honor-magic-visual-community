using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MagicCommunity;

// On-demand WPF editor, using the same DWM Acrylic path as community settings.
// No wallpaper capture, extra render host or refresh timer is involved.
internal sealed class NativeWeatherCityWindow : CommunityWindow
{
    internal TextBox Query { get; } = new() { FontSize=13, Padding=new(10,8,10,8), MaxLength=80 };
    internal Button Search { get; } = new() { Content="搜索", Padding=new(10,7,10,7), Margin=new(0,8,0,10) };
    internal TextBlock State { get; } = new() { Text="手动选城 · 不读取系统定位", TextWrapping=TextWrapping.Wrap };
    internal ListBox Cities { get; } = new() { BorderThickness=new(0), Background=Brushes.Transparent, Padding=new(4) };
    internal Button ChooseCity { get; } = new() { Content="使用所选城市", Padding=new(10,8,10,8), IsEnabled=false };
    private readonly DockPanel root=new();
    private readonly Border results=new() { CornerRadius=new(NativeWpfTheme.PanelRadius), Margin=new(0,14,0,0), BorderThickness=new(1) };
    private readonly TextBlock attribution=new() { Text="天气 Open-Meteo · 城市 GeoNames", FontSize=11, Margin=new(0,12,0,8) };
    private readonly bool? previewTheme;
    private HwndSource? source;

    internal NativeWeatherCityWindow(bool? theme=null)
    {
        previewTheme=theme;
        Title="设置天气城市";Width=470;Height=470;MinWidth=360;MinHeight=360;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Content=root;
        var panel=new DockPanel { Margin=new(20) };root.Children.Add(panel);
        var head=new StackPanel();DockPanel.SetDock(head,Dock.Top);panel.Children.Add(head);
        head.Children.Add(new TextBlock { Text="搜索城市", FontSize=22, FontWeight=FontWeights.SemiBold, Margin=new(0,0,0,14) });
        head.Children.Add(Query);head.Children.Add(Search);head.Children.Add(State);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        footer.Children.Add(attribution);footer.Children.Add(ChooseCity);
        results.Child=Cities;panel.Children.Add(results);
        Cities.SelectionChanged+=(_,_)=>ChooseCity.IsEnabled=Cities.SelectedItem is NativeWeather.City;
        ApplyAppearance();
        SourceInitialized+=(_,_)=>{
            var handle=new WindowInteropHelper(this).Handle;
            source=HwndSource.FromHwnd(handle);source?.AddHook(WindowMessage);powerSubscription=new(this,ApplyAppearance);
            ApplyAppearance();
        };
        Closed+=(_,_)=>{powerSubscription?.Dispose();source?.RemoveHook(WindowMessage);source=null;};
    }

    private NativePowerSubscription? powerSubscription;
    private IntPtr WindowMessage(IntPtr handle,int message,IntPtr w,IntPtr l,ref bool handled)
    {
        if(message==0x1a||message==0x218)ApplyAppearance(); // settings/theme and power events only
        return IntPtr.Zero;
    }

    private void ApplyAppearance()
    {
        var palette=new NativeWpfTheme(previewTheme);palette.Apply(this);
        bool glass=false;var handle=new WindowInteropHelper(this).Handle;
        if(handle!=IntPtr.Zero){
            bool enabled=!SystemParameters.HighContrast&&!SystemParameters.IsRemoteSession
                &&!CommunityPreferences.Power().Saver
                &&NativeWpfTheme.Personalization("EnableTransparency",0)!=0;
            int dark=palette.Dark?1:0,corner=2,backdrop=enabled?3:1;
            DwmSetWindowAttribute(handle,20,ref dark,4);
            DwmSetWindowAttribute(handle,33,ref corner,4); // DWMWCP_ROUND, ignored if unsupported
            int result=DwmSetWindowAttribute(handle,38,ref backdrop,4); // transient-window Acrylic
            var margins=new Margins { Left=enabled?-1:0,Right=enabled?-1:0,Top=enabled?-1:0,Bottom=enabled?-1:0 };
            int frame=DwmExtendFrameIntoClientArea(handle,ref margins);glass=enabled&&result==0&&frame==0;
            if(!glass&&enabled){backdrop=1;DwmSetWindowAttribute(handle,38,ref backdrop,4);}
            if(source?.CompositionTarget is {} target)target.BackgroundColor=Colors.Transparent;
        }
        Background=Brushes.Transparent;root.Background=palette.MaterialBackground(glass);
        State.Foreground=attribution.Foreground=palette.Muted;
        results.Background=palette.WindowCard;results.BorderBrush=palette.Divider;
        Resources[typeof(Button)]=RoundedControl(typeof(Button),palette);
        Resources[typeof(TextBox)]=RoundedControl(typeof(TextBox),palette);
        var item=new Style(typeof(ListBoxItem));item.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,8,10,8)));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        item.Setters.Add(new Setter(Control.FocusVisualStyleProperty,null));item.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
        item.Setters.Add(new Setter(Control.ForegroundProperty,palette.Ink));
        item.Setters.Add(new Setter(Control.TemplateProperty,BuildRoundedTemplate(typeof(ListBoxItem))));
        var selected=new Trigger { Property=ListBoxItem.IsSelectedProperty,Value=true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty,palette.Interaction(true)));selected.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
        selected.Setters.Add(new Setter(Control.ForegroundProperty,palette.Ink));item.Triggers.Add(selected);
        Cities.ItemContainerStyle=item;
    }

    private static Binding TemplateParentBinding(string name)=>new(name) { RelativeSource=new(RelativeSourceMode.TemplatedParent) };
    private static ControlTemplate BuildRoundedTemplate(Type type)
    {
        var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(NativeWpfTheme.ControlRadius));
        border.SetBinding(Border.BackgroundProperty,TemplateParentBinding("Background"));border.SetBinding(Border.BorderBrushProperty,TemplateParentBinding("BorderBrush"));
        border.SetBinding(Border.BorderThicknessProperty,TemplateParentBinding("BorderThickness"));
        if(type==typeof(TextBox)){
            var host=new FrameworkElementFactory(typeof(ScrollViewer),"PART_ContentHost");
            host.SetBinding(MarginProperty,TemplateParentBinding("Padding"));border.AppendChild(host);
        }else{
            var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetBinding(ContentPresenter.ContentProperty,TemplateParentBinding("Content"));
            content.SetBinding(ContentPresenter.ContentTemplateProperty,TemplateParentBinding("ContentTemplate"));content.SetBinding(MarginProperty,TemplateParentBinding("Padding"));
            content.SetBinding(HorizontalAlignmentProperty,TemplateParentBinding("HorizontalContentAlignment"));content.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);
            border.AppendChild(content);
        }
        return new ControlTemplate(type) { VisualTree=border };
    }
    private static Style RoundedControl(Type type,NativeWpfTheme palette)
    {
        var style=new Style(type);style.Setters.Add(new Setter(Control.FocusVisualStyleProperty,null));style.Setters.Add(new Setter(Control.ForegroundProperty,palette.Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty,palette.WindowCard));style.Setters.Add(new Setter(Control.BorderBrushProperty,palette.Divider));
        if(type==typeof(TextBox)){style.Setters.Add(new Setter(TextBox.SelectionBrushProperty,palette.Selection));style.Setters.Add(new Setter(TextBox.CaretBrushProperty,palette.Ink));}
        style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));style.Setters.Add(new Setter(Control.TemplateProperty,BuildRoundedTemplate(type)));
        // Focus remains logical; the shared interaction policy has no colored border.
        if(type==typeof(Button)){
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));
            var hover=new MultiTrigger();hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty,true));hover.Conditions.Add(new Condition(UIElement.IsEnabledProperty,true));hover.Setters.Add(new Setter(Control.BackgroundProperty,palette.Interaction()));style.Triggers.Add(hover);
            var pressed=new Trigger { Property=System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,Value=true };pressed.Setters.Add(new Setter(Control.BackgroundProperty,palette.Divider));style.Triggers.Add(pressed);
        }
        var disabled=new Trigger { Property=IsEnabledProperty,Value=false };disabled.Setters.Add(new Setter(OpacityProperty,.45));style.Triggers.Add(disabled);return style;
    }

    // Actual dialog content, no HWND/network/desktop takeover. Native caption/blur needs live acceptance.
    internal void Export(string path,bool populated,int dpi)
    {
        if(populated){Query.Text="威海";Cities.Items.Add(new NativeWeather.City("威海","山东省",37.51,122.12,"Asia/Shanghai"));Cities.Items.Add(new NativeWeather.City("威海市","山东省",37.5,122.1,"Asia/Shanghai"));Cities.SelectedIndex=0;State.Text="选择一个城市";}
        root.Measure(new Size(470,430));root.Arrange(new Rect(0,0,470,430));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(470*dpi/96,430*dpi/96,dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }

    [StructLayout(LayoutKind.Sequential)]private struct Margins { public int Left,Right,Top,Bottom; }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr handle,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")]private static extern int DwmExtendFrameIntoClientArea(IntPtr handle,ref Margins margins);
}
