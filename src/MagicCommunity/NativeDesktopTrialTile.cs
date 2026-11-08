using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;

namespace MagicCommunity;

// One retained drawing element per visible tile. No Image/TextBlock subtree,
// per-icon effects, permanent rendering subscription or animation timer.
public sealed class NativeDesktopTrialTile : FrameworkElement
{
    private Point? pressed;
    private bool selectOnRelease;
    private string? textStamp;
    private FormattedText? label;
    internal NativeDesktopTrialWindow? Host=>Window.GetWindow(this) as NativeDesktopTrialWindow;
    internal NativeDesktopTrial.Tile? Model=>DataContext as NativeDesktopTrial.Tile;
    public NativeDesktopTrialTile()
    {
        Focusable=true;ClipToBounds=true;
        DataContextChanged+=(_,_)=>{textStamp=null;InvalidateVisual();};
        Loaded+=(_,_)=>InvalidateVisual();
        GotKeyboardFocus+=(_,_)=>InvalidateVisual();LostKeyboardFocus+=(_,_)=>InvalidateVisual();
    }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);if(Model is not {} model||Host is not {} host)return;
        var size=new Rect(0,0,ActualWidth,ActualHeight);draw.DrawRectangle(Brushes.Transparent,null,size);
        if(host.Selected(model.Key))draw.DrawRoundedRectangle(Brush("#D8E8FF"),new Pen(Brush("#3978C6"),1),new Rect(1,1,Math.Max(0,ActualWidth-2),Math.Max(0,ActualHeight-2)),10,10);
        double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
        if(model.Folder is {} folder){
            var cover=new Rect(4,4,Math.Max(1,ActualWidth-8),Math.Max(1,ActualHeight-32));draw.DrawRoundedRectangle(Brush("#DEE7F1"),null,cover,18,18);
            int preview=Math.Min(4,folder.Items.Count);
            for(int i=0;i<preview;i++){
                var icon=host.Trial.Icon(folder.Items[i],dpi);double side=Math.Min(44,(cover.Height-24)/2),x=cover.X+12+(i%2)*(cover.Width/2),y=cover.Y+10+(i/2)*(cover.Height/2);
                if(icon is not null)draw.DrawImage(icon,new Rect(x,y,side,side));else draw.DrawRoundedRectangle(Brush("#ABC0DB"),null,new Rect(x,y,side,side),7,7);
            }
            if(preview==0){var hint=new FormattedText("＋",CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface(CommunityFont.Family,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),32,Brush("#6B809B"),VisualTreeHelper.GetDpi(this).PixelsPerDip);draw.DrawText(hint,new Point((ActualWidth-hint.Width)/2,(cover.Height-hint.Height)/2));}
        }else if(model.Item is {} item){
            var image=host.Trial.Icon(item,dpi);var rect=new Rect((ActualWidth-48)/2,8,48,48);
            if(image is not null)draw.DrawImage(image,rect);else draw.DrawRoundedRectangle(Brush("#A9C4E6"),null,rect,8,8);
        }
        string text=model.Name+(model.Folder is {} box?" ("+box.Items.Count+")":"");
        string stamp=text+"|"+ActualWidth+"|"+dpi;
        if(stamp!=textStamp){
            label=new FormattedText(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface(CommunityFont.Family,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),12,Brush("#22364D"),VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(1,ActualWidth-8),MaxTextHeight=34,TextAlignment=TextAlignment.Center,Trimming=TextTrimming.CharacterEllipsis};textStamp=stamp;
        }
        if(label is not null)draw.DrawText(label,new Point(4,model.Folder is null?64:ActualHeight-25));
        if(IsKeyboardFocused)draw.DrawRoundedRectangle(null,new Pen(Brush("#1A65BA"),1){DashStyle=DashStyles.Dot},new Rect(3,3,Math.Max(0,ActualWidth-6),Math.Max(0,ActualHeight-6)),8,8);
    }
    private static SolidColorBrush Brush(string value)=>NativeDesktopTrialWindow.Palette(value);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);if(Model is not {} model||Host is not {} host)return;
        Focus();bool control=(Keyboard.Modifiers&ModifierKeys.Control)!=0;selectOnRelease=!control&&host.PreserveSelectionForDrag(model.Key);if(!selectOnRelease)host.SelectTile(model,control);e.Handled=true;
        if(e.ClickCount==2){pressed=null;host.InvokeTile(model);return;}
        pressed=e.GetPosition(this);CaptureMouse();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(pressed is not {} start||e.LeftButton!=MouseButtonState.Pressed||Model is not {} model||Host is not {} host)return;
        var point=e.GetPosition(this);
        if(Math.Abs(point.X-start.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        pressed=null;selectOnRelease=false;ReleaseMouseCapture();host.StartDrag(this,model);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){if(selectOnRelease&&Model is {} model)Host?.SelectTile(model,false);selectOnRelease=false;pressed=null;if(IsMouseCaptured)ReleaseMouseCapture();base.OnMouseLeftButtonUp(e);}
    protected override void OnLostMouseCapture(MouseEventArgs e){selectOnRelease=false;pressed=null;base.OnLostMouseCapture(e);}
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e){base.OnMouseRightButtonUp(e);if(Model is {} model&&Host is {} host){if(!host.Selected(model.Key))host.SelectTile(model,false);host.ShowTileMenu(this,model);e.Handled=true;}}
    protected override void OnKeyDown(KeyEventArgs e){if(Host is {} host&&Model is {} model)host.TileKey(this,model,e);if(!e.Handled)base.OnKeyDown(e);}
    protected override AutomationPeer OnCreateAutomationPeer()=>new TilePeer(this);
    private sealed class TilePeer(NativeDesktopTrialTile tile):FrameworkElementAutomationPeer(tile),IInvokeProvider
    {
        protected override string GetClassNameCore()=>nameof(NativeDesktopTrialTile);
        protected override string GetNameCore()=>tile.Model?.Name??"项目";
        protected override string GetAutomationIdCore()=>tile.Model?.Key??"";
        protected override string GetHelpTextCore()=>tile.Host?.Selected(tile.Model?.Key??"")==true?"已选择；Enter 打开，F2 重命名":"Enter 打开，F2 重命名";
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Button;
        public override object? GetPattern(PatternInterface patternInterface)=>patternInterface==PatternInterface.Invoke?this:base.GetPattern(patternInterface);
        public void Invoke()=>tile.Dispatcher.BeginInvoke(new Action(()=>{if(tile.Model is {} model)tile.Host?.InvokeTile(model);}));
    }
}
