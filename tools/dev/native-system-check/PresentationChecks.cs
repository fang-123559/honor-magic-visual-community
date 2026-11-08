using MagicCommunity;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PresentationChecks
{
    internal static void Run(Dictionary<string, bool> checks)
    {
        void Check(string name, bool pass) { checks[name] = pass; if (!pass) throw new InvalidDataException(name); }
        using var config = JsonDocument.Parse("""
        {"protocol":"Magic.System.Configuration/1","schemaVersion":1,"toolbar":{"locked":true,"appearance":{"edge":"Top","enabled":true,"itemSize":16,"outerMargin":3},"sections":{"left":[{"id":"memory","kind":"module","module":"magic.memory"}],"center":[{"id":"clock","kind":"module","module":"magic.clock"}],"right":[{"id":"tray","kind":"module","module":"magic.tray"},{"id":"external","kind":"compatibility","sourceModuleId":"third-party/test"}]}},"dock":{"locked":false,"appearance":{"edge":"Bottom","iconSize":40,"itemGap":8,"runningVisibility":"OnMonitor"},"sections":{"left":[{"id":"app","kind":"application","target":"C:/Apps/Example.exe","displayName":"Example","pinned":true,"allowPin":false,"launch":{"executable":"C:/Apps/Example.exe","arguments":["a b"],"workingDirectory":"C:/Apps"}},{"id":"uwp","kind":"application","target":"","aumid":"Example.Uwp!App","pinned":true}],"center":[{"id":"sep","kind":"separator"}],"right":[]}}}
        """);
        using var snapshot = JsonDocument.Parse("""
        {"protocol":"Magic.System.Native/1","system":{"memoryLoadPercent":42,"cpuPercent":null},"displays":[{"id":"left","left":-1920,"top":0,"width":1920,"height":1080,"dpi":144,"primary":false},{"id":"main","left":0,"top":0,"width":2560,"height":1440,"dpi":96,"primary":true}],"windows":[{"id":"opaque:1","hwnd":"101","executable":"c:\\apps\\example.EXE","title":"Editor","left":-1500,"top":20,"right":-1000,"bottom":600,"focused":true,"minimized":false},{"id":"opaque:2","hwnd":"102","executable":"C:\\Apps\\Other.exe","title":"Other 1","left":10,"top":20,"right":1000,"bottom":600},{"id":"opaque:3","hwnd":"103","executable":"c:/Apps/other.EXE","title":"Other 2","left":10,"top":20,"right":1000,"bottom":600},{"id":"protected:1","hwnd":"104","executable":"","title":"Protected 1"},{"id":"protected:2","hwnd":"105","executable":"","title":"Protected 2"}]}
        """);
        using var tray=JsonDocument.Parse("""{"available":true,"icons":[{"id":"test","stableId":"path:1","tooltip":"Fixture","ownerHwnd":"15","promoted":true,"actionAvailable":true,"imageVersion":"123","png":"AAAA"}]}""");
        var live=NativeSystemPresentation.Project(snapshot.RootElement,config.RootElement,new Dictionary<string,JsonElement>{{"tray",tray.RootElement}},true,123);
        Check("interactive-presentation-does-not-claim-manual-acceptance",!live["preview"]!.GetValue<bool>()&&!live["productionReady"]!.GetValue<bool>()&&live["commandDispatcherAttached"]!.GetValue<bool>());
        var liveTray=live["toolbar"]!["right"]!.AsArray().First(x=>x!["type"]!.GetValue<string>()=="Tray")!;
        Check("native-tray-images-requested-through-bounded-icon-channel",liveTray["icon"]!.GetValue<string>()=="native:tray"&&liveTray["imageVersion"]!.GetValue<string>()=="123"&&!liveTray.AsObject().ContainsKey("png"));
        Check("native-tray-parent-order-identity-retained",liveTray["module"]!.GetValue<string>()=="tray"&&live["toolbar"]!["right"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="tray"));
        var providers=new Dictionary<string,JsonElement>{{"tray",tray.RootElement}};
        var state=new NativeSystemPresentationSession.Published(config.RootElement,snapshot.RootElement,providers);
        var panel=NativeSystemPanel.Build("@magic/system-tray",state);
        Check("tray-panel-keeps-image-and-action-identities-paired",panel.Data["rows"]![0]!["id"]!.GetValue<string>()=="tray:test"&&panel.Commands["tray:test"].Arguments["iconId"]!.GetValue<string>()=="test");
        Check("panel-does-not-enable-unoffered-commands",!panel.Commands.ContainsKey("tray:forged"));
        using var denied=JsonDocument.Parse("""{"hasPackageIdentity":false,"access":"independent-identity-required","notifications":[]}""");providers["notifications"]=denied.RootElement;
        var deniedPanel=NativeSystemPanel.Build("@magic/notifications",state);
        Check("notification-panel-offers-own-authorization-without-inheriting-access",deniedPanel.Commands.ContainsKey("authorize"));
        using var unpackaged=JsonDocument.Parse("""{"hasPackageIdentity":false,"available":true,"access":"allowed","notifications":[]}""");providers["notifications"]=unpackaged.RootElement;
        Check("unpackaged-notification-access-does-not-repeat-authorization",!NativeSystemPanel.Build("@magic/notifications",state).Commands.ContainsKey("authorize"));
        Check("music-discoverable-in-toolbar-only",live["toolbarModules"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="magic.music")&&!live["dockModules"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="magic.music"));
        using var nullPower=JsonDocument.Parse("""{"system":{"power":{"batteryPercent":null,"acLineStatus":1}}}""");
        Check("power-panel-handles-desktop-without-battery",NativeSystemPanel.Build("@magic/quick-settings",state with{Snapshot=nullPower.RootElement},true).Data["rows"]![0]!["label"]!.GetValue<string>()=="电源状态");
        Check("official-icon-projection-keeps-executable-and-action-identity",live["dock"]!["left"]![0]!["iconExecutable"]!.GetValue<string>()=="C:/Apps/Example.exe"&&live["dock"]!["left"]![0]!["id"]!.GetValue<string>()=="app");
        var iconConfig=JsonNode.Parse(config.RootElement.GetRawText())!;
        foreach(string module in new[]{"magic.battery","magic.bluetooth","magic.network","magic.audio","magic.notifications"})iconConfig["toolbar"]!["sections"]!["left"]!.AsArray().Add(new JsonObject{["id"]=module,["kind"]="module",["module"]=module});
        var iconSnapshot=JsonNode.Parse(snapshot.RootElement.GetRawText())!;iconSnapshot["system"]!["power"]=new JsonObject{["batteryPercent"]=53,["acLineStatus"]=1};
        using var iconC=JsonDocument.Parse(iconConfig.ToJsonString());using var iconS=JsonDocument.Parse(iconSnapshot.ToJsonString());
        using var bt=JsonDocument.Parse("""{"available":true,"radioOn":false,"devices":[]}""");
        using var sound=JsonDocument.Parse("""{"available":true,"volume":0.4,"muted":true}""");
        using var iconWifi=JsonDocument.Parse("""{"available":true,"interfaces":[{"state":1,"networks":[{"connected":true,"signal":61}]}]}""");
        var iconView=NativeSystemPresentation.Project(iconS.RootElement,iconC.RootElement,new Dictionary<string,JsonElement>{{"bluetooth",bt.RootElement},{"audio",sound.RootElement},{"wifi",iconWifi.RootElement}});
        var modules=iconView["toolbar"]!["left"]!.AsArray().ToDictionary(x=>x!["id"]!.GetValue<string>(),x=>x!);
        Check("official-battery-state-retains-percentage-and-ac",modules["magic.battery"]["batteryPercent"]!.GetValue<double>()==53&&modules["magic.battery"]["acLineStatus"]!.GetValue<double>()==1);
        Check("official-bluetooth-off-not-confused-with-unavailable",modules["magic.bluetooth"]["dataAvailable"]!.GetValue<bool>()&&!modules["magic.bluetooth"]["bluetoothOn"]!.GetValue<bool>());
        Check("official-volume-mute-retains-audio-actions",modules["magic.audio"]["dockMuted"]!.GetValue<bool>()&&modules["magic.audio"]["dockVolume"]!.GetValue<double>()==0.4);
        Check("official-wifi-icon-uses-connected-network-strength",modules["magic.network"]["dockOnline"]!.GetValue<bool>()&&modules["magic.network"]["wifiSignal"]!.GetValue<double>()==61);
        string before = config.RootElement.GetRawText();
        var view = NativeSystemPresentation.Project(snapshot.RootElement, config.RootElement);
        var mediaModules=view["dockModules"]!.AsArray().Where(x=>x!["id"]!.GetValue<string>()=="magic.player").ToArray();
        Check("one-media-module-with-video-icon",mediaModules.Length==1&&mediaModules[0]!["label"]!.GetValue<string>()=="媒体"&&mediaModules[0]!["icon"]!.GetValue<string>()=="MagicMediaVideo");
        Check("presentation-keeps-preview-and-actions-disabled", view["preview"]!.GetValue<bool>() && !view["productionReady"]!.GetValue<bool>() && !view["toolbar"]!["left"]![0]!["enabled"]!.GetValue<bool>());
        Check("presentation-retains-toolbar-regions-and-order", view["toolbar"]!["center"]![0]!["id"]!.GetValue<string>() == "clock" && view["toolbar"]!["right"]![1]!["id"]!.GetValue<string>() == "external");
        Check("presentation-retains-unavailable-and-external-items", view["toolbar"]!["right"]!.AsArray().Count == 2 && view["warnings"]!.AsArray().Count == 2 && view["toolbar"]!["right"]![1]!["compatibilityMetadataOnly"]!.GetValue<bool>());
        Check("presentation-reuses-native-clock-and-memory-data", view["toolbar"]!["center"]![0]!["clock"]!.GetValue<bool>() && view["toolbar"]!["left"]![0]!["label"]!.GetValue<string>() == "42%");
        Check("presentation-keeps-layout-and-lock-preferences", view["toolbarSettings"]!["position"]!.GetValue<string>() == "Top" && view["toolbarSettings"]!["margin"]!.GetValue<int>() == 3 && view["toolbar"]!["isReorderDisabled"]!.GetValue<bool>() && view["dockSettings"]!["size"]!.GetValue<int>() == 40);
        Check("presentation-mixed-dpi-negative-monitor-coordinates", view["monitors"]![0]!["scale"]!.GetValue<double>() == 1.5 && view["monitors"]![0]!["rect"]!["right"]!.GetValue<double>() == 0 && view["focused"]!["monitor"]!.GetValue<string>() == "left");
        Check("overlap-hide-receives-window-bounds-and-monitor", view["appWindows"]!.AsArray().Count == 5 && view["appWindows"]![0]!["hwnd"]!.GetValue<ulong>() == 101 && view["appWindows"]![0]!["monitor"]!.GetValue<string>() == "left" && view["appWindows"]![0]!["rect"]!["bottom"]!.GetValue<double>() == 600);
        var minimized = JsonNode.Parse(snapshot.RootElement.GetRawText())!; minimized["windows"]![0]!["minimized"] = true;
        using var minimizedDocument = JsonDocument.Parse(minimized.ToJsonString());
        Check("overlap-hide-retains-minimized-state", NativeSystemPresentation.Project(minimizedDocument.RootElement, config.RootElement)["appWindows"]![0]!["isIconic"]!.GetValue<bool>());
        using var wifi = JsonDocument.Parse("""{"available":true,"interfaces":[{"id":"adapter","radioOn":true,"networks":[{"ssidHex":"74657374","authentication":7,"profile":"one","signal":20},{"ssidHex":"74657374","authentication":7,"profile":"one","signal":80,"connected":true},{"ssidHex":"74657374","authentication":7,"profile":"two"},{"ssidHex":"74657374","authentication":1,"profile":""}]}]}""");
        var network = NativeSystemPanel.Build("@seelen/network-popup", state with { Providers = new Dictionary<string, JsonElement> { ["wifi"] = wifi.RootElement } });
        var networkRows = network.Data["rows"]!.AsArray().Where(x => x!["id"]!.GetValue<string>().StartsWith("network:")).ToArray();
        Check("wifi-repeated-ssid-does-not-abort-panel", networkRows.Length == 3 && networkRows.Select(x => x!["id"]!.GetValue<string>()).Distinct().Count() == 3);
        Check("wifi-same-ssid-has-one-disclosure-header",network.Data["rows"]!.AsArray().Count(x=>x!["id"]!.GetValue<string>().StartsWith("wifi-group:"))==1);
        Check("wifi-distinct-profiles-remain-authenticated-actions",networkRows.All(x=>x!["networkGroup"]!.GetValue<string>()=="wifi-group:74657374")&&network.Commands.Count(x=>x.Key.StartsWith("network:"))==3);
        Check("wifi-exact-duplicates-prefer-connected-record", networkRows.Any(x => x!["detail"]!.GetValue<string>() == "已连接"));
        Check("wifi-profile-actions-retain-correct-target", network.Commands.Values.Where(x => x.Operation == "wifi-action" && x.Arguments["action"]?.GetValue<string>() == "forget-profile").Select(x => x.Arguments["profile"]!.GetValue<string>()).Order().SequenceEqual(new[] { "one", "two" }));
        Check("presentation-groups-pinned-executable-case-insensitively", view["dock"]!["left"]![0]!["windows"]!.AsArray().Count == 1 && view["dock"]!["left"]![0]!["windows"]![0]!["nativeWindowId"]!.GetValue<string>() == "opaque:1");
        Check("presentation-retains-launch-arguments-and-pin-state", view["dock"]!["left"]![0]!["pinned"]!.GetValue<bool>() && view["dock"]!["left"]![0]!["nativeLaunch"]!["arguments"]![0]!.GetValue<string>() == "a b" && view["dock"]!["left"]![0]!["preventPinning"]!.GetValue<bool>());
        var running = view["dock"]!["center"]!.AsArray().Skip(1).ToArray();
        Check("presentation-groups-running-windows-without-duplicate-pinned-app", running.Length == 3 && running[0]!["windows"]!.AsArray().Count == 2);
        Check("presentation-does-not-merge-protected-windows-into-empty-target", running.Skip(1).All(x => x!["windows"]!.AsArray().Count == 1));
        Check("presentation-leaves-native-configuration-unchanged", config.RootElement.GetRawText() == before);
        var changed = JsonNode.Parse(snapshot.RootElement.GetRawText())!; changed["windows"] = new JsonArray(); using var closed = JsonDocument.Parse(changed.ToJsonString());
        var after = NativeSystemPresentation.Project(closed.RootElement, config.RootElement);
        Check("presentation-removes-closed-temporal-windows", after["dock"]!["center"]!.AsArray().Count == 1 && after["dock"]!["left"]![0]!["windows"]!.AsArray().Count == 0);
        var wrong = JsonNode.Parse(before)!; wrong["protocol"] = "Magic.System/1"; using var legacy = JsonDocument.Parse(wrong.ToJsonString()); bool rejected = false; try { NativeSystemPresentation.Project(snapshot.RootElement, legacy.RootElement); } catch (InvalidDataException) { rejected = true; }
        Check("presentation-rejects-unmigrated-schema", rejected);
        var crowdedConfig = JsonNode.Parse(before)!;
        var apps = new JsonArray(); for (int i = 0; i < 256; i++) apps.Add(new JsonObject { ["id"] = "app-" + i, ["kind"] = "application", ["target"] = "C:/Apps/Example.exe", ["pinned"] = true });
        crowdedConfig["dock"]!["sections"]!["left"] = apps;
        var crowdedSnapshot = JsonNode.Parse(snapshot.RootElement.GetRawText())!;
        var manyWindows = new JsonArray(); for (int i = 0; i < 512; i++) { var window = crowdedSnapshot["windows"]![0]!.DeepClone(); window["id"] = "window-" + i; window["hwnd"] = (i + 100).ToString(); manyWindows.Add(window); } crowdedSnapshot["windows"] = manyWindows;
        using var crowdedC = JsonDocument.Parse(crowdedConfig.ToJsonString()); using var crowdedS = JsonDocument.Parse(crowdedSnapshot.ToJsonString());
        var bounded = NativeSystemPresentation.Project(crowdedS.RootElement, crowdedC.RootElement);
        int associations = bounded["dock"]!["left"]!.AsArray().Sum(x => x!["windows"]!.AsArray().Count);
        Check("presentation-window-associations-bounded-under-duplicate-targets", associations == 512 && bounded["dock"]!["left"]!.AsArray().Count == 256 && bounded["warnings"]!.AsArray().Any(x => x!["reason"]!.GetValue<string>() == "window-link-budget-exhausted"));
    }
}
