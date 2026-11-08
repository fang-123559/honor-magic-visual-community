//! Event-driven native toolbar and Dock adapter. Unknown executable plugins retain
//! their existing component WebView; we do not duplicate it with a native overlay.
use super::desktop_drag::BridgeSession;
use crate::{
    error::{Result, ResultLogExt},
    get_tokio_handle,
    server::events::{Connection, EVENTS_HUB},
    state::application::{FULL_STATE, WEG_ITEMS_MANAGER},
    tauri_handlers::Handlers,
    windows_api::{WindowsApi, window::Window},
};
use seelen_core::{
    handlers::SeelenEvent,
    state::{WegItem, WegItemData, WidgetPerm, WidgetTriggerPayload},
};
use serde_json::{Value, json};
use std::{
    collections::{HashMap, HashSet},
    sync::atomic::{AtomicBool, Ordering},
    time::Duration,
};
#[path = "native_shell_config.rs"]
mod configuration;
#[path = "native_toolbar.rs"]
mod toolbar_presentation;
const PROTOCOL: &str = "Magic.NativePopups/1";
static STARTED: AtomicBool = AtomicBool::new(false);
static FAILED: AtomicBool = AtomicBool::new(false);
const TB: [&str; 18] = [
    "@default/memory-usage",
    "@default/cpu-usage",
    "@default/gpu-usage",
    "@default/disk-usage",
    "@default/network-usage",
    "@default/focused-app",
    "@default/focused-app-title",
    "@seelen/tb-user-menu",
    "@seelen/tb-power-menu",
    "@seelen/tb-calendar-popup",
    "@seelen/tb-system-tray",
    "@seelen/tb-bluetooth-popup",
    "@seelen/tb-keyboard-selector",
    "@default/power",
    "@seelen/tb-network-popup",
    "@seelen/tb-quick-settings",
    "@seelen/tb-notifications",
    "@seelen/tb-media-popup",
];
const DOCK: [&str; 11] = [
    "@default/weg-power",
    "@default/weg-start-menu",
    "@default/weg-show-desktop",
    "@default/weg-trash-bin",
    "@seelen/dock-system-tray",
    "@seelen/dock-calendar-popup",
    "@seelen/dock-media-popup",
    "@seelen/dock-bluetooth-popup",
    "@seelen/dock-keyboard-selector",
    "@seelen/dock-notifications",
    "@seelen/dock-network-popup",
];
pub(super) fn selection() -> HashSet<String> {
    if FAILED.load(Ordering::Acquire) {
        return HashSet::new();
    }
    let Ok(root) = BridgeSession::project_root() else {
        return HashSet::new();
    };
    let Ok(bytes) = std::fs::read(root.join("app.current.json")) else {
        return HashSet::new();
    };
    if bytes.len() > 1024 * 1024 {
        return HashSet::new();
    }
    let Ok(value) = serde_json::from_slice::<Value>(&bytes) else {
        return HashSet::new();
    };
    if value["nativeShell"] != "composition" || value["desktopRenderer"] != "composition" {
        return HashSet::new();
    }
    let mut result = HashSet::new();
    let toolbar = serde_json::to_value(Handlers::state_get_toolbar_items()).unwrap_or_default();
    let dock = serde_json::to_value(Handlers::state_get_weg_items()).unwrap_or_default();
    if ["left", "center", "right"].iter().all(|s| {
        toolbar[*s]
            .as_array()
            .into_iter()
            .flatten()
            .all(configuration::native_toolbar_item)
    }) {
        result.insert("@seelen/fancy-toolbar".into());
    }
    if ["left", "center", "right"].iter().all(|s| {
        dock[*s].as_array().into_iter().flatten().all(|item| {
            item["type"] != "Plugin" || item["plugin"].as_str().is_some_and(|id| DOCK.contains(&id))
        })
    }) {
        result.insert("@seelen/weg".into());
    }
    if !result.is_empty() {
        result.insert("@magic-community/popup-lifecycle".into());
        result.insert("@seelen/flyouts".into());
    }
    result.insert("@magic-community/desktop".into());
    result
}
pub(super) fn start() {
    // Early CLI resource-unload requests can reconcile before pre_start has
    // loaded the signed bundle. Never paint apparently usable bars in that state.
    if ["@seelen/fancy-toolbar", "@seelen/weg"].iter().any(|id|
        !crate::resources::RESOURCES.widgets.contains_sync(&seelen_core::resource::WidgetId::from(*id))) {
        return;
    }
    if !selection()
        .iter()
        .any(|id| id == "@seelen/fancy-toolbar" || id == "@seelen/weg")
        || STARTED.swap(true, Ordering::AcqRel)
    {
        return;
    }
    get_tokio_handle().spawn(async {
        // Bounded startup discovery only. Once attached, all work is event or user driven.
        for _ in 0..120 {
            if let Ok(session) = BridgeSession::current() {
                // BridgeSession authenticates the running renderer. The /1 protocol
                // handshake below determines support; do not disable native bars on
                // the next application version merely because its number changed.
                if session.native_renderer_pid != 0 {
                    if let Err(error) = run(&session).await {
                        log::error!("Native shell ended: {error}");
                        if let Ok(client) = reqwest::Client::builder().no_proxy().build() {
                            let _ = post(
                                &client,
                                &session,
                                "/composition-shell-update",
                                &json!({"data":{"monitors":[]}}),
                                false,
                            )
                            .await;
                        }
                        fallback();
                    }
                    return;
                }
            }
            tokio::time::sleep(Duration::from_millis(500)).await;
        }
        log::error!("Native shell startup deadline; restoring compatible bars");
        fallback();
    });
}
fn fallback() {
    FAILED.store(true, Ordering::Release);
    // Native ownership may already have removed the compatibility pods.
    // Recreate and start them through the existing serialized worker.
    super::manager::WIDGET_MANAGER.reconcile().log_error();
}
static ITEMS_CHANGED: tokio::sync::Notify = tokio::sync::Notify::const_new();
pub(super) fn items_changed() {
    // One retained permit coalesces writes, including changes before run starts.
    ITEMS_CHANGED.notify_one();
}
struct Subscription {
    connection: std::sync::Arc<Connection>,
    resources: Vec<&'static str>,
}
impl Subscription {
    fn new() -> Self {
        let connection = EVENTS_HUB.connect("magic-native-shell");
        for event in [
            SeelenEvent::UserAppWindowsChanged,
            SeelenEvent::GlobalFocusChanged,
            SeelenEvent::WegItemsChanged,
            SeelenEvent::StateSettingsChanged,
            SeelenEvent::SystemTrayChanged,
            SeelenEvent::StatePluginsChanged,
            SeelenEvent::StateIconPacksChanged,
            SeelenEvent::SystemMonitorsChanged,
            SeelenEvent::PowerStatus,
            SeelenEvent::PowerMode,
            SeelenEvent::MediaDevices,
            SeelenEvent::MediaSessions,
            SeelenEvent::Notifications,
            SeelenEvent::NotificationsModeChanged,
            SeelenEvent::VirtualDesktopsChanged,
            SeelenEvent::ShortcutsPaused,
            SeelenEvent::PendingUacRequestsChanged,
            SeelenEvent::DarkModeChanged,
            SeelenEvent::SystemLanguagesChanged,
            SeelenEvent::SystemImeStateChanged,
            SeelenEvent::UserChanged,
            SeelenEvent::BatteriesStatus,
            SeelenEvent::BluetoothDevicesChanged,
            SeelenEvent::NetworkInternetConnection,
            SeelenEvent::NetworkAdapters,
            SeelenEvent::NetworkDefaultLocalIp,
            SeelenEvent::TrashBinChanged,
        ] {
            EVENTS_HUB.subscribe(&connection, event);
        }
        let mut value=Self { connection, resources:Vec::new() };
        value.sync_resources();
        value
    }
    fn sync_resources(&mut self) {
        let state=serde_json::to_value(Handlers::state_get_toolbar_items()).unwrap_or_default();
        let selected=selection().contains("@seelen/fancy-toolbar") && FULL_STATE.load().settings.by_widget.fancy_toolbar.enabled;
        for (id,event) in toolbar_presentation::RESOURCES {
            let needed=selected && ["left","center","right"].iter().any(|s|state[*s].as_array().is_some_and(|v|v.iter().any(|v|v==id)));
            let subscribed=self.resources.contains(&event);
            if needed&&!subscribed {EVENTS_HUB.subscribe(&self.connection,event);self.resources.push(event);}
            else if !needed&&subscribed {EVENTS_HUB.unsubscribe(&self.connection,event);self.resources.retain(|v|*v!=event);}
        }
    }
}
impl Drop for Subscription {
    fn drop(&mut self) {
        EVENTS_HUB.disconnect("magic-native-shell", &self.connection);
    }
}
async fn post(
    client: &reqwest::Client,
    session: &BridgeSession,
    path: &str,
    body: &Value,
    wait: bool,
) -> Result<Value> {
    let mut request = client
        .post(format!("{}{path}", session.endpoint))
        .bearer_auth(&session.token)
        .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
        .json(body);
    if !wait {
        request = request.timeout(Duration::from_secs(4));
    }
    let response = request.send().await?.error_for_status()?;
    if response.content_length().is_some_and(|v| v > 65536) {
        return Err("Native shell response too large".into());
    }
    let bytes = response.bytes().await?;
    if bytes.len() > 65536 {
        return Err("Native shell response too large".into());
    }
    Ok(serde_json::from_slice(&bytes)?)
}
async fn run(session: &BridgeSession) -> Result<()> {
    let client = reqwest::Client::builder()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(Duration::from_millis(500))
        .build()?;
    let mut sub = Subscription::new();
    let mut model = ShellModel::default();
    let data = model.snapshot()?;
    if post(
        &client,
        &session,
        "/composition-shell-update",
        &json!({"data":data}),
        false,
    )
    .await?["accepted"]
        != true
    {
        return Err("Native shell adapter not ready".into());
    }
    // One pending state refresh, not an awaited delay in the command branch.
    // Keep the outstanding command read alive throughout the debounce window.
    let mut refresh_pending = false;
    let mut refresh_at = tokio::time::Instant::now();
    loop {
        let empty = json!({});
        let next = post(&client, &session, "/composition-shell-next", &empty, true);
        tokio::pin!(next);
        let refresh = tokio::time::sleep_until(refresh_at);
        tokio::pin!(refresh);
        let command = loop {
            tokio::select! {
                biased;
                command=&mut next=>break command?,
                _=&mut refresh, if refresh_pending=>{
                    refresh_pending=false;
                    sub.connection.drain();sub.sync_resources();
                    let started=std::time::Instant::now();let data=model.snapshot()?;
                    let snapshot_ms=started.elapsed().as_millis();
                    post(&client,&session,"/composition-shell-update",&json!({"data":data}),false).await?;
                    log::debug!("Native shell refresh: snapshot={snapshot_ms}ms total={}ms",started.elapsed().as_millis());
                }
                _=async { tokio::select! { _=sub.connection.notified()=>{}, _=ITEMS_CHANGED.notified()=>{} } }=>{
                    if !refresh_pending {
                        refresh_pending=true;
                        refresh_at=tokio::time::Instant::now()+Duration::from_millis(100);
                        refresh.as_mut().reset(refresh_at);
                    }
                }
            }
        };
        let started=std::time::Instant::now();
        let result=model.execute(&command, &session).await;
        // Trace discrete activation/focus only; no per-frame or scroll logging.
        if matches!(command["verb"].as_str(),Some("activate"|"focus")) {
            let now=unsafe {windows::Win32::System::SystemInformation::GetTickCount64()};
            if let Some(sent)=command["sentAtTick"].as_u64().filter(|sent|*sent<=now) {
                log::info!("Native shell command: verb={} queue={}ms execute={}ms ok={}",command["verb"],now.saturating_sub(sent).saturating_sub(started.elapsed().as_millis() as u64),started.elapsed().as_millis(),result.is_ok());
            }
        }
        if let Some(token)=command["orderToken"].as_str().filter(|s|s.len()<=64) {
            if model.order_receipts.len()>=32 {model.order_receipts.pop_front();}
            model.order_receipts.push_back(json!({"token":token,"ok":result.is_ok(),"error":result.as_ref().err().map(ToString::to_string)}));
        }
        result.log_error();
        // Let the next click be read before collecting/pushing state. Receipts
        // stay in the model and are included in the single coalesced refresh.
        if !refresh_pending {
            refresh_pending=true;
            refresh_at=tokio::time::Instant::now()+Duration::from_millis(100);
        }
    }
}

#[derive(Default)]
struct ShellModel {
    commands: HashMap<String, Value>,
    order_receipts: std::collections::VecDeque<Value>,
}
impl ShellModel {
    fn snapshot(&mut self) -> Result<Value> {
        self.commands.clear();
        self.commands.insert("bar:toolbar".into(), json!({"op":"bar-config","dock":false}));
        self.commands.insert("bar:dock".into(), json!({"op":"bar-config","dock":true}));
        let selected = selection();
        let state = FULL_STATE.load();
        let settings = serde_json::to_value(&state.settings)?;
        let windows = Handlers::get_user_app_windows();
        let raw_windows = windows.len();
        let dark = Handlers::get_system_dark_mode().unwrap_or(false);
        let toolbar = serde_json::to_value(Handlers::state_get_toolbar_items())?;
        let dock = serde_json::to_value(Handlers::state_get_weg_items())?;
        let battery = Handlers::get_power_status();
        let presentation=toolbar_presentation::Presentation::new(if selected.contains("@seelen/fancy-toolbar")&&state.settings.by_widget.fancy_toolbar.enabled {&toolbar}else{&Value::Null},&settings);
        let language=String::new();
        let mut tb = serde_json::Map::new();
        let mut dg = serde_json::Map::new();
        let mut grouped = HashSet::new();
        let tray_icons=Handlers::get_system_tray_icons().into_iter().filter(|icon|icon.is_visible).collect::<Vec<_>>();
        let media=Handlers::get_media_sessions().into_iter().find(|p|p.default);
        let dock_modules=configuration::catalog(true);
        let notifications=serde_json::to_value(Handlers::get_notifications())?;
        for section in ["left", "center", "right"] {
            let mut items = Vec::new();
            for entry in toolbar[section].as_array().into_iter().flatten().take(96) {
                let Some(id)=entry.as_str() else {
                    if configuration::native_toolbar_item(entry) {
                        let key=format!("toolbar:{}",entry["id"].as_str().unwrap_or(""));
                        self.commands.insert(key.clone(),json!({"op":"toolbar-item","itemId":entry["id"]}));
                        let template=entry["template"].as_str().unwrap_or("");let label=if template.contains("focusedApp"){if Handlers::get_focused_app().title.is_empty(){""}else{"-"}}else if template==r#"return " ""#{" "}else{"|"};
                        items.push(json!({"id":key,"label":label,"text":true,"passive":true,"inline":true}));
                    }
                    continue;
                };
                if id=="@seelen/tb-system-tray" {
                    for icon in tray_icons.iter().filter(|icon|icon.is_promoted).take(64) {
                        if icon.guid.is_some_and(|g|["7820ae73-23e3-4229-82c1-e41cb67d5b9c","7820ae74-23e3-4229-82c1-e41cb67d5b9c","7820ae75-23e3-4229-82c1-e41cb67d5b9c"].contains(&g.to_string().as_str())) {continue;}
                        let key=format!("tray:{}",icon.registry_key);
                        self.commands.insert(key.clone(),json!({"op":"tray","id":icon.registry_key}));
                        let image=icon.icon_path.as_ref().unwrap_or(&icon.executable_path);
                        items.push(json!({"id":key,"key":key,"label":icon.tooltip,"type":"Tray","ownerHwnd":icon.window_handle,"module":"toolbar:@seelen/tb-system-tray","icon":image,"imageVersion":icon.icon_image_hash,"glyph":icon.is_glyph}));
                    }
                }
                let (label, widget) = match id {
                    "@seelen/tb-user-menu" => ("◉".to_owned(), "@seelen/user-menu"),
                    "@seelen/tb-power-menu" => ("Power Menu".into(), "@seelen/power-menu"),
                    "@seelen/tb-calendar-popup" => ("clock".into(), "@seelen/calendar-popup"),
                    "@seelen/tb-system-tray" => ("⌄".into(), "@seelen/system-tray"),
                    "@seelen/tb-bluetooth-popup" => ("ᛒ".into(), "@seelen/bluetooth-popup"),
                    "@seelen/tb-keyboard-selector" => {
                        (language.clone(), "@seelen/keyboard-selector")
                    }
                    "@default/power" => (
                        if battery.battery_life_percent <= 100 {
                            format!("▰ {}%", battery.battery_life_percent)
                        } else {
                            "▰ —".into()
                        },
                        "@seelen/quick-settings",
                    ),
                    "@seelen/tb-network-popup" => ("⌁".into(), "@seelen/network-popup"),
                    "@seelen/tb-quick-settings" => ("☷".into(), "@seelen/quick-settings"),
                    "@seelen/tb-notifications" => ("♧".into(), "@seelen/notifications"),
                    "@default/focused-app" => (Handlers::get_focused_app().name, ""),
                    "@default/focused-app-title" => (Handlers::get_focused_app().title, ""),
                    "@seelen/tb-media-popup" => ("音量".into(), "@seelen/media-popup"),
                    _ => (String::new(), ""),
                };
                let key = format!("toolbar:{id}");
                self.commands.insert(
                    key.clone(),
                    json!({"op":"popup","widget":widget,"power":id=="@default/power"}),
                );
                let mut row=json!({"id":key,"label":label,"clock":label=="clock","text":id.starts_with("@default/focused-app"),"passive":widget.is_empty(),"batteryPercent":battery.battery_life_percent,"charging":battery.battery_flag!=255&&battery.battery_flag&8!=0,"batteryLow":battery.ac_line_status==0&&battery.battery_life_percent<=20});
                if let Some(fields)=presentation.rows.get(id).and_then(Value::as_object) {
                    for (key,value) in fields {row[key]=value.clone();}
                }
                if let Some(image)=row["content"].as_array().and_then(|v|v.iter().find(|s|s["kind"]=="image")).and_then(|s|s["value"].as_str()).map(str::to_owned) {
                    row["icon"]=json!(image);row["key"]=json!(key);row["path"]=if id=="@default/focused-app" {json!(Handlers::get_focused_app().exe)}else{json!("")};
                    row["type"]=json!(if id=="@default/focused-app" {"AppOrFile"}else{"ToolbarImage"});
                    row["umid"]=if id=="@default/focused-app" {json!(Handlers::get_focused_app().umid)}else{Value::Null};
                    row["imageVersion"]=json!(std::fs::metadata(image).ok().and_then(|m|m.modified().ok()).and_then(|m|m.duration_since(std::time::UNIX_EPOCH).ok()).map(|m|m.as_millis().to_string()));
                }
                items.push(row);
                if id=="@seelen/tb-system-tray" && section=="left" {
                    let start=items.iter().rposition(|r|r["module"]!="toolbar:@seelen/tb-system-tray" && r["id"]!="toolbar:@seelen/tb-system-tray").map(|i|i+1).unwrap_or(0);
                    items[start..].reverse();
                }
            }
            tb.insert(section.into(), json!(items));
            let mut items = Vec::new();
            for item in dock[section].as_array().into_iter().flatten().take(96) {
                let key = item["id"].as_str().unwrap_or("").to_owned();
                let mut row = item.clone();
                row["key"] = json!(key);
                let mut app_windows = Vec::new();
                if item["type"] == "AppOrFile" {
                    for window in &windows {
                        let same=if let Some(umid)=window.umid.as_ref(){item["umid"].as_str()==Some(umid.as_str())}
                            else {window.process.path.as_ref().is_some_and(|path|{let path=path.to_string_lossy();item["path"].as_str().is_some_and(|p|p.eq_ignore_ascii_case(&path))||item["relaunch"]["command"].as_str().is_some_and(|p|p.eq_ignore_ascii_case(&path))})};
                        if same {
                            grouped.insert(window.hwnd);
                            app_windows.push(serde_json::to_value(window)?);
                        }
                    }
                    row["windows"] = json!(app_windows);
                    row["label"] = item["displayName"].clone();
                    let icon_entry = configuration::app_icon_entry(&item, dark);
                    row["icon"] = icon_entry["resolvedImage"].as_str().map(|p|json!(p)).unwrap_or_else(||item["relaunch"]["icon"].as_str().map(|v|json!(v)).unwrap_or_else(||item["path"].clone()));row["squareIcon"]=icon_entry["icon"]["isAproximatelySquare"].clone();
                } else if item["type"]=="Media" {
                    row["label"]=json!(media.as_ref().map(|p|p.title.as_str()).unwrap_or("媒体"));row["playing"]=json!(media.as_ref().is_some_and(|p|p.playing));row["artist"]=json!(media.as_ref().map(|p|p.author.as_str()).unwrap_or(""));row["icon"]=json!(media.as_ref().and_then(|p|p.thumbnail.as_ref()));row["hasPlayer"]=json!(media.is_some());if media.is_none(){row["label"]=json!(toolbar_presentation::translated(&settings,"dock.media.not_playing"));}
                } else if item["type"] == "Plugin" {
                    row["label"]=json!(toolbar_presentation::translated(&settings,match item["plugin"].as_str().unwrap_or("") {"@default/weg-start-menu"=>"dock.start.label","@default/weg-show-desktop"=>"dock.show_desktop.label","@default/weg-trash-bin"=>"dock.trash_bin.label",_=>"toolbar.placeholder.settings"}));

                }
                if item["type"]=="Plugin"&&item["plugin"]=="@default/weg-power" {
                    let batteries=Handlers::get_batteries();row["batteries"]=json!(batteries);row["acLineStatus"]=json!(battery.ac_line_status);
                    row["tooltip"]=json!(if batteries.is_empty(){"Plugged in".into()}else{batteries.iter().enumerate().map(|(i,b)|format!("{}{:.0}%{}{}",if batteries.len()>1{format!("{}. ",i+1)}else{String::new()},b.percentage.round(),if b.state=="charging"{" - Charging"}else{""},if b.smart_charging{" - Smart charge"}else{""})).collect::<Vec<_>>().join("\n")});row["label"]=json!("Power");
                }
                if item["type"]=="Plugin"&&item["plugin"]=="@seelen/dock-system-tray" {row["label"]=json!("System Tray");row["tooltip"]=json!("System Tray");}
                if item["type"]=="Plugin"&&item["plugin"]=="@default/weg-trash-bin" {row["trashCount"]=json!(Handlers::get_trash_bin_info().item_count);row["tooltip"]=json!(format!("{}\n{}",toolbar_presentation::translated(&settings,"dock.trash_bin.label"),toolbar_presentation::translated(&settings,"dock.trash_bin.item_count").replace("{{0}}",&row["trashCount"].to_string())));}
                toolbar_presentation::dock_plugin(&mut row,&settings,&notifications,&dock_modules)?;
                toolbar_presentation::dock_badge(&mut row,&app_windows,&notifications);
                self.commands.insert(
                    key.clone(),
                    json!({"op":"dock","item":item,"section":section,"windows":app_windows}),
                );
                items.push(row);
            }
            dg.insert(section.into(), json!(items));
        }
        if let Some(items) = dg.get_mut("center").and_then(Value::as_array_mut) {
            for window in windows
                .iter()
                .filter(|w| !grouped.contains(&w.hwnd))
                .take(96)
            {
                let key = format!("running:{}", window.hwnd);
                let item = json!({"type":"AppOrFile","id":key,"key":key,"label":window.app_name,"displayName":window.app_name,"path":window.process.path,"icon":window.process.path.as_ref().map(|p|p.to_string_lossy().into_owned()).unwrap_or_default(),"umid":window.umid,"pinned":false,"windows":[window]});
                self.commands.insert(
                    key,
                    json!({"op":"dock","item":item,"section":"center","windows":[window]}),
                );
                let mut row=item;
                toolbar_presentation::dock_badge(&mut row,&[serde_json::to_value(window)?],&notifications);
                let entry=configuration::app_icon_entry(&row,dark);
                if let Some(image)=entry["resolvedImage"].as_str(){row["icon"]=json!(image);}
                items.push(row);
            }
        }
        for request in Handlers::get_pending_uac_requests().into_iter().take(8) {
            let key = format!("uac:{}", request.id);
            self.commands
                .insert(key.clone(), json!({"op":"uac","id":request.id}));
            if let Some(items) = dg.get_mut("right").and_then(Value::as_array_mut) {
                items.push(
                    json!({"type":"Uac","key":key,"label":"授权请求","detail":request.title}),
                );
            }
        }
        let monitors = Handlers::get_connected_monitors()
            .into_iter()
            .take(16)
            .map(|m| {
                let tb = selected.contains("@seelen/fancy-toolbar")
                    && state.is_widget_enable_on_monitor(&"@seelen/fancy-toolbar".into(), &m.id);
                let dock = selected.contains("@seelen/weg")
                    && state.is_widget_enable_on_monitor(&"@seelen/weg".into(), &m.id);
                json!({"id":m.id,"rect":m.rect,"scale":m.scale_factor,"primary":m.is_primary,"toolbar":tb,"dock":dock,"brightness":m.brightness})
            })
            .collect::<Vec<_>>();
        let player = Handlers::get_media_sessions().into_iter().find(|p| p.default).map(|p| json!({"key":format!("{}:{}",p.umid,p.title),"title":p.title.chars().take(128).collect::<String>(),"detail":p.author.chars().take(256).collect::<String>()}));
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64;
        let notification = if serde_json::to_value(Handlers::get_notifications_mode().ok())?
            == "All"
        {
            Handlers::get_notifications().into_iter().filter(|n| (0..10000).contains(&(now-n.date))).max_by_key(|n| n.date).map(|n| json!({"key":n.id.to_string(),"title":n.app_name.chars().take(128).collect::<String>(),"detail":super::native_panels::toast_text(&serde_json::to_value(n.content.visual).unwrap_or_default(),0).chars().take(256).collect::<String>()}))
        } else {
            None
        };
        let virtuals = serde_json::to_value(Handlers::get_virtual_desktops())?;
        let mut workspaces = serde_json::Map::new();
        if let Some(list) = virtuals["monitors"].as_object() {
            for (monitor, data) in list {
                let active = &data["active_workspace"];
                let mut title = "工作区".to_owned();
                if let Some(rows) = data["workspaces"].as_array() {
                    for row in rows {
                        if let Some(columns) = row.as_array() {
                            for item in columns {
                                if item["id"] == *active {
                                    title = item["name"]
                                        .as_str()
                                        .unwrap_or("工作区")
                                        .chars()
                                        .take(128)
                                        .collect();
                                }
                            }
                        }
                    }
                }
                workspaces.insert(monitor.clone(), json!({"key":active,"title":title}));
            }
        }
        Ok(
            json!({"systemBackend":if slu_ipc::community::enabled(){let m=slu_ipc::community::manifest()?;json!({"protocol":slu_ipc::community::PROTOCOL,"instanceId":m.instance_id,"capabilities":slu_ipc::community::CORE_CAPABILITIES})}else{Value::Null},"orderReceipts":self.order_receipts,"notificationAccess":crate::modules::notifications::infrastructure::listener_ready(),"powerMode":match Handlers::get_power_mode(){seelen_core::system_state::PowerMode::BatterySaver=>"节能",seelen_core::system_state::PowerMode::BetterBattery=>"更长续航",seelen_core::system_state::PowerMode::Balanced=>"平衡",seelen_core::system_state::PowerMode::HighPerformance=>"高性能",seelen_core::system_state::PowerMode::MaxPerformance=>"最佳性能",seelen_core::system_state::PowerMode::GameMode=>"游戏",seelen_core::system_state::PowerMode::MixedReality=>"混合现实",_=>"暂不可用"},"devTools":settings["devTools"],"dateFormat":settings["dateFormat"],"language":settings["language"],"toolbarModules":configuration::catalog(false),"dockModules":dock_modules,"player":player,"notification":notification,"workspaceSwitching":virtuals["switching"],"appWindows":windows,"workspaces":workspaces,"shortcutsPaused":crate::cli::shortcuts::SHORTCUTS_PAUSED.load(Ordering::Acquire),"monitors":monitors,"toolbar":tb,"dock":dg,"toolbarSettings":settings["byWidget"]["@seelen/fancy-toolbar"],"dockSettings":settings["byWidget"]["@seelen/weg"],"hostPid":std::process::id(),"windowCount":raw_windows,"webviews":Handlers::debug_get_widgets_statuses(),"focused":Handlers::get_focused_app(),"windowMoving":Window::from(Handlers::get_focused_app().hwnd).is_dragging(),"volume":Handlers::get_media_devices().ok().and_then(|(_,out)|out.into_iter().find(|d|d.is_default_multimedia)).map(|d|d.volume),"flyouts":settings["byWidget"]["@seelen/flyouts"],"dark":dark,"reorderToolbar":!toolbar["isReorderDisabled"].as_bool().unwrap_or(false),"reorderDock":!dock["isReorderDisabled"].as_bool().unwrap_or(false)}),
        )
    }
    async fn execute(&mut self, value: &Value, session: &BridgeSession) -> Result<()> {
        let id = value["id"].as_str().ok_or("Missing shell item")?;
        if id == "__measure-popup" || id == "__open-panel" {
            let widget = value["widget"].as_str().ok_or("Missing measured widget")?;
            if !super::native_popups::selection().contains(widget) {
                return Err("Unselected measured widget".into());
            }
            let mut payload = WidgetTriggerPayload::new(widget.into());
            if id == "__measure-popup" {
                payload.add_custom_arg("__nativeMeasure", true);
            }
            super::trigger_widget_inner(payload, None)?;
            return Ok(());
        }
        let command = self.commands.get(id).ok_or("Shell item expired")?.clone();
        let verb = value["verb"].as_str().unwrap_or("activate");
        if command["op"]=="bar-config" {
            configuration::execute(command["dock"]==true,None,value,&self.commands)?; return Ok(());
        }
        if command["op"]=="popup" && id=="toolbar:@seelen/tb-media-popup" && matches!(verb,"volume-up"|"volume-down") {
            if let Some(device)=Handlers::get_media_devices()?.1.into_iter().find(|d|d.is_default_multimedia) {
                Handlers::set_volume_level(device.id,None,(device.volume+if verb=="volume-up"{0.02}else{-0.02}).clamp(0.0,1.0))?;
            }
            return Ok(());
        }
        if command["op"]=="toolbar-item" || (command["op"]=="popup" && verb!="activate") {
            configuration::execute(false,Some(id.trim_start_matches("toolbar:")),value,&self.commands)?; return Ok(());
        }
        if command["op"]=="dock" && command["item"]["plugin"]=="@seelen/dock-media-popup" && matches!(verb,"volume-up"|"volume-down") {
            if let Some(device)=Handlers::get_media_devices()?.1.into_iter().find(|d|d.is_default_multimedia) {
                Handlers::set_volume_level(device.id,None,(device.volume+if verb=="volume-up"{0.02}else{-0.02}).clamp(0.0,1.0))?;
            }
            return Ok(());
        }
        if command["op"]=="dock" && matches!(verb,"remove"|"move-left"|"move-right"|"section-left"|"section-center"|"section-right"|"reorder") {
            configuration::execute(true,Some(id),value,&self.commands)?;return Ok(());
        }
        // Popup triggers use their own widget route. Only Dock commands manage
        // application windows; an unrelated permission must not gate the bar.
        if command["op"]=="dock" {
            let item=&command["item"];let app=item["type"]=="AppOrFile";
            if matches!(verb,"focus"|"close"|"close-one") || (verb=="activate"&&app&&command["windows"].as_array().is_some_and(|w|!w.is_empty())) || (verb=="activate"&&item["plugin"]=="@default/weg-show-desktop") {
                super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/weg".into(),WidgetPerm::ManageAppWindows)?;
            } else if app && (matches!(verb,"launch"|"runas") || verb=="activate") {
                super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/weg".into(),WidgetPerm::Run)?;
            }
        }
        if session.native_renderer_pid != 0 {
            let _ = unsafe {
                windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(
                    session.native_renderer_pid,
                )
            };
        }
        match command["op"].as_str().unwrap_or("") {
            "tray" => {
                let icon_id=command["id"].as_str().ok_or("Tray item expired")?.to_owned();
                let action=serde_json::from_value(json!(if verb=="context" {"RightClick"} else if verb=="middle" {"MiddleClick"} else {"LeftClick"}))?;
                Handlers::send_system_tray_icon_action(icon_id, action)?;
            }
            "popup" => {
                let mut payload =
                    WidgetTriggerPayload::new(command["widget"].as_str().unwrap_or("").into());
                let position = serde_json::from_value(
                    json!({"x":value["x"].as_i64().unwrap_or(0),"y":value["y"].as_i64().unwrap_or(0)}),
                )?;
                payload.desired_position = Some(position);
                payload.align_x = Some(seelen_core::state::Alignment::Center);
                payload.align_y = Some(if FULL_STATE.load().settings.by_widget.fancy_toolbar.position==seelen_core::state::FancyToolbarSide::Bottom {seelen_core::state::Alignment::End}else{seelen_core::state::Alignment::Start});
                if command["power"] == true {
                    payload.add_custom_arg("view", "power");
                }
                super::trigger_widget_inner(payload, None)?;
            }
            "uac" => {
                crate::modules::uac::application::PendingUacManager::instance()
                    .open(command["id"].as_str().unwrap_or("").into())?;
            }
            "dock" => {
                let item = &command["item"];
                if matches!(verb,"empty-bin"|"edit-custom-icon") && item["type"]=="Plugin" {
                    if verb=="empty-bin" && item["plugin"]=="@default/weg-trash-bin" {Handlers::trash_bin_empty()?;}
                    else if verb=="edit-custom-icon" {super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/weg".into(),WidgetPerm::ModifyConfiguration)?;
                        let custom=match item["plugin"].as_str().unwrap_or("") {"@default/weg-start-menu"=>"@seelen/weg::start-menu","@default/weg-show-desktop"=>"@seelen/weg::show-desktop","@default/weg-trash-bin"=>if Handlers::get_trash_bin_info().item_count>0 {"bin::full"}else{"bin::empty"},_=>return Err("No custom icon for this module".into())};
                        let mut payload=WidgetTriggerPayload::new("@seelen/icon-editor".into());payload.add_custom_arg("entry",json!({"type":"custom","key":custom}));super::trigger_widget_inner(payload,None)?;
                    } else {return Err("Invalid module operation".into());}
                } else if verb=="kill" {
                    use std::os::windows::process::CommandExt;
                    super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/weg".into(),WidgetPerm::KillProcesses)?;
                    let mut pids=HashSet::new();for hwnd in command["windows"].as_array().into_iter().flatten().filter_map(|w|w["hwnd"].as_i64()) {let pid=Window::from(hwnd as isize).process().id();if pids.insert(pid){std::process::Command::new("taskkill.exe").args(["/F","/PID",&pid.to_string()]).creation_flags(0x08000000).spawn()?;}}
                } else if verb == "reorder" || verb == "pin" || verb == "unpin" {
                    super::permissions::WIDGET_PERMISSIONS
                        .request(&"@seelen/weg".into(), WidgetPerm::ModifyConfiguration)?;
                    let mut items = WEG_ITEMS_MANAGER.get();
                    if verb == "reorder" {
                        let dest = value["destination"].as_str().ok_or("Missing destination")?;
                        for group in [&mut items.left, &mut items.center, &mut items.right] {
                            let from = group.iter().position(|i| i.id().to_string() == id);
                            let to = group.iter().position(|i| i.id().to_string() == dest);
                            if let (Some(a), Some(b)) = (from, to) {
                                group.swap(a, b);
                            }
                        }
                    } else if verb == "unpin" {
                        let open=command["windows"].as_array().is_some_and(|w|!w.is_empty());
                        for group in [&mut items.left, &mut items.center, &mut items.right] {
                            if open {for item in group.iter_mut(){if let WegItem::AppOrFile(data)=item {if data.id.to_string()==id {data.pinned=false;}}}}
                            else {group.retain(|i| i.id().to_string()!=id);}
                        }
                    } else {
                        let mut source = item.clone();
                        source["id"] = json!(uuid::Uuid::new_v4());
                        if source["path"].is_null() {
                            source["path"] = json!("");
                        }
                        let mut data: WegItemData = serde_json::from_value(source)?;
                        data.id = uuid::Uuid::new_v4();
                        data.pinned = true;
                        items.center.push(WegItem::AppOrFile(data));
                    }
                    WEG_ITEMS_MANAGER.write(items)?;
                } else if verb=="edit-icon" {
                    super::permissions::WIDGET_PERMISSIONS
                        .request(&"@seelen/weg".into(), WidgetPerm::ModifyConfiguration)?;
                    let mut payload = WidgetTriggerPayload::new("@seelen/icon-editor".into());
                    payload.add_custom_arg("entry", configuration::app_icon_entry(item, Handlers::get_system_dark_mode().unwrap_or(false)));
                    super::trigger_widget_inner(payload, None)?;
                } else if verb=="open-location" {
                    if let Some(path)=item["path"].as_str(){Handlers::select_file_on_explorer(path.into())?;}
                } else if item["type"]=="Media" {
                    if let Some(player)=Handlers::get_media_sessions().into_iter().find(|p|p.default){match verb {"prev"=>Handlers::media_prev(player.umid)?,"play"=>Handlers::media_toggle_play_pause(player.umid)?,"next"=>Handlers::media_next(player.umid)?,_=>{super::trigger_widget_inner(WidgetTriggerPayload::new("@seelen/media-popup".into()),None)?;}}}
                } else if item["type"] == "Plugin" {
                    match item["plugin"].as_str().unwrap_or("") {
                        "@default/weg-start-menu" => {
                            super::trigger_widget_inner(
                                WidgetTriggerPayload::new("@seelen/apps-menu".into()),
                                None,
                            )?;
                        }
                        "@default/weg-show-desktop" => {
                            crate::windows_api::Com::run_with_context(|| {
                                use windows::Win32::UI::Shell::{IShellDispatch6, Shell};
                                let shell: IShellDispatch6 =
                                    crate::windows_api::Com::create_instance(&Shell)?;
                                unsafe {
                                    shell.ToggleDesktop()?;
                                }
                                Ok(())
                            })?;
                        }
                        "@default/weg-power"=>{WindowsApi::execute("ms-settings:powersleep".into(),None,None,false)?;},
                        "@default/weg-trash-bin" => {
                            WindowsApi::execute("shell:RecycleBinFolder".into(), None, None, false)?
                        }
                        _ => {
                            let mut payload =
                                WidgetTriggerPayload::new(toolbar_presentation::dock_popup(item["plugin"].as_str().unwrap_or("")).ok_or("Unsupported native Dock plugin")?.into());
                            payload.desired_position = Some(serde_json::from_value(
                                json!({"x":value["x"],"y":value["y"]}),
                            )?);
                            use seelen_core::state::{Alignment,SeelenWegSide};let side=FULL_STATE.load().settings.by_widget.weg.position;
                            payload.align_x=Some(match side {SeelenWegSide::Left=>Alignment::Start,SeelenWegSide::Right=>Alignment::End,_=>Alignment::Center});
                            payload.align_y=Some(match side {SeelenWegSide::Top=>Alignment::Start,SeelenWegSide::Bottom=>Alignment::End,_=>Alignment::Center});
                            super::trigger_widget_inner(payload, None)?;
                        }
                    }
                } else {
                    let first=configuration::offered_window(&command,value["target"].as_i64())?;
                    if verb=="close-one" {
                        if let Some(hwnd)=first {WindowsApi::post_message(Window::from(hwnd as isize).hwnd(),windows::Win32::UI::WindowsAndMessaging::WM_CLOSE,0,0)?;}
                    } else if verb == "close" {
                        for hwnd in command["windows"].as_array().into_iter().flatten().filter_map(|w|w["hwnd"].as_i64()) {
                            WindowsApi::post_message(
                                Window::from(hwnd as isize).hwnd(),
                                windows::Win32::UI::WindowsAndMessaging::WM_CLOSE,
                                0,
                                0,
                            )?;
                        }
                    } else if verb != "launch" && verb != "runas" && first.is_some() {
                        let window = Window::from(first.unwrap() as isize);
                        if value["wasFocused"] == true && verb != "focus" {
                            window.show_window_async(
                                windows::Win32::UI::WindowsAndMessaging::SW_MINIMIZE,
                            )?;
                        } else {
                            window.unminimize()?;
                            window.focus()?;
                        }
                    } else if let Some(relaunch) = item.get("relaunch").filter(|r| !r.is_null()) {
                        let args = relaunch["args"].as_str().map(str::to_owned);
                        WindowsApi::execute(
                            relaunch["command"].as_str().unwrap_or("").into(),
                            args,
                            relaunch["workingDir"]
                                .as_str()
                                .map(std::path::PathBuf::from),
                            verb == "runas",
                        )?;
                    } else if let Some(umid)=item["umid"].as_str().filter(|s|!s.is_empty()) {
                        WindowsApi::execute(format!("shell:AppsFolder\\{umid}"),None,None,verb=="runas")?;
                    } else if let Some(path) = item["path"].as_str() {
                        WindowsApi::execute(path.into(), None, None, verb == "runas")?;
                    }
                }
            }
            _ => return Err("Unknown shell command".into()),
        }
        Ok(())
    }
}
