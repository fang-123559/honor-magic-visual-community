using MagicCommunity;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
if(args.Length==2&&args[0]=="--inspect-original"){
 var installed=new EncoDeckIntegration(Environment.CurrentDirectory).Locate();
 var data=OriginalEncoDeckCardData.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EncoDeck"),installed);
 File.WriteAllText(args[1],JsonSerializer.Serialize(new{readOnly=true,installed.Status,programRunning=installed.Running,available=data.GetProperty("available").GetBoolean(),providerStatus=data.GetProperty("providerStatus").GetString(),left=data.TryGetProperty("left",out var l)?l.GetProperty("level"):(JsonElement?)null,right=data.TryGetProperty("right",out var r)?r.GetProperty("level"):(JsonElement?)null,@case=data.TryGetProperty("case",out var c)?c.GetProperty("level"):(JsonElement?)null}));return 0;
}
var checks=new Dictionary<string,bool>();
void Check(string name,bool ok){checks[name]=ok;if(!ok)throw new InvalidOperationException(name);}
async Task Wait(Func<bool> ready,int ms=4000){long start=Environment.TickCount64;while(!ready()){if(Environment.TickCount64-start>ms)throw new TimeoutException();await Task.Delay(15);}}
string root=Path.GetFullPath(args[0]),directory=Path.Combine(root,"data-fixture"),fixture=Path.Combine(root,"fixture","EncoDeck.exe"),report=Path.Combine(root,"result.json");Directory.CreateDirectory(directory);
string cache=Path.Combine(directory,"device-cache.json"),settings=Path.Combine(directory,"settings.json");
var live=new EncoDeckIntegration.Location("running-no-bridge",fixture,1,1);
void Cache(int left=70,int right=77,int box=3)=>File.WriteAllText(cache,JsonSerializer.Serialize(new{one=new{left=new{level=left,charging=false,timestamp=DateTimeOffset.UtcNow},right=new{level=right,charging=false},@case=new{level=box,charging=false}}}));
JsonElement Read()=>OriginalEncoDeckCardData.Read(directory,live);
var app=new EncoDeckIntegration(Path.Combine(root,"settings"),"Magic.Original.EncoDeck."+Guid.NewGuid().ToString("N"));Process? owned=null;
try{
 Cache();var state=Read();
 Check("original-cache-read-without-ipc",state.GetProperty("available").GetBoolean()&&state.GetProperty("programRunning").GetBoolean());
 Check("exact-earbud-battery",state.GetProperty("left").GetProperty("level").GetInt32()==70&&state.GetProperty("right").GetProperty("level").GetInt32()==77&&state.GetProperty("case").GetProperty("level").GetInt32()==3);
 Check("cache-never-claims-live",state.GetProperty("left").GetProperty("stale").GetBoolean()&&!state.GetProperty("controlReady").GetBoolean()&&!state.GetProperty("canSetNoise").GetBoolean());
 Check("no-unreported-control-offered",state.GetProperty("modes").GetArrayLength()==0);
 Check("not-running-separate-state",!OriginalEncoDeckCardData.Read(directory,new("installed-not-running",fixture)).GetProperty("available").GetBoolean());
 File.WriteAllText(cache,"{\"a\":{},\"b\":{}}");Check("multiple-devices-never-guessed",!Read().GetProperty("available").GetBoolean());
 string id="owned-private-device-identity",key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("encodeck-cache|"+id)).AsSpan(0,12));
 File.WriteAllText(settings,JsonSerializer.Serialize(new{preferredDeviceId=id}));File.WriteAllText(cache,JsonSerializer.Serialize(new Dictionary<string,object>{{"unrelated",new{}},{key,new{left=new{level=45}}}}));state=Read();
 Check("preferred-device-hash-selects-correct-entry",state.GetProperty("left").GetProperty("level").GetInt32()==45);
 Check("private-device-identity-never-emitted",!state.GetRawText().Contains(id)&&!state.GetRawText().Contains(key));
 File.WriteAllText(cache,"{\"other\":{}}");Check("preferred-missing-never-falls-back",!Read().GetProperty("available").GetBoolean());
 File.WriteAllText(settings,"{}");
 foreach(var (json,name) in new[]{("[]","array-cache-denied"),("{\"a\":{\"left\":{\"level\":101}}}","invalid-battery-denied"),("{\"a\":{\"left\":{\"level\":50,\"level\":2}}}","duplicate-state-denied"),(new string('x',131073),"oversized-cache-denied"),("{\"a\":{\"left\":{\"timestamp\":\"2099-01-01T00:00:00Z\"}}}","future-reading-denied")}){File.WriteAllText(cache,json);Check(name,!Read().GetProperty("available").GetBoolean());}
 Cache(-1);Check("unknown-minus-one-preserved",Read().GetProperty("left").GetProperty("level").ValueKind==JsonValueKind.Null);
 Check("recognized-control-id-mapping",OriginalEncoDeckAccessibility.Mode("AncButton","")=="NoiseCancellation"&&OriginalEncoDeckAccessibility.Mode("TransparencyButton","")=="Transparency");
 Check("arbitrary-control-not-mapped",OriginalEncoDeckAccessibility.Mode("DeleteAllButton","Erase files")=="");
 Check("caption-close-cannot-be-anc-off",OriginalEncoDeckAccessibility.ControlMode("Close","关闭",false)=="");
 Check("invoke-only-off-label-cannot-be-anc",OriginalEncoDeckAccessibility.ControlMode("","关闭",false)=="");
 Check("real-toggle-off-without-id-supported",OriginalEncoDeckAccessibility.ControlMode("","关闭",true)=="Off");
 Check("named-original-mode-remains-supported",OriginalEncoDeckAccessibility.ControlMode("OffButton","",false)=="Off");
 Check("invalid-probe-args-denied",OriginalEncoDeckAccessibility.Run(new[]{"--encodeck-accessibility"})==1);
 var freshBattery=OriginalEncoDeckAccessibility.BatteryLabel("左耳 96%");Check("labeled-original-battery-is-current",freshBattery is {Key:"left",Value.level:96,Value.stale:false});
 Check("original-battery-last-read-remains-stale",OriginalEncoDeckAccessibility.BatteryLabel("Right 98% Last read 10:20") is {Key:"right",Value.stale:true});
 Check("original-battery-charging-preserved",OriginalEncoDeckAccessibility.BatteryLabel("充电盒 80% 正在充电") is {Key:"case",Value.charging:true});
 Check("unlabeled-percent-never-guessed",OriginalEncoDeckAccessibility.BatteryLabel("96%") is null&&OriginalEncoDeckAccessibility.BatteryLabel("Case 101%") is null);
 var liveBatteryPanel=JsonSerializer.SerializeToElement(new{available=true,deviceName="Owned headset",deviceConnected=true,left=new{level=96,charging=false,stale=false},modes=new[]{new{mode="Off",label="关闭",enabled=true}},levels=Array.Empty<object>(),noiseMode="Off"});
 var mergedBattery=OriginalEncoDeckCardData.Combine(Read(),liveBatteryPanel);Check("live-labeled-ui-reading-takes-priority-over-cache",mergedBattery.GetProperty("left").GetProperty("level").GetInt32()==96&&!mergedBattery.GetProperty("left").GetProperty("stale").GetBoolean());
 Check("unreported-battery-retains-cached-freshness",mergedBattery.GetProperty("right").GetProperty("stale").GetBoolean());
 var home=OriginalEncoDeckAccessibility.Header(false,new[]{("DeviceNameText","Owned headset"),("","已连接")});
 Check("main-home-recognized-without-quick-panel-button",home.Recognized&&home.Connected&&home.Device=="Owned headset");
 Check("quick-header-retained",OriginalEncoDeckAccessibility.Header(true,new[]{("","Owned headset"),("","Connected")}).Connected);
 Check("settings-page-never-recognized",!OriginalEncoDeckAccessibility.Header(false,new[]{("","Settings"),("","Connected")}).Recognized);
 Check("contradictory-or-stale-connected-label-denied",!OriginalEncoDeckAccessibility.Header(false,new[]{("DeviceNameText","Owned headset"),("","已连接"),("","已断开")}).Connected);
 var cacheOnly=Read();var disconnected=JsonSerializer.SerializeToElement(new{available=true,deviceName="Owned headset",deviceConnected=false,modes=new[]{new{mode="Off",label="关闭",enabled=true}},levels=Array.Empty<object>(),noiseMode="Off"});
 Check("enabled-button-cannot-prove-device-connected",!OriginalEncoDeckCardData.Combine(cacheOnly,disconnected).GetProperty("canSetNoise").GetBoolean());
 File.WriteAllText(Path.Combine(root,"fixture","pipe-name.txt"),"Magic.Original.Fixture."+Guid.NewGuid().ToString("N"));
 app.Configure(fixture);owned=Process.Start(new ProcessStartInfo(fixture){UseShellExecute=false})!;await Wait(()=>app.Locate().Running);
 var frames=new ConcurrentQueue<JsonElement>();int probes=0;bool providerAvailable=true,providerConnected=true;
 Task<JsonElement> Probe(EncoDeckIntegration.Location location,string action,string name,string label,CancellationToken ct){Interlocked.Increment(ref probes);if(!providerAvailable)return Task.FromResult(JsonSerializer.SerializeToElement(new{available=false}));return Task.FromResult(JsonSerializer.SerializeToElement(new{available=true,deviceName="Owned test earphones",deviceConnected=providerConnected,noiseMode=action=="mode"?"Off":"NoiseCancellation",modes=new[]{new{mode="NoiseCancellation",label="降噪",selected=true,enabled=true},new{mode="Off",label="关闭",selected=false,enabled=true}},levels=Array.Empty<object>()}));}
 using(var provider=new OriginalEncoDeckCardData(app,value=>{frames.Enqueue(value);return Task.CompletedTask;},directory,Probe,_=>true)){
  Cache();provider.Demand(true);await Wait(()=>frames.Count>0);
  Check("initial-read-only-snapshot-without-opening-original",probes==1);
  Check("file-observer-active-only-when-visible",JsonSerializer.SerializeToElement(provider.Diagnostics).GetProperty("watching").GetBoolean());
  int baseline=frames.Count;Cache(69);await Wait(()=>frames.Last().GetProperty("left").GetProperty("level").GetInt32()==69);
  Check("file-change-pushes-new-battery",frames.Count>baseline);
  baseline=frames.Count;await Task.Delay(500);Check("no-idle-poll-or-push",frames.Count==baseline&&probes==2);
  Check("explicit-refresh-uses-compatibility-probe",await provider.SendActionAsync("refresh",null,CancellationToken.None)&&probes==3);
  await Wait(()=>frames.Last().GetProperty("deviceName").GetString()=="Owned test earphones");
  Check("only-real-reported-controls-enabled",frames.Last().GetProperty("modes").GetArrayLength()==2&&frames.Last().GetProperty("canSetNoise").GetBoolean());
  Check("unsupported-mode-never-probed",!await provider.SendActionAsync("setNoiseMode",8,CancellationToken.None)&&probes==3);
  // The fixture returns its previous selection on readback: do not announce success.
  Check("unconfirmed-selection-never-success",!await provider.SendActionAsync("setNoiseMode",1,CancellationToken.None));
  providerConnected=false;Cache(68);await Wait(()=>!frames.Last().GetProperty("deviceConnected").GetBoolean());
  Check("disconnect-event-removes-controls",!frames.Last().GetProperty("canSetNoise").GetBoolean());
  providerConnected=true;Cache(67);await Wait(()=>frames.Last().GetProperty("deviceConnected").GetBoolean());
  providerAvailable=false;Cache(66);await Wait(()=>!frames.Last().GetProperty("controlReady").GetBoolean());
  Check("provider-disappearance-invalidates-previous-controls",!frames.Last().GetProperty("deviceConnected").GetBoolean());
  provider.Demand(false);baseline=frames.Count;Cache(68);await Task.Delay(400);
  Check("hidden-stops-file-watch-and-emission",frames.Count==baseline&&!JsonSerializer.SerializeToElement(provider.Diagnostics).GetProperty("watching").GetBoolean());
  Check("hidden-cannot-command",!await provider.SendActionAsync("refresh",null,CancellationToken.None));
 }
 var hiddenFrames=new ConcurrentQueue<JsonElement>();int beforeHidden=probes;
 using(var hidden=new OriginalEncoDeckCardData(app,value=>{hiddenFrames.Enqueue(value);return Task.CompletedTask;},directory,Probe,_=>false)){
  Cache(65);hidden.Demand(true);await Wait(()=>hiddenFrames.Count>0);Check("tray-only-original-does-not-start-accessibility-helper",probes==beforeHidden);
  Cache(64);await Wait(()=>hiddenFrames.Last().GetProperty("left").GetProperty("level").GetInt32()==64);Check("hidden-original-still-pushes-cache-without-false-connection",probes==beforeHidden&&!hiddenFrames.Last().GetProperty("deviceConnected").GetBoolean());hidden.Demand(false);
 }
 File.WriteAllText(report,JsonSerializer.Serialize(new{allPass=true,count=checks.Count,checks,scope="Original cache contract + owned process + injected accessibility fixtures only; no original EncoDeck actions or modified fork."},new JsonSerializerOptions{WriteIndented=true}));return 0;
}catch(Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{allPass=false,count=checks.Count,checks,error=e.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
finally{if(owned is not null){if(!owned.HasExited)owned.Kill();await owned.WaitForExitAsync();owned.Dispose();}}
