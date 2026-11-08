//! Optional native routing for three explicitly selected community popups.
//! No startup connection, idle heartbeat, new system hook or WebView bridge.
use super::desktop_drag::BridgeSession;
use crate::{
    error::{Result, ResultLogExt},
    get_tokio_handle,
    state::application::FULL_STATE,
    tauri_handlers::Handlers,
};
use seelen_core::state::WidgetTriggerPayload;
use serde_json::{Value, json};
use std::{
    collections::HashSet,
    sync::atomic::{AtomicUsize, Ordering},
    time::Duration,
};
const PROTOCOL: &str = "Magic.NativePopups/1";
const SUPPORTED: [&str; 12] = [
    "@seelen/keyboard-selector",
    "@seelen/calendar-popup",
    "@seelen/user-menu",
    "@seelen/quick-settings",
    "@seelen/media-popup",
    "@seelen/network-popup",
    "@seelen/bluetooth-popup",
    "@seelen/notifications",
    "@seelen/system-tray",
    "@seelen/apps-menu",
    "@seelen/task-switcher",
    "@seelen/power-menu",
];
static IN_FLIGHT: AtomicUsize = AtomicUsize::new(0);
struct Flight;
impl Drop for Flight {
    fn drop(&mut self) {
        IN_FLIGHT.fetch_sub(1, Ordering::Release);
    }
}
/// Called once on each event-driven deployment reconcile; metadata remains intact.
pub(super) fn selection() -> HashSet<String> {
    let Ok(root) = BridgeSession::project_root() else {
        return HashSet::new();
    };
    use std::io::Read;
    let Ok(file) = std::fs::File::open(root.join("app.current.json")) else {
        return HashSet::new();
    };
    let mut bytes = Vec::new();
    if file.take(1024 * 1024 + 1).read_to_end(&mut bytes).is_err() {
        return HashSet::new();
    }
    if bytes.len() > 1024 * 1024 {
        return HashSet::new();
    }
    let Ok(value) = serde_json::from_slice::<Value>(&bytes) else {
        return HashSet::new();
    };
    if value["nativePopupProtocol"] != 1 || value["desktopRenderer"] != "composition" {
        return HashSet::new();
    }
    value["nativePopups"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .filter(|id| SUPPORTED.contains(id))
        .map(str::to_owned)
        .collect()
}
pub(super) fn try_trigger(payload: WidgetTriggerPayload) -> bool {
    let id = payload.id.to_string();
    if !SUPPORTED.contains(&id.as_str()) || !selection().contains(&id) {
        return false;
    }
    // Other locales retain their existing translated popup until the native
    // locale catalog is extended. Both paths keep the same trigger command.
    let language = FULL_STATE.load().settings.language.clone();
    if id!="@seelen/apps-menu"&&!language.starts_with("zh") && !language.starts_with("en") {
        return false;
    }
    if IN_FLIGHT
        .fetch_update(Ordering::AcqRel, Ordering::Acquire, |n| {
            if n < 8 { Some(n + 1) } else { None }
        })
        .is_err()
    {
        return true;
    }
    get_tokio_handle().spawn(async move {
        let _flight = Flight;
        let started = std::time::Instant::now();
        let mut accepted = false;
        match open_and_wait(&payload, &mut accepted).await {
            Ok(true) => {}
            Ok(false) => {
                log::warn!("Native popup {} declined before acceptance after {} ms; checking compatible entry", payload.id, started.elapsed().as_millis());
                super::trigger_widget_routed(payload, None, false).log_error();
            }
            Err(error) => {
                if accepted {
                    log::warn!("Native popup {} session ended after acceptance; compatible popup suppressed: {error}", payload.id);
                } else {
                    log::warn!("Native popup {} unavailable before acceptance after {} ms; checking compatible entry: {error}", payload.id, started.elapsed().as_millis());
                    super::trigger_widget_routed(payload, None, false).log_error();
                }
            }
        }
    });
    true
}
async fn json_response(response: reqwest::Response) -> Result<Value> {
    let response = response.error_for_status()?;
    if response.content_length().is_some_and(|n| n > 65536) {
        return Err("Native popup response too large".into());
    }
    let bytes = response.bytes().await?;
    if bytes.len() > 65536 {
        return Err("Native popup response too large".into());
    }
    Ok(serde_json::from_slice(&bytes)?)
}
async fn open_and_wait(payload: &WidgetTriggerPayload, accepted: &mut bool) -> Result<bool> {
    let started=std::time::Instant::now();
    let session = BridgeSession::current()?;
    let id = payload.id.to_string();
    if !session.native_popups.contains(&id) {
        return Ok(false);
    }
    let mut panel = if super::native_panels::IDS.contains(&id.as_str()) {
        Some(super::native_panels::Panel::new(&id))
    } else {
        None
    };
    if id == "@seelen/network-popup" {
        Handlers::wlan_scan();
    }
    if let Some(panel) = panel.as_mut() {
        let args = serde_json::to_value(&payload.custom_args)?;
        panel.power_view = args["view"] == "power";
        if id=="@seelen/apps-menu" {panel.apps=Some(super::native_apps::Apps::load().await?);}
    }
    let bluetooth = BluetoothScan::new(&id);
    let state = FULL_STATE.load();
    let layouts = if id == "@seelen/keyboard-selector" {
        Handlers::get_system_languages()
            .into_iter()
            .flat_map(|language| {
                language.keyboard_layouts.into_iter().map(move |layout| {
                    json!({
                        "language": language.name,
                        "id": layout.id,
                        "handle": layout.handle,
                        "name": layout.display_name,
                        "active": layout.active,
                    })
                })
            })
            .take(256)
            .collect::<Vec<_>>()
    } else {
        Vec::new()
    };
    let mut data = if let Some(panel) = panel.as_mut() {
        panel.safe_data()?
    } else {
        json!({"language":state.settings.language,"startOfWeek":state.settings.start_of_week,"layouts":layouts,"dark":Handlers::get_system_dark_mode().unwrap_or(false)})
    };
    let measure = serde_json::to_value(&payload.custom_args)?["__nativeMeasure"] == true;
    data["measurement"] = json!(measure);
    if id == "@seelen/task-switcher" {
        data["taskArgs"] = serde_json::to_value(&payload.custom_args)?
            .as_object()
            .map(|v| json!(v))
            .unwrap_or_else(|| json!({}));
    }
    drop(state);
    let client = reqwest::Client::builder()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(Duration::from_millis(300))
        .build()?;
    if session.native_renderer_pid != 0 {
        // Transfer only foreground permission to the verified community renderer.
        let renderer =
            crate::windows_api::WindowsApi::exe_path_by_process(session.native_renderer_pid)?;
        let expected = BridgeSession::project_root()?
            .join("versions")
            .join(&session.version)
            .join("MagicDesktopComposition.exe");
        if !renderer
            .to_string_lossy()
            .eq_ignore_ascii_case(&expected.to_string_lossy())
        {
            return Ok(false);
        }
        let _ = unsafe {
            windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(
                session.native_renderer_pid,
            )
        };
    }
    let prepared_ms=started.elapsed().as_millis();
    let open = json_response(
        client
            .post(format!("{}/composition-popup-open", session.endpoint))
            .bearer_auth(&session.token)
            .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
            .timeout(Duration::from_millis(1600))
            .json(&json!({"widgetId":id,"payload":payload,"data":data}))
            .send()
            .await?,
    )
    .await?;
    if open["accepted"] != true {
        return Ok(false);
    }
    *accepted = true;
    log::info!("Native popup ready: widget={} prepare={}ms open={}ms total={}ms",id,prepared_ms,started.elapsed().as_millis().saturating_sub(prepared_ms),started.elapsed().as_millis());
    let token = open["token"]
        .as_str()
        .ok_or("Missing native popup receipt")?;
    if token.len() != 32 || !token.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err("Invalid native popup receipt".into());
    }
    // Once displayed, a failure must not unexpectedly reopen a second popup.
    // A single awaited request carries the result, with no repeated polling.
    let events = panel.as_ref().map(|p| p.events()).unwrap_or_default();
    let subscription = NativeEvents::new(&format!("native-popup-{token}"), &events);
    loop {
        let wait = async {
            json_response(
                client
                    .post(format!("{}/composition-popup-wait", session.endpoint))
                    .bearer_auth(&session.token)
                    .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
                    .timeout(Duration::from_secs(310))
                    .json(&json!({"token":token}))
                    .send()
                    .await?,
            )
            .await
        };
        tokio::pin!(wait);
        let response = loop {
            tokio::select! {
                result=&mut wait=>break result,
                _=subscription.connection.notified(), if panel.is_some()=>{
                    tokio::time::sleep(Duration::from_millis(100)).await;
                    subscription.connection.drain();
                    if let Some(panel)=panel.as_mut(){let mut data=panel.safe_data()?;data["measurement"]=json!(measure);if !update(&client,&session,token,&data).await?{return Ok(true)}}
                }
            }
        };
        let Ok(response) = response else { break };
        if response["keepOpen"] == true {
            if let Some(panel) = panel.as_mut() {
                match panel.execute(&response["result"]).await {
                    Ok(true) => {
                        let _ = json_response(
                            client
                                .post(format!("{}/composition-popup-update", session.endpoint))
                                .bearer_auth(&session.token)
                                .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
                                .timeout(Duration::from_secs(2))
                                .json(&json!({"token":token,"close":true,"data":{}}))
                                .send()
                                .await?,
                        )
                        .await;
                        break;
                    }
                    Ok(false) => {}
                    Err(error) => panel.status = format!("操作未完成：{error}"),
                }
                let mut data = panel.safe_data()?;
                data["measurement"] = json!(measure);
                if !update(&client, &session, token, &data).await? {
                    break;
                }
            }
        } else {
            execute(&id, &layouts, &client, &session, &response)
                .await
                .log_error();
            break;
        }
    }
    drop(bluetooth);
    Ok(true)
}
async fn execute(
    id: &str,
    layouts: &[Value],
    client: &reqwest::Client,
    session: &BridgeSession,
    result: &Value,
) -> Result<()> {
    let action = result["action"]
        .as_str()
        .ok_or("Missing native popup action")?;
    if action == "cancel" {
        return Ok(());
    }
    match (id, action) {
        ("@seelen/keyboard-selector", "keyboard-select") => {
            let selected = layouts
                .iter()
                .find(|layout| layout["id"] == result["id"] && layout["handle"] == result["handle"])
                .ok_or("Input layout was not offered")?;
            Handlers::set_system_keyboard_layout(
                selected["id"]
                    .as_str()
                    .ok_or("Invalid layout id")?
                    .to_owned(),
                selected["handle"]
                    .as_str()
                    .ok_or("Invalid layout handle")?
                    .to_owned(),
            )?;
        }
        ("@seelen/keyboard-selector", "keyboard-settings") => {
            open_settings("ms-settings:regionlanguage")?
        }
        ("@seelen/calendar-popup", "date-settings") => open_settings("ms-settings:dateandtime")?,
        ("@seelen/user-menu", "settings" | "account" | "feedback" | "exit") => {
            json_response(
                client
                    .post(format!("{}/community-menu", session.endpoint))
                    .bearer_auth(&session.token)
                    .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
                    .timeout(Duration::from_secs(3))
                    .json(&json!({"kind":action}))
                    .send()
                    .await?,
            )
            .await?;
        }
        ("@seelen/user-menu", "sleep") => Handlers::suspend(),
        ("@seelen/user-menu", "lock") => Handlers::lock()?,
        ("@seelen/user-menu", "restart" | "shutdown") if result["confirmed"] == true => {
            if action == "restart" {
                Handlers::restart(Some(false))?
            } else {
                Handlers::shutdown(Some(false))?
            }
        }
        _ => return Err("Unsupported native popup action".into()),
    }
    Ok(())
}
pub(super) fn open_settings(uri: &str) -> Result<()> {
    // The caller above supplies fixed Windows settings URIs, never a payload URL.
    let explorer =
        std::path::PathBuf::from(std::env::var_os("WINDIR").ok_or("Missing Windows directory")?)
            .join("explorer.exe");
    std::process::Command::new(explorer).arg(uri).spawn()?;
    Ok(())
}

async fn update(
    client: &reqwest::Client,
    session: &BridgeSession,
    token: &str,
    data: &Value,
) -> Result<bool> {
    let result = json_response(
        client
            .post(format!("{}/composition-popup-update", session.endpoint))
            .bearer_auth(&session.token)
            .header("X-Magic-Native-Popup-Protocol", PROTOCOL)
            .timeout(Duration::from_secs(2))
            .json(&json!({"token":token,"data":data}))
            .send()
            .await?,
    )
    .await?;
    Ok(result["accepted"] == true)
}
struct NativeEvents {
    label: String,
    connection: std::sync::Arc<crate::server::events::Connection>,
}
impl NativeEvents {
    fn new(label: &str, events: &[&str]) -> Self {
        let hub = &crate::server::events::EVENTS_HUB;
        let connection = hub.connect(label);
        for event in events {
            hub.subscribe(&connection, event);
        }
        Self {
            label: label.into(),
            connection,
        }
    }
}
impl Drop for NativeEvents {
    fn drop(&mut self) {
        crate::server::events::EVENTS_HUB.disconnect(&self.label, &self.connection);
    }
}
struct BluetoothScan(bool);
impl BluetoothScan {
    fn new(id: &str) -> Self {
        Self(id == "@seelen/bluetooth-popup" && Handlers::start_bluetooth_scanning().is_ok())
    }
}
impl Drop for BluetoothScan {
    fn drop(&mut self) {
        if self.0 {
            Handlers::stop_bluetooth_scanning().log_error();
        }
    }
}
