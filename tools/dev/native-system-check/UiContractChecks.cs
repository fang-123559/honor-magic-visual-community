using MagicCommunity;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
internal static class UiContractChecks
{
 internal static void Run(Dictionary<string,bool> checks,string root,string output){
  PopupIdentityChecks.Run(checks,root);
  void Check(string key,bool ok){checks[key]=ok;if(!ok)throw new InvalidOperationException(key);}
  var source=File.ReadAllText(Path.Combine(root,"src/MagicCommunity/NativeSystemPresentation.cs"));
  var modules=Regex.Matches(source,"\\[\"(magic\\.[^\"]+)\"\\] = \\(").Select(x=>x.Groups[1].Value).Where(x=>x!="magic.player").Distinct().ToArray(); // Media remains a Dock-only catalog entry.
  var assets=Regex.Matches(File.ReadAllText(Path.Combine(root,"src/composition-trial/native-toolbar-icons.h")),"\\{L\"([^\"]+)\"").Select(x=>x.Groups[1].Value).ToHashSet();
  JsonObject Bar(JsonArray rows)=>new(){["sections"]=new JsonObject{["left"]=rows,["center"]=new JsonArray(),["right"]=new JsonArray()},["appearance"]=new JsonObject{["enabled"]=true}};
  var rows=new JsonArray();foreach(string module in modules)rows.Add(new JsonObject{["id"]=module,["kind"]="module",["module"]=module});
  var config=new JsonObject{["protocol"]="Magic.System.Configuration/1",["schemaVersion"]=1,["toolbar"]=Bar(rows),["dock"]=Bar(new JsonArray())};
  using var c=JsonDocument.Parse(config.ToJsonString());using var s=JsonDocument.Parse("""{"protocol":"Magic.System.Native/1","windows":[],"displays":[{"id":"fixture","left":0,"top":0,"width":1920,"height":1080,"dpi":192,"primary":true}],"system":{"power":{"batteryPercent":80,"acLineStatus":1}}}""");
  var projected=NativeSystemPresentation.Project(s.RootElement,c.RootElement,interactive:true,hostPid:99);var actual=projected["toolbar"]!["left"]!.AsArray();
  Check("all-native-module-icons-present-in-renderer",actual.SelectMany(x=>x!["content"]!.AsArray()).Where(x=>x!["kind"]!.GetValue<string>()=="icon").All(x=>assets.Contains(x!["value"]!.GetValue<string>())));
  Check("all-module-identities-retained",actual.Count==modules.Length&&actual.Select(x=>x!["id"]!.GetValue<string>()).Distinct().Count()==modules.Length);
  using var audio=JsonDocument.Parse("""{"available":true,"defaultDeviceId":"device","defaultDeviceName":"Speaker","volume":0.65,"muted":true,"canSelectDefault":true,"devices":[{"id":"device","name":"Speaker"}]}""");
  using var bluetooth=JsonDocument.Parse("""{"available":true,"radioOn":true,"devices":[{"id":"one","name":"Device","paired":true,"connected":false}]}""");
  using var notification=JsonDocument.Parse("""{"available":true,"canClearAll":true,"hasPackageIdentity":true,"access":"allowed","notifications":[{"id":"opaque","appName":"Example","appId":"Example!App","createdFileTime":"134375328000000000","text":["Fixture","Notification body"]}]}""");
  var providers=new Dictionary<string,JsonElement>{{"audio",audio.RootElement},{"bluetooth",bluetooth.RootElement},{"notifications",notification.RootElement}};var state=new NativeSystemPresentationSession.Published(c.RootElement,s.RootElement,providers);var fixtures=new JsonArray();
  foreach(string kind in new[]{"@magic/quick-settings","@magic/bluetooth-popup","@magic/media-popup","@magic/notifications","@magic/power-menu"}){
   var panel=NativeSystemPanel.Build(kind,state);var list=panel.Data["rows"]!.AsArray();Check("panel-title-"+kind,panel.Data["title"]!.GetValue<string>().Length>0);var controls=new HashSet<string>{"button","toggle","range","text","password","tray"};
   Check("renderer-controls-"+kind,list.All(x=>controls.Contains(x!["control"]!.GetValue<string>())));
   if(kind=="@magic/media-popup"){Check("media-volume-uses-renderer-range",list.Any(x=>x!["id"]!.GetValue<string>().StartsWith("volume:")&&x["control"]!.GetValue<string>()=="range"));Check("muted-toggle-carries-numeric-value",list.Any(x=>x!["id"]!.GetValue<string>().StartsWith("mute:")&&x["value"]!.GetValue<double>()==1));Check("default-output-uses-renderer-identity",list.Any(x=>x!["id"]!.GetValue<string>()=="default:device"));}
   if(kind=="@magic/notifications"){
    Check("notification-retains-card-and-action-identities",list.Any(x=>x!["id"]!.GetValue<string>()=="notification:opaque")&&panel.Commands["dismiss:opaque"].Arguments["notificationId"]!.GetValue<string>()=="opaque");
    var notice=list.First(x=>x!["id"]!.GetValue<string>()=="notification:opaque")!;
    Check("notification-display-metadata-retains-provider-identity",notice["iconUmid"]!.GetValue<string>()=="Example!App"&&notice["createdFileTime"]!.GetValue<string>()=="134375328000000000");
    Check("notification-title-and-body-are-not-flattened",notice["detail"]!.GetValue<string>()=="Fixture\nNotification body");
    Check("notification-clear-is-real-confirmed-backend-command",panel.Commands.Count==5&&panel.Commands["clear"].Operation=="notifications-clear"&&panel.Commands["clear"].Arguments["confirmed"]!.GetValue<bool>()&&panel.Commands["focus"].Local=="notification-quiet");
   }
   fixtures.Add(new JsonObject{["id"]=kind.Split('/')[1],["kind"]=kind,["width"]=360,["height"]=520,["account"]=new JsonObject{["name"]="Fixture"},["data"]=panel.Data.DeepClone()});
  }
  var many=new JsonArray();for(int i=0;i<120;i++)many.Add(new JsonObject{["id"]=i.ToString(),["appName"]="App "+i,["appId"]="Owned!"+i,["text"]=new JsonArray("Title")});
  using var full=JsonDocument.Parse(new JsonObject{["hasPackageIdentity"]=true,["access"]="allowed",["notifications"]=many}.ToJsonString());
  var bounded=NativeSystemPanel.Build("@magic/notifications",state with{Providers=new Dictionary<string,JsonElement>{{"notifications",full.RootElement}}});var boundedRows=bounded.Data["rows"]!.AsArray();
  Check("notification-cap-retains-settings-footer-and-pairs",boundedRows.Count<=192&&boundedRows.Last()!["id"]!.GetValue<string>()=="notification-settings"&&boundedRows.Count(x=>x!["id"]!.GetValue<string>().StartsWith("notification:"))==boundedRows.Count(x=>x!["id"]!.GetValue<string>().StartsWith("dismiss:")));
  Check("notification-cap-does-not-overwrite-last-card-metadata",boundedRows.Where(x=>x!["id"]!.GetValue<string>().StartsWith("notification:")).All(x=>x!["iconUmid"]!.GetValue<string>()=="Owned!"+x["id"]!.GetValue<string>().Split(':')[1]));
  var power=NativeSystemPanel.Build("@magic/quick-settings",state,true);Check("battery-details-kept-in-power-view",power.Data["nativePowerView"]!.GetValue<bool>()&&power.Data["rows"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="power-status"));
  CenterActionChecks.Run(checks,state);
  NotificationWindowChecks.Run(checks);
  using var connectedBluetooth=JsonDocument.Parse("""{"available":true,"radioOn":true,"devices":[{"id":"connected","name":"Headphones","paired":true,"connected":true,"deviceIcon":"IoHeadset"},{"id":"paired","name":"Mouse","paired":true,"connected":false,"deviceIcon":"BsMouse","batteryPercent":80},{"id":"new","name":"Unpaired","paired":false,"connected":false}]}""");
  var btPanel=NativeSystemPanel.Build("@magic/bluetooth-popup",state with{Providers=new Dictionary<string,JsonElement>{{"bluetooth",connectedBluetooth.RootElement}}});
  Check("unknown-bluetooth-radio-is-explicit-and-not-clickable",!btPanel.Commands.ContainsKey("bluetooth")&&btPanel.Data["rows"]!.AsArray().First(r=>r!["id"]!.GetValue<string>()=="bluetooth")!["label"]!.GetValue<string>().Contains("状态未知"));
  using var knownRadio=JsonDocument.Parse("""{"available":true,"radioAvailable":true,"radioOn":true,"devices":[]}""");
  var knownPanel=NativeSystemPanel.Build("@magic/bluetooth-popup",state with{Providers=new Dictionary<string,JsonElement>{{"bluetooth",knownRadio.RootElement}}});
  Check("known-bluetooth-radio-retains-switch",knownPanel.Commands.ContainsKey("bluetooth")&&knownPanel.Data["rows"]!.AsArray().First(r=>r!["id"]!.GetValue<string>()=="bluetooth")!["active"]!.GetValue<bool>());
  var disconnect=btPanel.Commands["disconnect:connected"];var forget=btPanel.Commands["forget:connected"];
  Check("bluetooth-disconnect-is-distinct-from-unpair",disconnect.Operation=="bluetooth-action"&&disconnect.Arguments["action"]!.GetValue<string>()=="disconnect"&&disconnect.Arguments["deviceId"]!.GetValue<string>()=="connected"&&forget.Operation=="bluetooth-unpair"&&forget.Arguments["deviceId"]!.GetValue<string>()=="connected");
  Check("bluetooth-actions-follow-pairing-and-connection-state",!btPanel.Commands.ContainsKey("connect:connected")&&!btPanel.Commands.ContainsKey("disconnect:paired")&&btPanel.Commands.ContainsKey("connect:paired")&&btPanel.Commands.ContainsKey("pair:new")&&!btPanel.Commands.ContainsKey("forget:new"));
  var btRows=btPanel.Data["rows"]!.AsArray();var headphone=btRows.First(r=>r!["id"]!.GetValue<string>()=="device:connected")!;var mouse=btRows.First(r=>r!["id"]!.GetValue<string>()=="device:paired")!;
  Check("bluetooth-device-type-metadata-retained-without-inventing-battery",headphone["deviceIcon"]!.GetValue<string>()=="IoHeadset"&&headphone["batteryPercent"] is null&&mouse["deviceIcon"]!.GetValue<string>()=="BsMouse"&&mouse["batteryPercent"]!.GetValue<double>()==80);
  using var mic=JsonDocument.Parse("""{"available":true,"defaultDeviceId":"input","defaultDeviceName":"Microphone","volume":0.3}""");using var mixer=JsonDocument.Parse("""{"deviceId":"device","sessions":[{"id":"session","name":"Owned app","volume":0.5}]}""");
  var audioPanel=NativeSystemPanel.Build("@magic/media-popup",state with{Providers=new Dictionary<string,JsonElement>(providers){["microphone"]=mic.RootElement,["mixer"]=mixer.RootElement}});
  Check("sound-secondary-ranges-retain-authenticated-endpoints",audioPanel.Commands["volume:microphone"].Operation=="microphone-action"&&audioPanel.Commands["volume:microphone"].Arguments["deviceId"]!.GetValue<string>()=="input"&&audioPanel.Commands["session:session"].Operation=="mixer-action"&&audioPanel.Commands["session:session"].Arguments["sessionId"]!.GetValue<string>()=="session"&&audioPanel.Commands["session:session"].ValueField=="value"&&!audioPanel.Commands["session:session"].BooleanValue);
  foreach(int playback in new[]{4,5,3,0}){
   using var player=JsonDocument.Parse(JsonSerializer.Serialize(new{available=true,sessions=new[]{new{id="owned-player",title="Owned track",status=playback,canPrevious=true,canNext=true}}}));
   var transport=NativeSystemPanel.Build("@magic/media-popup",state with{Providers=new Dictionary<string,JsonElement>(providers){["media"]=player.RootElement}});
   var middle=transport.Data["rows"]!.AsArray().Single(r=>r!["id"]!.GetValue<string>()=="player:owned-player:play")!;
   Check("media-middle-label-and-glyph-state-"+playback,middle["label"]!.GetValue<string>()==(playback==4?"暂停":"播放")&&middle["active"]!.GetValue<bool>()==(playback==4));
   var action=transport.Commands["player:owned-player:play"];Check("media-middle-retains-real-toggle-"+playback,action.Operation=="media-action"&&action.Arguments["action"]!.GetValue<string>()=="toggle"&&action.Arguments["sessionId"]!.GetValue<string>()=="owned-player");
   if(playback is 4 or 5)fixtures.Add(new JsonObject{["id"]="media-transport-"+playback,["kind"]="@magic/media-popup",["width"]=360,["height"]=520,["data"]=transport.Data.DeepClone()});
  }
  Check("shutdown-menu-keeps-native-action-rows",NativeSystemPanel.Build("@magic/power-menu",state).Data["rows"]!.AsArray().Count==6);
  fixtures.Add(new JsonObject{["id"]="power-details",["kind"]="@magic/quick-settings",["width"]=360,["height"]=520,["data"]=power.Data.DeepClone()});foreach(var item in PowerPanelChecks.Run(checks))fixtures.Add(item!.DeepClone());Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"panel-fixtures.json"),fixtures.ToJsonString());
 }
}
