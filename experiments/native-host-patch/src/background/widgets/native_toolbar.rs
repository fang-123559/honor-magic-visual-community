//! Presentation of built-in toolbar modules, translated from the bundled plugin
//! templates. No JavaScript evaluator, new sampler or independent polling loop.
use super::*;

pub(super) const RESOURCES: [(&str, &'static str); 5] = [
    ("@default/memory-usage", SeelenEvent::SystemMemoryChanged),
    ("@default/cpu-usage", SeelenEvent::SystemCoresChanged),
    ("@default/gpu-usage", SeelenEvent::SystemGpusChanged),
    ("@default/disk-usage", SeelenEvent::SystemDisksChanged),
    ("@default/network-usage", SeelenEvent::SystemNetworkChanged),
];
pub(super) fn translated(settings:&Value,key:&str)->String {
    static STRINGS:std::sync::LazyLock<Value>=std::sync::LazyLock::new(||serde_json::from_str(include_str!("native-shell-strings.json")).expect("generated translations"));
    let language=settings["language"].as_str().unwrap_or("en").to_lowercase();let short=language.split('-').next().unwrap_or("en");
    STRINGS[&language][key].as_str().or(STRINGS[short][key].as_str()).or(STRINGS["en"][key].as_str()).unwrap_or(key).to_owned()
}
fn icon(name: &str) -> Value { json!({"kind":"icon","value":name}) }
fn text(value: impl Into<String>) -> Value { json!({"kind":"text","value":value.into()}) }
fn image(path: Value) -> Value { json!({"kind":"image","value":path}) }
fn number(value: &Value, key: &str) -> f64 { value[key].as_f64().unwrap_or(0.0) }
fn sum(values: &Value, key: &str) -> f64 {
    values.as_array().into_iter().flatten().map(|v|number(v,key)).sum()
}
fn rates(mut first: f64, mut second: f64, bits: bool) -> String {
    let units=if bits { ["b","Kb","Mb","Gb","Tb","Pb","Eb","Zb","Yb"] }
        else { ["B","KB","MB","GB","TB","PB","EB","ZB","YB"] };
    let base=if bits {1000.0}else{1024.0};let mut unit=0;
    while (first>=base||second>=base)&&unit<8 {first/=base;second/=base;unit+=1;}
    let format=|v:f64| if v==0.0 {"-".into()}else{format!("{v:.0}{}{}",units[unit],if bits {"ps"}else{"/s"})};
    format!("{} | {}",format(first),format(second))
}
pub(super) struct Presentation {
    pub rows: HashMap<String, Value>,
}
impl Presentation {
    pub fn new(toolbar: &Value, settings: &Value) -> Self {
        let t=|key:&str|translated(settings,key);
        let ids=["left","center","right"].into_iter().flat_map(|s|toolbar[s].as_array().into_iter().flatten())
            .filter_map(Value::as_str).collect::<HashSet<_>>();
        let mut rows=HashMap::new();
        let mut put=|id:&str, label:String, spans:Vec<Value>, tooltip:String| {
            rows.insert(id.to_owned(),json!({"content":spans,"label":label,"tooltip":tooltip}));
        };
        for (id,_) in RESOURCES.iter().filter(|(id,_)|ids.contains(id)) {
            let mut tooltip=String::new();
            let (glyph,label)=match *id {
                "@default/memory-usage"=>{
                    let memory=Handlers::get_system_memory();let mut total=memory.total as f64;
                    let mut used=memory.total.saturating_sub(memory.free) as f64;let mut index=0;
                    let units=["B","KB","MB","GB","TB","PB","EB","ZB","YB"];
                    while total>=1024.0&&index<8 {total/=1024.0;used/=1024.0;index+=1;}
                    ("FaMemory",format!("{used:.0}{} / {total:.0}{}",units[index],units[index]))
                },
                "@default/cpu-usage"=>{let cores=Handlers::get_system_cores();
                    let used=if cores.is_empty(){0.0}else{cores.iter().map(|c|c.usage).sum::<f32>()/cores.len() as f32};
                    ("LuCpu",format!("{used:.0}%"))},
                "@default/gpu-usage"=>{let used=Handlers::get_system_gpus().iter().map(|g|g.usage).fold(0.0,f32::max);
                    ("BsGpuCard",format!("{used:.0}%"))},
                "@default/disk-usage"=>{let values=serde_json::to_value(Handlers::get_system_disks()).unwrap_or_default();
                    tooltip=values.as_array().into_iter().flatten().map(|disk|{let mut total=number(disk,"totalSpace");let mut used=total-number(disk,"availableSpace");let mut unit=0;let units=["B","KB","MB","GB","TB","PB","EB","ZB","YB"];while total>=1024.0&&unit<8{total/=1024.0;used/=1024.0;unit+=1;}format!("{} -> {used:.1}{} / {total:.1}{}",disk["mountPoint"].as_str().unwrap_or(""),units[unit],units[unit])}).collect::<Vec<_>>().join("\n");
                    ("BsDeviceHdd",rates(sum(&values,"readBytes"),sum(&values,"writtenBytes"),false))},
                _=>{let values=serde_json::to_value(Handlers::get_system_network()).unwrap_or_default();
                    ("PiArrowsDownUpBold",rates(sum(&values,"received")*8.0,sum(&values,"transmitted")*8.0,true))},
            };
            put(id,label.clone(),vec![icon(glyph),text(" "),text(label.clone())],tooltip);
        }
        let focused=serde_json::to_value(Handlers::get_focused_app()).unwrap_or_default();
        for (id,field) in [("@default/focused-app","name"),("@default/focused-app-title","title")] {
            if !ids.contains(id) {continue;}
            let label=focused[field].as_str().unwrap_or("").to_owned();let mut spans=Vec::new();
            if field=="name" {spans.push(image(focused["exe"].clone()));spans.push(text("  "));}
            spans.push(text(label.clone()));put(id,label.clone(),spans,label);
        }
        if ids.contains("@seelen/tb-user-menu") {
            let user=serde_json::to_value(Handlers::get_user()).unwrap_or_default();
            let xbox=settings["byWidget"]["@seelen/user-menu"]["displayNameSource"]=="xboxGamertag";
            let label=if xbox {user["xboxGamertag"].as_str().filter(|s|!s.is_empty()).or(user["name"].as_str())}else{user["name"].as_str()}.unwrap_or("").to_owned();
            put("@seelen/tb-user-menu",label.clone(),vec![image(user["profilePicturePath"].clone()),text(" "),text(label.clone())],label);
        }
        if ids.contains("@seelen/tb-keyboard-selector") {
            let languages=Handlers::get_system_languages();
            let lang=languages.iter().find(|l|l.keyboard_layouts.iter().any(|k|k.active)).or(languages.first());
            let (label,tooltip)=if let Some(lang)=lang {
                let code=lang.code.to_lowercase();let label=if code.starts_with("zh") {
                    if Handlers::get_ime_state().is_ok_and(|ime|!ime.open||!ime.native) {"英".into()} else {"中".into()}
                }else if code.starts_with("en") {"英".into()}else{lang.native_name.chars().filter(|c|!c.is_whitespace()&&*c!='('&&*c!=')').take(3).collect::<String>().to_uppercase()};
                let keyboard=lang.keyboard_layouts.iter().find(|k|k.active).or(lang.keyboard_layouts.first());
                (label,format!("{} - {}",lang.name,keyboard.map(|k|k.display_name.as_str()).unwrap_or("")))
            }else{("…".into(),String::new())};
            put("@seelen/tb-keyboard-selector",label.clone(),vec![text(label)],tooltip);
        }
        if ids.contains("@seelen/tb-media-popup") {
            let output=Handlers::get_media_devices().ok().and_then(|(_,devices)|devices.into_iter().find(|d|d.is_default_multimedia));
            let (volume,muted)=output.map(|d|(d.volume,d.muted)).unwrap_or((0.0,false));
            let glyph=if muted {"IoVolumeMuteOutline"}else if volume>=0.66 {"IoVolumeHighOutline"}else if volume>=0.33 {"IoVolumeMediumOutline"}else if volume!=0.0 {"IoVolumeLowOutline"}else {"IoVolumeOffOutline"};
            let tooltip=format!("{}: {:.0}%",t("toolbar.placeholder.volume"),volume*100.0);put("@seelen/tb-media-popup",tooltip.clone(),vec![icon(glyph)],tooltip);
        }
        if ids.contains("@seelen/tb-network-popup") {
            let online=Handlers::get_network_internet_connection().unwrap_or(false);
            let ip=Handlers::get_network_default_local_ip().unwrap_or_default();
            let adapters=serde_json::to_value(Handlers::get_network_adapters().unwrap_or_default()).unwrap_or_default();
            let wifi=adapters.as_array().into_iter().flatten().any(|a|a["ipv4"]==ip&&a["type"]=="IEEE80211");
            let tooltip=t(if online {"toolbar.placeholder.ethernet_connected"}else{"toolbar.placeholder.ethernet_disconnected"});
            put("@seelen/tb-network-popup",tooltip.clone(),vec![icon(if !online {"TbWorldCancel"}else if wifi {"FaWifi"}else{"FaComputer"})],tooltip);
        }
        if ids.contains("@default/power") {
            let batteries=serde_json::to_value(Handlers::get_batteries()).unwrap_or_default();
            let batteries=batteries.as_array().into_iter().flatten().filter(|b|(0.0..=100.0).contains(&number(b,"percentage"))).collect::<Vec<_>>();
            if batteries.is_empty() {put("@default/power","电源".into(),vec![icon("TbPlugConnected")],"未检测到电池".into());}
            else {let percent=(batteries.iter().map(|b|number(b,"percentage")).sum::<f64>()/batteries.len() as f64).round();
                let charging=batteries.iter().any(|b|b["state"]=="charging");
                let glyph=format!("MagicBattery{}{}",if charging {"Charge"}else{"Normal"},(percent/10.0).floor() as u32);
                let label=format!("{percent:.0}%");let mode=serde_json::to_value(Handlers::get_power_mode()).unwrap_or_default();let tooltip=format!("{} · {label}\nWindows 状态：{}\n点击查看电量与性能",if charging {"充电中"}else{"剩余"},mode.as_str().unwrap_or("Unknown"));put("@default/power",label.clone(),vec![icon(&glyph),text(" "),text(label.clone())],tooltip);}
        }
        put("@seelen/tb-power-menu","Power Menu".into(),vec![icon("IoPower")],"Power Menu".into());
        put("@seelen/tb-system-tray","System Tray".into(),vec![icon("IoIosArrowDropdown")],"System Tray".into());
        put("@seelen/tb-quick-settings",t("toolbar.placeholder.settings"),vec![icon("LuSettings2")],t("toolbar.placeholder.settings"));
        // Bluetooth templates can contain multiple connected device icons. Their
        // presentation is filled by the generated reference table, not emoji.
        if ids.contains("@seelen/tb-bluetooth-popup") {
            let devices=serde_json::to_value(Handlers::get_bluetooth_devices()).unwrap_or_default();
            let mut spans=Vec::new();
            let values=devices.as_array().map(Vec::as_slice).unwrap_or(&[]);let mut seen=HashSet::new();
            for device in values.iter().filter(|d|d["connected"]==true).filter(|d|!(d["isLowEnergy"]==true&&values.iter().any(|other|other["name"]==d["name"]&&other["id"]!=d["id"]&&other["isLowEnergy"]==false))).filter(|d|seen.insert(d["name"].as_str().unwrap_or("").to_owned())).take(32) {
                spans.push(icon(&bluetooth_icon(device)));spans.push(text(" "));
            }
            spans.push(icon(if spans.is_empty(){"TbBluetooth"}else{"TbBluetoothConnected"}));
            put("@seelen/tb-bluetooth-popup","Bluetooth".into(),spans,"Bluetooth".into());
        }
        if ids.contains("@seelen/tb-notifications") {
            let count=Handlers::get_notifications().len();let mut spans=Vec::new();
            let mode=serde_json::to_value(Handlers::get_notifications_mode().ok()).unwrap_or_default();
            if mode=="PriorityOnly"||mode=="AlarmsOnly" {spans.push(icon("TbZzz"));}
            spans.push(icon(if count>0 {"MdNotificationsActive"}else{"MdOutlineNotifications"}));
            let label=format!("{}: {count}",t("toolbar.placeholder.notifications"));put("@seelen/tb-notifications",label.clone(),spans,label);
            if let Some(row)=rows.get_mut("@seelen/tb-notifications") {row["badge"]=if count>0{json!(count.to_string())}else{Value::Null};}
        }
        Self { rows }
    }
}
fn bluetooth_icon(device:&Value)->String {
    static TABLES:std::sync::LazyLock<Value>=std::sync::LazyLock::new(||serde_json::from_str(include_str!("native-toolbar-bluetooth.json")).expect("generated reference dictionaries"));
    let table=&*TABLES;let appearance=&device["appearance"];
    let lookup=|group:&str,key:&str,fallback:&str|table[group][key].as_str().unwrap_or(fallback).to_owned();
    if let Some(category)=appearance["category"].as_str() {
        if category=="HumanInterfaceDevice" {return lookup("HUMAN_INTERFACE_DEVICE_ICONS",appearance["subcategory"].as_str().unwrap_or(""),"BsKeyboard");}
        return lookup("APPEARANCE_ICONS",category,"TbDeviceUnknown");
    }
    let class=&device["class"];let major=class["major"].as_str().unwrap_or("");let minor=class["minor"].as_str().unwrap_or("");
    let (group,fallback)=match major {
        "Computer"=>("COMPUTER_ICONS","IoDesktopOutline"),"Phone"=>("PHONE_ICONS","IoPhonePortraitOutline"),
        "LANNetworkAccessPoint"=>("LAN_NETWORK_ICONS","PiNetwork"),"AudioVideo"=>("AUDIO_VIDEO_ICONS","LuSpeaker"),
        "Peripheral"=>{if let Some(sub)=class["subminor"].as_str().filter(|s|*s!="Uncategorized"){return lookup("PERIPHERAL_SUBMINOR_ICONS",sub,"IoGameControllerOutline");}("PERIPHERAL_MINOR_ICONS","IoGameControllerOutline")},
        "Wearable"=>("WEARABLE_ICONS","IoWatchOutline"),"Toy"=>("TOY_ICONS","LuToyBrick"),"Health"=>("HEALTH_ICONS","MdOutlineMedication"),
        "Imaging"=>{let flags=class["minor"].as_array();let has=|s:&str|flags.is_some_and(|v|v.iter().any(|v|v==s));return if has("Display"){"BsDisplay"}else if has("Scanner")||has("Printer"){"IoPrintOutline"}else if has("Camera"){"IoCameraOutline"}else{"IoImagesOutline"}.into();},
        _=>return "TbDeviceUnknown".into(),
    };
    lookup(group,minor,fallback)
}

// Same precedence as weg/state/badge.svelte.ts: taskbar value, overlay image,
// then unread notifications. Counts are capped at 99 by the reference.
pub(super) fn dock_badge(row:&mut Value, windows:&[Value], notifications:&Value) {
    if let Some(value)=windows.iter().find_map(|w|w.get("badgeValue").filter(|v|!v.is_null())) {
        if let Some(count)=value.as_u64().filter(|v|*v>0) {row["badge"]=json!({"kind":"count","value":count.min(99).to_string()});return;}
        let glyph=match value.as_str().unwrap_or("") {"Activity"=>"TbRefresh","Alarm"=>"IoAlarm","Alert"|"Attention"=>"IoAlert","Error"=>"IoClose","NewMessage"=>"IoMail","Paused"=>"IoPause","Playing"=>"IoPlay","Unavailable"=>"IoBan","Available"=>"green","Away"=>"orange","Busy"=>"red",_=>""};
        if !glyph.is_empty(){row["badge"]=json!({"kind":if ["green","orange","red"].contains(&glyph){"color"}else{"icon"},"value":glyph});return;}
    }
    if let Some(window)=windows.iter().filter(|w|w["badgeUpdatedAt"].as_i64().unwrap_or(0)>0&&w["badgeIconPath"].is_string()).max_by_key(|w|w["badgeUpdatedAt"].as_i64().unwrap_or(0)) {
        row["badge"]=json!({"kind":"image","value":window["badgeIconPath"],"version":window["badgeUpdatedAt"]});return;
    }
    let count=notifications.as_array().into_iter().flatten().filter(|v|!row["umid"].is_null()&&v["appUmid"]==row["umid"]).count();
    if count>0 {row["badge"]=json!({"kind":"count","value":count.min(99).to_string()});}
}

// Native equivalents of the seven official canvas Dock plugins. They share the
// toolbar's OS managers/events; arbitrary third-party render code stays compatible.
pub(super) fn dock_popup(plugin:&str)->Option<&'static str>{Some(match plugin{
    "@seelen/dock-calendar-popup"=>"@seelen/calendar-popup",
    "@seelen/dock-media-popup"=>"@seelen/media-popup",
    "@seelen/dock-bluetooth-popup"=>"@seelen/bluetooth-popup",
    "@seelen/dock-keyboard-selector"=>"@seelen/keyboard-selector",
    "@seelen/dock-notifications"=>"@seelen/notifications",
    "@seelen/dock-network-popup"=>"@seelen/network-popup",
    "@seelen/dock-system-tray"=>"@seelen/system-tray",_=>return None,
})}
pub(super) fn dock_plugin(row:&mut Value,settings:&Value,notifications:&Value,catalog:&Value)->crate::error::Result<()>{
    if row["type"]!="Plugin"{return Ok(());}
    let plugin=row["plugin"].as_str().unwrap_or("").to_owned();
    if dock_popup(&plugin).is_none(){return Ok(());}
    if let Some(label)=catalog.as_array().into_iter().flatten().find(|m|m["id"]==plugin).and_then(|m|m["label"].as_str()) { row["label"]=json!(label); }
    row["tooltip"]=row["label"].clone();
    match plugin.as_str(){
        "@seelen/dock-calendar-popup"=>{row["dockClock"]=json!(true);row["tooltip"]=Value::Null;},
        "@seelen/dock-media-popup"=>{
            let output=Handlers::get_media_devices().ok().and_then(|(_,devices)|devices.into_iter().find(|d|d.is_default_multimedia));
            let (volume,muted)=output.map(|d|(d.volume,d.muted)).unwrap_or((0.0,false));
            row["dockVolume"]=json!(volume);row["dockMuted"]=json!(muted);
            row["tooltip"]=json!(format!("{}: {:.0}%",translated(settings,"toolbar.placeholder.volume"),volume*100.0));
        },
        "@seelen/dock-bluetooth-popup"=>{row["dockConnected"]=json!(Handlers::get_bluetooth_devices().iter().any(|d|d.connected));},
        "@seelen/dock-keyboard-selector"=>{
            let languages=Handlers::get_system_languages();
            if let Some(lang)=languages.iter().find(|l|l.keyboard_layouts.iter().any(|k|k.active)).or(languages.first()){
                let keyboard=lang.keyboard_layouts.iter().find(|k|k.active).or(lang.keyboard_layouts.first());
                row["dockLanguage"]=json!(lang.code.split('-').next().unwrap_or("?").to_uppercase());
                row["dockKeyboard"]=json!(if lang.keyboard_layouts.len()>1 {keyboard.map(|k|k.display_name.chars().take(8).collect::<String>()).unwrap_or_default()}else{String::new()});
                row["tooltip"]=json!(format!("{} - {}",lang.name,keyboard.map(|k|k.display_name.as_str()).unwrap_or("")));
            }
        },
        "@seelen/dock-notifications"=>{
            let count=notifications.as_array().map_or(0,Vec::len);
            let mode=serde_json::to_value(Handlers::get_notifications_mode().ok())?;
            row["dockDnd"]=json!(mode=="PriorityOnly"||mode=="AlarmsOnly");
            row["tooltip"]=json!(format!("{}: {count}",translated(settings,"toolbar.placeholder.notifications")));
            if count>0{row["badge"]=json!({"kind":"count","value":count.min(99).to_string()});}
        },
        "@seelen/dock-network-popup"=>{
            let online=Handlers::get_network_internet_connection().unwrap_or(false);
            let ip=Handlers::get_network_default_local_ip().unwrap_or_default();
            let adapters=serde_json::to_value(Handlers::get_network_adapters().unwrap_or_default())?;
            let wifi=adapters.as_array().into_iter().flatten().any(|a|a["ipv4"]==ip&&a["type"]=="IEEE80211");
            row["dockOnline"]=json!(online);row["dockWifi"]=json!(wifi);
            row["tooltip"]=json!(if !online{translated(settings,"toolbar.placeholder.ethernet_disconnected")}else if wifi{"Wi-Fi".into()}else{translated(settings,"toolbar.placeholder.ethernet_connected")});
        },_=>{}
    }
    Ok(())
}
