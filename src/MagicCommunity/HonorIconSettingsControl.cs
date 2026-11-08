using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
namespace MagicCommunity;
// Local template: default Windows ComboBox chrome can use white on white in dark mode.
internal static class HonorIconSettingsControl
{
    private static Binding Parent(string name)=>new(name){RelativeSource=new(RelativeSourceMode.TemplatedParent)};
    internal static ComboBox Create(bool dark)
    {
        var palette=new NativeWpfTheme(dark);
        var combo=new ComboBox{Width=156,MinHeight=32,Foreground=palette.Ink,Background=palette.WindowCard,BorderBrush=palette.Divider};
        var template=new ControlTemplate(typeof(ComboBox));
        var grid=new FrameworkElementFactory(typeof(Grid));
        var toggle=new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(UIElement.FocusableProperty,false);toggle.SetValue(ButtonBase.ClickModeProperty,ClickMode.Press);
        toggle.SetBinding(ToggleButton.IsCheckedProperty,new Binding("IsDropDownOpen"){RelativeSource=new(RelativeSourceMode.TemplatedParent),Mode=BindingMode.TwoWay});
        var toggleTemplate=new ControlTemplate(typeof(ToggleButton));
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,palette.Interaction());border.SetValue(Border.BorderBrushProperty,palette.Divider);border.SetValue(Border.BorderThicknessProperty,new Thickness(1));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(NativeWpfTheme.ControlRadius));
        var arrow=new FrameworkElementFactory(typeof(TextBlock));arrow.SetValue(TextBlock.TextProperty,"⌄");arrow.SetValue(TextBlock.ForegroundProperty,palette.Ink);arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right);arrow.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);arrow.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,10,0));border.AppendChild(arrow);toggleTemplate.VisualTree=border;toggle.SetValue(Control.TemplateProperty,toggleTemplate);grid.AppendChild(toggle);
        var selected=new FrameworkElementFactory(typeof(ContentPresenter));selected.SetBinding(ContentPresenter.ContentProperty,Parent("SelectionBoxItem"));selected.SetBinding(ContentPresenter.ContentTemplateProperty,Parent("SelectionBoxItemTemplate"));selected.SetValue(FrameworkElement.MarginProperty,new Thickness(10,0,28,0));selected.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);selected.SetValue(UIElement.IsHitTestVisibleProperty,false);grid.AppendChild(selected);
        var popup=new FrameworkElementFactory(typeof(Popup),"PART_Popup");popup.SetValue(Popup.PlacementProperty,PlacementMode.Bottom);popup.SetValue(Popup.AllowsTransparencyProperty,true);popup.SetValue(Popup.FocusableProperty,false);popup.SetBinding(Popup.IsOpenProperty,Parent("IsDropDownOpen"));popup.SetBinding(Popup.PlacementTargetProperty,new Binding{RelativeSource=new(RelativeSourceMode.TemplatedParent)});
        var menu=new FrameworkElementFactory(typeof(Border));menu.SetValue(Border.BackgroundProperty,palette.WindowCard);menu.SetValue(Border.BorderBrushProperty,palette.Divider);menu.SetValue(Border.BorderThicknessProperty,new Thickness(1));menu.SetValue(Border.CornerRadiusProperty,new CornerRadius(NativeWpfTheme.ControlRadius));menu.SetValue(Border.PaddingProperty,new Thickness(4));menu.SetBinding(FrameworkElement.MinWidthProperty,Parent("ActualWidth"));
        var scroll=new FrameworkElementFactory(typeof(ScrollViewer));scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);scroll.SetValue(FrameworkElement.MaxHeightProperty,220d);scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));menu.AppendChild(scroll);popup.AppendChild(menu);grid.AppendChild(popup);
        template.VisualTree=grid;combo.Template=template;return combo;
    }
}
