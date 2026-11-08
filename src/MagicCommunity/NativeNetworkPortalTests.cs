using System.Net;
using System.Text.Json;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal static class NativeNetworkPortalTests{
    internal static void Run(Action<string,bool> check){
        check("constrained connection is not automatically a captive portal",NativeNetworkPortal.Classify(HttpStatusCode.BadGateway,null,"offline")=="unconfirmed");
        check("redirect with a web destination confirms portal",NativeNetworkPortal.Classify(HttpStatusCode.Redirect,new Uri("https://login.example.invalid/"),"")=="portal");
        check("unsupported redirect scheme is rejected",NativeNetworkPortal.Classify(HttpStatusCode.Redirect,new Uri("file:///C:/Windows"),"")=="unconfirmed");
        check("normal connectivity response is distinguished",NativeNetworkPortal.Classify(HttpStatusCode.OK,null,"Microsoft Connect Test")=="online");
        check("connection key rejects malformed SSID",NativeNetworkPortal.Key(JsonSerializer.SerializeToElement(new{id="adapter",ssidHex="../"}))=="");
        int probes=0,opens=0;var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var portal=new NativeNetworkPortal(()=>{},_=>Interlocked.Increment(ref opens),async cancellation=>{Interlocked.Increment(ref probes);return await completion.Task.WaitAsync(cancellation);});
        JsonElement Wifi(bool internet,bool connected=true)=>JsonSerializer.SerializeToElement(new{available=true,interfaces=new[]{new{id="owned-adapter",state=connected?1:0,ssidHex="6162",internetKnown=true,internetAvailable=internet}}});
        var empty=JsonSerializer.SerializeToElement(new{});NativeSystemPresentationSession.Published State(bool internet)=>new(empty,empty,new Dictionary<string,JsonElement>{["wifi"]=Wifi(internet)});
        portal.Observe(Wifi(false));portal.Observe(Wifi(false));check("newly limited network starts only one probe",probes==1);
        check("unconfirmed authentication is explicitly shown while checking",Text(Array(portal.Enrich(State(false)).Providers["wifi"],"interfaces")[0],"portalState")=="checking");
        portal.Observe(Wifi(true));completion.SetResult("portal");SpinWait.SpinUntil(()=>completion.Task.IsCompleted,100);
        check("Internet availability clears authentication prompt",Text(Array(portal.Enrich(State(true)).Providers["wifi"],"interfaces")[0],"portalHint")=="");
        portal.Observe(Wifi(false,false));portal.Observe(Wifi(false));check("reconnection of same network does not repeat the automatic probe",probes==1);
        check("outdated completion cannot reopen browser after authentication",opens==0);
        NativeSystemPanel Panel(object value)=>NativeSystemPanel.Build("@magic/network-popup",new(empty,empty,new Dictionary<string,JsonElement>{["wifi"]=JsonSerializer.SerializeToElement(value)}));
        var adapter=new{id="owned",name="Owned Wi-Fi",state=1,radioOn=true,networks=new[]{new{ssidHex="6162",profile="Owned profile",authentication=7,signal=80,secured=true,connected=true}}};
        var pending=Panel(new{available=true,interfaces=new[]{new{id="owned",name="Owned Wi-Fi",radioOn=true}}});
        check("pending network panel exposes status and real radio/refresh actions",pending.Data["rows"]!.AsArray().Any(r=>r?["id"]?.GetValue<string>()=="network-details-status")&&pending.Commands["scan:owned"].Operation=="wifi-action"&&pending.Commands["radio:owned"].BooleanValue);
        var loadedNetwork=Panel(new{available=true,networkDetailsRequested=true,interfaces=new[]{adapter}});
        check("ready network panel removes pending row and retains disconnect/forget actions",!loadedNetwork.Data["rows"]!.AsArray().Any(r=>r?["id"]?.GetValue<string>()=="network-details-status")&&loadedNetwork.Commands.Values.Any(c=>c.Arguments["action"]?.GetValue<string>()=="disconnect")&&loadedNetwork.Commands.Values.Any(c=>c.Arguments["action"]?.GetValue<string>()=="forget-profile"));
        var failedNetwork=Panel(new{available=true,networkDetailsFailed=true,interfaces=new[]{new{id="owned",name="Owned Wi-Fi",radioOn=true}}});
        check("failed network read exposes retry and settings without pretending success",failedNetwork.Data["rows"]!.AsArray().Any(r=>r?["label"]?.GetValue<string>()=="网络列表暂不可用，请刷新")&&failedNetwork.Commands.ContainsKey("scan:owned")&&failedNetwork.Commands.ContainsKey("network-settings"));

    }
}
