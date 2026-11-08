using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace MagicCommunity;

// Short-lived, ordinary-permission mode of Magic itself. UIA can block inside a
// foreign provider; the parent bounds and terminates only this owned probe.
// No injected code, EncoDeck assemblies, memory reads or private window messages.
internal static class OriginalEncoDeckAccessibility
{
    private sealed record Button(AutomationElement Element,string Mode,string Label,bool Selected,bool Enabled);
    internal sealed record Battery(int level,bool charging,bool stale);
    private sealed record Panel(AutomationElement Root,string DeviceName,List<Button> Modes,List<Button> Levels,bool Connected,IReadOnlyDictionary<string,Battery> Batteries);
    internal static bool HasVisibleSurface(EncoDeckIntegration.Location location){bool found=false;EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out uint pid);if(pid==location.ProcessId&&IsWindowVisible(window)&&!IsIconic(window)){found=true;return false;}return true;},IntPtr.Zero);return found;}
    internal static int Run(string[] args)
    {
        try
        {
            if(args.Length!=7||args[0]!="--encodeck-accessibility")throw new ArgumentException();
            var location=new EncoDeckIntegration.Location("running-no-bridge",args[3],int.Parse(args[1],CultureInfo.InvariantCulture),long.Parse(args[2],CultureInfo.InvariantCulture));
            using var verified=EncoDeckIntegration.Recheck(location);
            string action=args[4],expectedDevice=args[5],label=args[6];
            if(action is not ("snapshot" or "mode" or "level")||expectedDevice.Length>80||label.Length>24)throw new ArgumentException();
            var watch=Stopwatch.StartNew();Panel? panel=null;
            EnumWindows((window,_)=>{
                GetWindowThreadProcessId(window,out uint pid);if(pid!=location.ProcessId||!IsWindowVisible(window)||IsIconic(window))return true;
                if(watch.ElapsedMilliseconds>2200)return false;
                try{var found=Read(AutomationElement.FromHandle(window),location.ProcessId,watch);if(found is not null&&(panel is null||found.Connected))panel=found;}catch(ElementNotAvailableException){}catch(InvalidOperationException){}
                return panel is null||!panel.Connected;
            },IntPtr.Zero);
            if(panel is null){Console.WriteLine("{\"available\":false,\"reason\":\"panel-unavailable\"}");return 0;}
            bool applied=action=="snapshot";
            if(action!="snapshot")
            {
                // Bind the user's choice to the same device and an actual
                // currently offered control, not a stale numeric position.
                if(!panel.Connected||panel.DeviceName!=expectedDevice)throw new InvalidOperationException("device-changed");
                var choices=action=="mode"?panel.Modes:panel.Levels;
                var matches=choices.Where(b=>b.Label==label&&b.Enabled).ToArray();
                if(matches.Length!=1||panel.Root.Current.IsOffscreen)throw new InvalidOperationException("control-unavailable");
                using var again=EncoDeckIntegration.Recheck(location);
                var button=matches[0];
                if(button.Selected)applied=true;
                else if(button.Element.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke)){((InvokePattern)invoke).Invoke();applied=true;}
                else if(button.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var selection)){((SelectionItemPattern)selection).Select();applied=true;}
                else if(button.Element.TryGetCurrentPattern(TogglePattern.Pattern,out var toggle)){((TogglePattern)toggle).Toggle();applied=true;}
            }
            using var finalIdentity=EncoDeckIntegration.Recheck(location);
            Console.WriteLine(JsonSerializer.Serialize(new{available=true,applied,deviceName=panel.DeviceName,
                modes=panel.Modes.Select(b=>new{mode=b.Mode,label=b.Label,selected=b.Selected,enabled=b.Enabled}),
                levels=panel.Levels.Select((b,index)=>new{index,label=b.Label,selected=b.Selected,enabled=b.Enabled}),
                left=panel.Batteries.GetValueOrDefault("left"),right=panel.Batteries.GetValueOrDefault("right"),@case=panel.Batteries.GetValueOrDefault("case"),
                deviceConnected=panel.Connected,noiseMode=panel.Modes.FirstOrDefault(b=>b.Selected)?.Mode??"",offscreen=panel.Root.Current.IsOffscreen}));return 0;
        }
        catch(Exception e)when(e is not OutOfMemoryException){Console.WriteLine("{\"available\":false,\"reason\":\"provider-or-identity-unavailable\"}");return 1;}
    }
    private static Panel? Read(AutomationElement root,int pid,Stopwatch watch)
    {
        var modes=new List<Button>();var levels=new List<Button>();var texts=new List<(string Id,string Text)>();var batteries=new Dictionary<string,Battery?>();bool header=false;int count=0;
        void Visit(AutomationElement element,int depth)
        {
            if(depth>24||++count>512||watch.ElapsedMilliseconds>2200)return;
            var current=element.Current;if(current.ProcessId!=pid||current.IsOffscreen)return;
            string name=Safe(current.Name,80),id=current.AutomationId;
            var battery=BatteryLabel(name);if(battery is { } reading){if(!batteries.TryGetValue(reading.Key,out var prior))batteries[reading.Key]=reading.Value;else if(prior!=reading.Value)batteries[reading.Key]=null;}
            if(current.ControlType==ControlType.Text&&name.Length>0)texts.Add((id,name));
            if(id=="DeviceSwitcher"&&name.Length>0)texts.Add(("DeviceNameText",name));
            if(current.ControlType==ControlType.Button)
            {
                if(name is "Open EncoDeck" or "打开 EncoDeck")header=true;
                bool selected=false;
                if(element.TryGetCurrentPattern(TogglePattern.Pattern,out var toggle))selected=((TogglePattern)toggle).Current.ToggleState==ToggleState.On;
                else if(element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var selection))selected=((SelectionItemPattern)selection).Current.IsSelected;
                // Localized caption Close has the same label as ANC Off. Only
                // named mode controls or actual selectable mode buttons qualify.
                string mode=ControlMode(id,name,toggle is not null||element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out _));
                if(mode.Length>0)modes.Add(new(element,mode,mode switch{"NoiseCancellation"=>"降噪","Transparency"=>"通透","Adaptive"=>"自适应",_=>"关闭"},selected,current.IsEnabled));
                else if(toggle is not null&&name.Length is >0 and <=24)levels.Add(new(element,"",name,selected,current.IsEnabled));
            }
            var walker=TreeWalker.RawViewWalker;var child=walker.GetFirstChild(element);
            while(child is not null&&count<512&&watch.ElapsedMilliseconds<=2200){Visit(child,depth+1);child=walker.GetNextSibling(child);}
        }
        Visit(root,0);
        // Both original surfaces offer the same controls, but only Home has
        // DeviceNameText. Require a recognized header and a real connection
        // label; cached battery values never establish a control connection.
        var identity=Header(header,texts);
        if(!identity.Recognized||root.Current.IsOffscreen||modes.Count is <2 or >4||modes.Select(b=>b.Mode).Distinct().Count()!=modes.Count||levels.Count>4)return null;
        return new(root,identity.Device,modes,levels,identity.Connected,batteries.Where(x=>x.Value is not null).ToDictionary(x=>x.Key,x=>x.Value!));
    }
    // BatteryIndicator exposes a labeled accessibility name in the original
    // UI. Reject unknown formats instead of assigning unlabeled % text by order.
    internal static KeyValuePair<string,Battery>? BatteryLabel(string text){
        if(text.Length>80)return null;
        var match=Regex.Match(text,@"^(左耳|右耳|充电盒|Left|Right|Case)\s+([0-9]{1,3})%(?:\s+(正在充电|charging))?(?:\s+(上次读取 .+|Last read .+))?$",RegexOptions.CultureInvariant);
        if(!match.Success||!int.TryParse(match.Groups[2].Value,out int level)||level>100)return null;
        string key=match.Groups[1].Value switch{"左耳" or "Left"=>"left","右耳" or "Right"=>"right",_=>"case"};bool stale=match.Groups[4].Success;
        return new(key,new(level,match.Groups[3].Success&&!stale,stale));
    }
    internal static (bool Recognized,string Device,bool Connected) Header(bool quick,IReadOnlyList<(string Id,string Text)> texts)
    {
        string device=texts.FirstOrDefault(t=>t.Id=="DeviceNameText").Text??"";
        bool main=device.Length>0;
        if(!main&&quick)device=texts.FirstOrDefault(t=>t.Text!="EncoDeck"&&t.Text.Length>0&&!t.Text.Contains('%')&&!ConnectionLabel(t.Text)&&t.Text.Any(char.IsLetter)).Text??"";
        bool linked=texts.Any(t=>t.Text is "已连接" or "Connected");
        bool offline=texts.Any(t=>t.Text is "未连接" or "已断开" or "已断开连接" or "正在连接" or "Disconnected" or "Not connected" or "Connecting" or "Connecting…");
        return ((main||quick)&&device.Length>0,device,linked&&!offline);
    }
    private static bool ConnectionLabel(string text)=>text is "已连接" or "Connected" or "未连接" or "已断开" or "已断开连接" or "正在连接" or "Disconnected" or "Not connected" or "Connecting" or "Connecting…";
    internal static string Mode(string id,string label)=>id switch{
        "AncButton"=>"NoiseCancellation","AdaptiveButton"=>"Adaptive","TransparencyButton"=>"Transparency","OffButton"=>"Off",
        _=>label switch{"降噪" or "Noise cancelling" or "Noise cancellation"=>"NoiseCancellation","通透" or "Transparency"=>"Transparency","自适应" or "Adaptive"=>"Adaptive","关闭" or "Off"=>"Off",_=>""}};
    internal static string ControlMode(string id,string label,bool selectable)=>
        id is "AncButton" or "AdaptiveButton" or "TransparencyButton" or "OffButton"?Mode(id,label):selectable?Mode("",label):"";
    private static string Safe(string value,int maximum)=>new(value.Where(c=>!char.IsControl(c)).Take(maximum).ToArray());
    private delegate bool EnumProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
}
