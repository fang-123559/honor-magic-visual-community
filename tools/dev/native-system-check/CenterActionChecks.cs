using MagicCommunity;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class CenterActionChecks
{
    internal static void Run(Dictionary<string,bool> checks, NativeSystemPresentationSession.Published state)
    {
        void Check(string name,bool ok){checks[name]=ok;if(!ok)throw new InvalidOperationException(name);}
        JsonElement Json(string text)=>JsonSerializer.Deserialize<JsonElement>(text);
        JsonObject Row(NativeSystemPanel panel,string id)=>panel.Data["rows"]!.AsArray().First(x=>x!["id"]!.GetValue<string>()==id)!.AsObject();
        foreach(bool on in new[]{false,true})
        {
            var providers=new Dictionary<string,JsonElement>(state.Providers){
                ["wifi"]=JsonSerializer.SerializeToElement(new{interfaces=new[]{new{id="wifi",radioOn=on}}}),
                ["bluetooth"]=JsonSerializer.SerializeToElement(new{available=true,radioAvailable=true,radioOn=on}),
                ["brightness"]=Json("""{"available":true,"displays":[{"id":"display","value":0.43}]}""")};
            var panel=NativeSystemPanel.Build("@magic/quick-settings",state with{Providers=providers});
            Check("quick-radio-state-from-provider-"+on,Row(panel,"quick-network")["active"]!.GetValue<bool>()==on&&Row(panel,"quick-bluetooth")["active"]!.GetValue<bool>()==on);
            Check("quick-radio-tiles-retain-navigation-"+on,panel.Commands["quick-network"].Local=="panel"&&panel.Commands["quick-bluetooth"].Local=="panel");
            Check("quick-brightness-offers-real-display-and-range-"+on,Row(panel,"brightness:display")["control"]!.GetValue<string>()=="range"&&Row(panel,"brightness:display")["value"]!.GetValue<double>()==.43&&panel.Commands["brightness:display"].Operation=="brightness-action"&&panel.Commands["brightness:display"].Arguments["displayId"]!.GetValue<string>()=="display");
            Check("quick-volume-offers-real-endpoint-and-range-"+on,Row(panel,"volume:device")["control"]!.GetValue<string>()=="range"&&panel.Commands["volume:device"].Arguments["deviceId"]!.GetValue<string>()=="device");
        }
        var absent=NativeSystemPanel.Build("@magic/quick-settings",state with{Providers=new Dictionary<string,JsonElement>()});
        Check("quick-no-phantom-brightness",!absent.Commands.Keys.Any(x=>x.StartsWith("brightness:"))&&!Row(absent,"brightness-unavailable")["enabled"]!.GetValue<bool>());
        Check("quick-unknown-radios-are-not-on",!Row(absent,"quick-network")["active"]!.GetValue<bool>()&&!Row(absent,"quick-bluetooth")["active"]!.GetValue<bool>()&&Row(absent,"quick-network")["label"]!.GetValue<string>().Contains("未知"));
        foreach(string notification in new[]{"""{"available":false,"canClearAll":true,"access":"allowed"}""","""{"available":true,"canClearAll":true,"access":"denied"}""","""{"available":true,"canClearAll":false,"access":"allowed"}"""})
            Check("clear-denied-"+notification,!NativeSystemPanel.Build("@magic/notifications",state with{Providers=new Dictionary<string,JsonElement>{{"notifications",Json(notification)}}}).Commands.ContainsKey("clear"));
        var displays=Json("""{"available":true,"displays":[{"id":"secondary","monitorId":"2","primary":false,"available":true,"value":0.7,"diagonalInches":27},{"id":"primary","monitorId":"1","primary":true,"available":true,"value":0.4},{"id":"unsupported","monitorId":"3","primary":false,"available":false,"value":0,"widthPx":2560,"heightPx":1440}]}""");
        var perScreen=NativeSystemPanel.Build("@magic/quick-settings",state with{Providers=new Dictionary<string,JsonElement>{{"brightness",displays}}});
        var rangeRows=perScreen.Data["rows"]!.AsArray().Where(r=>r!["id"]!.GetValue<string>().StartsWith("brightness:")).ToArray();
        Check("per-screen-primary-first",rangeRows.Length==3&&rangeRows[0]!["id"]!.GetValue<string>()=="brightness:primary"&&rangeRows[0]!["label"]!.GetValue<string>()=="亮度-主屏幕");
        Check("per-screen-secondary-size",Row(perScreen,"brightness:secondary")["label"]!.GetValue<string>().Contains("27 英寸"));
        Check("per-screen-unknown-size-resolution",Row(perScreen,"brightness:unsupported")["label"]!.GetValue<string>().Contains("2560×1440"));
        Check("unsupported-screen-retained-disabled",Row(perScreen,"brightness:unsupported")["control"]!.GetValue<string>()=="range"&&!Row(perScreen,"brightness:unsupported")["enabled"]!.GetValue<bool>()&&!perScreen.Commands.ContainsKey("brightness:unsupported"));
        Check("screen-actions-do-not-cross-targets",perScreen.Commands["brightness:primary"].Arguments["displayId"]!.GetValue<string>()=="primary"&&perScreen.Commands["brightness:secondary"].Arguments["displayId"]!.GetValue<string>()=="secondary");
        Check("control-center-notification-navigation",perScreen.Commands["focus-settings"].Local=="panel"&&perScreen.Commands["focus-settings"].Arguments["kind"]!.GetValue<string>()=="@magic/notifications");
        foreach(bool quiet in new[]{false,true}){
            var notices=NativeSystemPanel.Build("@magic/notifications",state,notificationQuiet:quiet);var row=Row(notices,"focus");
            Check("magic-quiet-label-and-blue-state-"+quiet,row["active"]!.GetValue<bool>()==quiet&&row["label"]!.GetValue<string>()==(quiet?"免打扰-开":"免打扰-magic视界"));
            Check("magic-quiet-offered-local-only-"+quiet,notices.Commands["focus"].Local=="notification-quiet"&&notices.Commands["focus"].Operation==""&&notices.Commands["focus"].Arguments["enabled"]!.GetValue<bool>()==!quiet);
        }
    }
}
