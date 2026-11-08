using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MagicCommunity;
// Two real asset families are visible together; no additional icon backplates.
internal static class HonorIconSchemeCards
{
 internal static Grid Create(bool dark,string selected,bool enabled,Action<string> choose)
 {
  var palette=new NativeWpfTheme(dark);var grid=new Grid{Margin=new(16,4,16,16)};grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
  string group="honor-"+Guid.NewGuid().ToString("N");
  foreach(var (id,label,column,directory) in new[]{("honor-white","荣耀 · 白底",0,"DockIcon-白底"),("honor-translucent","荣耀 · 通透",1,"DockIcon-通透")}){
   bool active=selected==id;var content=new StackPanel();
   var images=new UniformGrid{Columns=4,Margin=new(0,8,0,10)};
   foreach(string name in new[]{"msedge","QQ","Steam","Weixin"}){
    string file=Path.Combine(AppContext.BaseDirectory,"honor-icons","library",directory,name+".png");var cell=new Grid{Height=48,Margin=new(2)};
    if(File.Exists(file)){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=64;image.UriSource=new Uri(file);image.EndInit();image.Freeze();cell.Children.Add(new Image{Source=image,MaxWidth=44,MaxHeight=44,Stretch=Stretch.Uniform,ToolTip=name});}
    else cell.Children.Add(new TextBlock{Text="素材暂缺",FontSize=9,Foreground=palette.Muted,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
    images.Children.Add(cell);
   }
   content.Children.Add(images);content.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=palette.Ink,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,0,0,6)});
   content.Children.Add(new TextBlock{Text=active?"✓ 当前使用":"选择此套图标",Foreground=active?SystemColors.HighlightBrush:palette.Muted,FontSize=10,HorizontalAlignment=HorizontalAlignment.Center});
   var radio=new RadioButton{Tag=id,GroupName=group,IsChecked=active,IsEnabled=enabled,Content=content,Margin=new(column==0?0:7,0,column==0?7:0,0),Padding=new(8,10,8,10),HorizontalContentAlignment=HorizontalAlignment.Stretch,BorderBrush=active?SystemColors.HighlightBrush:palette.Divider,BorderThickness=new(active?2:1),Background=palette.WindowCard};
   var template=new ControlTemplate(typeof(RadioButton));var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(12));
   foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})border.SetBinding(property,new System.Windows.Data.Binding(property.Name){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});
   var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetBinding(ContentPresenter.ContentProperty,new System.Windows.Data.Binding("Content"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});border.AppendChild(presenter);template.VisualTree=border;
   var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,SystemColors.HighlightBrush));template.Triggers.Add(focus);radio.Template=template;
   System.Windows.Automation.AutomationProperties.SetName(radio,label);radio.Checked+=(_,_)=>choose(id);Grid.SetColumn(radio,column);grid.Children.Add(radio);
  }
  return grid;
 }
}
