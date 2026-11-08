//! Native panel view models. Actions are fixed backend capabilities, never arbitrary commands.
use crate::{error::Result, tauri_handlers::Handlers};
use seelen_core::{
    handlers::SeelenEvent,
    system_state::{DevicePairingAnswer, MonitorId, RadioDeviceKind, SystrayIconAction},
};
use serde_json::{Value, json};
use std::{
    collections::HashMap,
    hash::{Hash, Hasher},
};

pub(super) const IDS: [&str; 9] = [
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
#[derive(Default)]
pub(super) struct Panel {
    pub id: String,
    pub apps:Option<super::native_apps::Apps>,
    pub commands: HashMap<String, Value>,
    rows: Vec<Value>,
    pub status: String,
    pub query: String,
    pub power_view: bool,
    pub power_confirm: Option<String>,
    pub selected: Option<Value>,
    pub pairing: Option<(String, Value)>,
}
impl Panel {
    pub fn new(id: &str) -> Self {
        Self {
            id: id.into(),
            ..Self::default()
        }
    }
    fn add(
        &mut self,
        key: impl Into<String>,
        label: impl Into<String>,
        detail: impl Into<String>,
        control: &str,
        value: f64,
        command: Value,
    ) {
        if self.rows.len() >= 192 {
            return;
        }
        let key = key.into();
        let enabled = !command.is_null();
        let image = command.get("image").or_else(|| if command["op"] == "launch-app" {command.get("path")} else {None}).cloned().unwrap_or(Value::Null);
        // Only authenticated panel metadata crosses to the native icon worker.
        // Packaged applications can have an AUMID without a filesystem path.
        let icon_umid=command.get("umid").and_then(Value::as_str).unwrap_or("").to_owned();
        let image=if image.as_str().is_none_or(str::is_empty)&&!icon_umid.is_empty(){json!(format!("shell:AppsFolder\\{icon_umid}"))}else{image};
        self.commands.insert(key.clone(), command);
        self.rows.push(json!({"id":key,"label":label.into().chars().take(256).collect::<String>(),"detail":detail.into().chars().take(512).collect::<String>(),"control":control,"value":value,"enabled":enabled,"image":image,"iconUmid":icon_umid}));
    }
    fn button(&mut self, key: impl Into<String>, label: impl Into<String>, command: Value) {
        self.add(key, label, "", "button", 0.0, command)
    }
    fn info(&mut self, key: &str, label: impl Into<String>, detail: impl Into<String>) {
        self.add(key, label, detail, "label", 0.0, Value::Null)
    }
    fn toggle(&mut self, key: &str, label: &str, current: bool, command: Value) {
        self.add(
            key,
            format!("{}  {}", if current { "●" } else { "○" }, label),
            "",
            "toggle",
            if current { 1.0 } else { 0.0 },
            command,
        )
    }
    fn media(&mut self, mixer: bool) -> Result<()> {
        let (inputs, outputs) = Handlers::get_media_devices()?;
        for device in outputs.into_iter().chain(inputs).take(16) {
            if !mixer && !device.is_default_multimedia {continue;}
            let id = device.id.clone();
            self.add(
                format!("volume:{id}"),
                format!("{} · {}%", device.name, (device.volume * 100.0).round()),
                "",
                "range",
                device.volume as f64,
                json!({"op":"volume","device":id}),
            );
            self.button(
                format!("mute:{id}"),
                if device.muted {
                    "取消静音"
                } else {
                    "静音"
                },
                json!({"op":"mute","device":id}),
            );
            if mixer { self.button(
                format!("default:{id}"),
                if device.is_default_multimedia {
                    "✓ 默认设备"
                } else {
                    "设为默认设备"
                },
                json!({"op":"default","device":id}),
            ); }
            if mixer {
                for session in device.sessions.into_iter().take(24) {
                    self.add(
                        format!("session:{id}:{}", session.id),
                        format!("{} · {}%", session.name, (session.volume * 100.0).round()),
                        "",
                        "range",
                        session.volume as f64,
                        json!({"op":"volume","device":id,"session":session.id}),
                    );
                }
            }
        }
        if mixer {
            for player in Handlers::get_media_sessions().into_iter().take(8) {
                self.info(
                    &format!("track:{}", player.umid),
                    player.title,
                    player.author,
                );
                for (suffix, label) in [
                    ("prev", "上一首"),
                    ("play", "播放／暂停"),
                    ("next", "下一首"),
                ] {
                    self.button(
                        format!("player:{}:{suffix}", player.umid),
                        label,
                        json!({"op":suffix,"player":player.umid}),
                    );
                }
            }
        }
        Ok(())
    }
    fn radios(&mut self) {
        for radio in Handlers::get_radios().into_iter().take(8) {
            let kind = serde_json::to_value(&radio.kind).unwrap_or_default();
            self.toggle(
                &format!("radio:{kind}"),
                &format!("{:?}", radio.kind),
                radio.is_enabled,
                json!({"op":"radio","kind":kind,"enabled":!radio.is_enabled}),
            );
        }
    }
    pub fn safe_data(&mut self) -> Result<Value> {
        match self.data() {
            Ok(data) => Ok(data),
            Err(error) => {
                self.rows.clear();
                self.commands.clear();
                self.info("error", "系统数据暂不可用", error.to_string());
                self.button("refresh", "重试", json!({"op":"refresh"}));
                Ok(json!({"language":"zh-CN","title":"系统面板","rows":self.rows}))
            }
        }
    }
    pub fn data(&mut self) -> Result<Value> {
        self.rows.clear();
        self.commands.clear();
        let title = match self.id.as_str() {
            "@seelen/power-menu" => {
                if let Some(action) = self.power_confirm.clone() {
                    self.info("confirm", "请确认系统操作", action.clone());
                    self.button(
                        "confirm-yes",
                        "确认",
                        json!({"op":"system-confirm","value":action}),
                    );
                    self.button("confirm-no", "取消", json!({"op":"system-cancel"}));
                } else {
                    for (op, label) in [
                        ("sleep", "睡眠"),
                        ("lock", "锁定"),
                        ("restart", "重新启动"),
                        ("shutdown", "关机"),
                    ] {
                        self.button(op, label, json!({"op":"system-action","value":op}));
                    }
                }
                "电源"
            }
            "@seelen/apps-menu" => {
                self.add(
                    "search",
                    "搜索应用",
                    "",
                    "text",
                    0.0,
                    json!({"op":"search"}),
                );
                let (filter,query,original_query)=super::native_apps::query(&self.query);let filter=filter.as_str();
                let mut catalog=Handlers::get_start_menu_items().into_iter().map(|a|json!({"path":a.path,"umid":a.umid,"target":a.target,"displayName":a.display_name})).collect::<Vec<_>>();
                if filter=="files"||!query.is_empty()&&filter.is_empty(){
                    use seelen_core::system_state::FolderType;
                    for kind in [FolderType::Desktop,FolderType::Downloads,FolderType::Documents,FolderType::Music,FolderType::Pictures,FolderType::Videos]{for path in Handlers::get_user_folder_content(kind){let label=path.file_name().map(|s|s.to_string_lossy().to_string()).unwrap_or_default();if label.to_lowercase().contains(&query){catalog.push(json!({"path":path,"displayName":label,"document":true}));}}}
                }
                let all=self.apps.as_ref().is_none_or(|a|a.all)||!self.query.trim().is_empty();
                self.button("apps-view",if all {"收藏"}else{"全部应用"},json!({"op":"apps-view"}));
                let folder=self.apps.as_ref().and_then(|a|a.folder.clone());
                if folder.is_some(){self.button("apps-back","返回收藏",json!({"op":"apps-back"}));self.add("folder-name","文件夹名称","","text",0.0,json!({"op":"apps-rename"}));}
                let favorites=self.apps.as_ref().map(|a|a.favorites.clone()).unwrap_or_default();
                let mut rows=Vec::new();
                if all {rows.extend(catalog.iter().filter(|v|(filter.is_empty()||filter=="apps"&&v["document"]!=true||filter=="files"&&v["document"]==true)&&super::native_apps::matches_name(v,&query)).cloned());
                    if filter.is_empty()||filter=="settings" {for setting in self.apps.as_ref().into_iter().flat_map(|a|a.settings.iter()){if (query.is_empty()&&filter=="settings")||!query.is_empty()&&(setting["title"].as_str().unwrap_or("").to_lowercase().contains(&query)||setting["keywords"].to_string().to_lowercase().contains(&query)){rows.push(json!({"path":setting["target"],"displayName":setting["title"]}));}}}
                    if filter=="web"&&!query.is_empty(){let encoded=url::form_urlencoded::byte_serialize(original_query.as_bytes()).collect::<String>();rows.push(json!({"path":format!("https://www.google.com/search?q={encoded}"),"displayName":format!("在网页中搜索：{original_query}")}));}
                }
                else {
                    let entries=if let Some(id)=&folder {favorites.iter().find(|v|v["itemId"].as_str()==Some(id)).and_then(|v|v["itemIds"].as_array()).map(|a|a.iter().map(|id|json!({"type":"app","itemId":id})).collect::<Vec<_>>()).unwrap_or_default()}else{favorites.clone()};
                    for entry in entries {
                        let id=entry["itemId"].as_str().unwrap_or("");
                        if entry["type"]=="folder"{rows.push(json!({"folder":true,"folderId":id,"displayName":entry["name"].as_str().filter(|s|!s.is_empty()).unwrap_or("收藏文件夹")}));}
                        else if let Some(app)=catalog.iter().find(|v|super::native_apps::app_id(v)==id){rows.push(app.clone());}
                        else if id.contains('\\')||id.contains('/') {rows.push(json!({"path":id,"displayName":std::path::Path::new(id).file_name().map(|s|s.to_string_lossy().to_string()).unwrap_or_else(||id.to_owned())}));}
                    }
                }
                let total=rows.len();let offset=self.apps.as_ref().map(|a|a.offset).unwrap_or(0).min(total.saturating_sub(1)/168*168);
                if offset>0 {self.button("apps-previous","上一页",json!({"op":"apps-page","delta":-168}));}
                if offset+168<total {self.button("apps-next","下一页",json!({"op":"apps-page","delta":168}));}
                for item in rows.into_iter().skip(offset).take(168){
                    let app_id=if item["folder"]==true {item["folderId"].as_str().unwrap_or("").to_owned()}else{super::native_apps::app_id(&item)};
                    let mut identity=std::collections::hash_map::DefaultHasher::new();app_id.hash(&mut identity);
                    let key=format!("app:{:016x}",identity.finish());
                    self.button(key,item["displayName"].as_str().unwrap_or("应用"),json!({"op":"launch-app","path":item["path"],"umid":item["umid"],"appId":app_id,"folder":item["folder"]==true}));
                    if let Some(row)=self.rows.last_mut(){row["pinned"]=json!(self.apps.as_ref().is_some_and(|a|a.contains(&app_id)));row["folder"]=json!(item["folder"]==true);row["hasPath"]=json!(item["path"].as_str().is_some_and(|p|!p.is_empty()));row["application"]=json!(item["umid"].is_string()||item["path"].as_str().is_some_and(|p|p.to_lowercase().ends_with(".exe")||p.to_lowercase().ends_with(".lnk")));}
                }
                self.button("apps-display",if self.apps.as_ref().is_some_and(|a|a.fullscreen){"窗口"}else{"全屏"},json!({"op":"apps-display"}));
                self.button("apps-power", "电源", json!({"op":"open-power"}));
                "应用"
            }
            "@seelen/task-switcher" => {
                let focused = Handlers::get_focused_app().hwnd;
                let mut windows = Handlers::get_user_app_windows();
                windows.sort_by_key(|w| std::cmp::Reverse(w.last_foreground_at));
                for window in windows.into_iter().take(128) {
                    self.add(
                        format!("window:{}", window.hwnd),
                        window.app_name,
                        window.title,
                        "button",
                        if window.hwnd == focused { 1.0 } else { 0.0 },
                        json!({"op":"focus-window","window":window.hwnd,"image":window.process.path}),
                    );
                }
                "切换窗口"
            }
            "@seelen/quick-settings" => {
                if self.power_view {
                    let power = Handlers::get_power_status();
                    self.info(
                        "power-status",
                        if power.battery_life_percent <= 100 {
                            format!("电量 {}%", power.battery_life_percent)
                        } else {
                            "电量未知".into()
                        },
                        if power.ac_line_status == 1 {
                            "接通电源"
                        } else {
                            "电池供电"
                        },
                    );
                    self.info(
                        "power-mode",
                        "Windows 电源状态",
                        match Handlers::get_power_mode() {
                            seelen_core::system_state::PowerMode::BatterySaver => "节能",
                            seelen_core::system_state::PowerMode::BetterBattery => "更长续航",
                            seelen_core::system_state::PowerMode::Balanced => "平衡",
                            seelen_core::system_state::PowerMode::HighPerformance => "高性能",
                            seelen_core::system_state::PowerMode::MaxPerformance => "最佳性能",
                            seelen_core::system_state::PowerMode::GameMode => "游戏",
                            seelen_core::system_state::PowerMode::MixedReality => "混合现实",
                            _ => "未知",
                        },
                    );
                    self.info(
                        "saver",
                        "Windows 节能模式",
                        if power.system_status_flag == 1 {"已开启"} else {"已关闭"},
                    );
                    self.info("honor-mode", "荣耀性能模式", "暂不可用");
                    for (index, battery) in
                        Handlers::get_batteries().into_iter().take(8).enumerate()
                    {
                        self.info(
                            &format!("battery:{index}"),
                            battery.model.unwrap_or_else(|| "电池".into()),
                            format!(
                                "健康度 {} · 循环 {}",
                                if battery.energy_full_design > 0.0 {
                                    format!(
                                        "{:.0}%",
                                        battery.energy_full / battery.energy_full_design * 100.0
                                    )
                                } else {
                                    "未知".into()
                                },
                                battery
                                    .cycle_count
                                    .map(|n| n.to_string())
                                    .unwrap_or_else(|| "未知".into())
                            ),
                        );
                    }
                    self.button("power-refresh", "刷新", json!({"op":"refresh"}));
                    self.button(
                        "power-settings",
                        "Windows 电源设置",
                        json!({"op":"power-settings"}),
                    );
                    self.button("quick-back", "返回快速设置", json!({"op":"power-back"}));
                } else {
                    self.radios();
                    for monitor in Handlers::get_connected_monitors().into_iter().take(8) {
                        if let Some(brightness) = monitor.brightness {
                            self.add(
                                format!("brightness:{}", monitor.id),
                                format!("{} · 亮度 {}%", monitor.name, brightness),
                                "",
                                "range",
                                brightness as f64 / 100.0,
                                json!({"op":"brightness","monitor":monitor.id}),
                            );
                        }
                    }
                    let dark = Handlers::get_system_dark_mode().unwrap_or(false);
                    self.toggle(
                        "dark",
                        "深色模式",
                        dark,
                        json!({"op":"dark","enabled":!dark}),
                    );
                    let night = Handlers::get_system_night_light_enabled().unwrap_or(false);
                    self.toggle(
                        "night",
                        "夜间模式",
                        night,
                        json!({"op":"night","enabled":!night}),
                    );
                    self.media(false)?;
                    self.button("power", "电量与性能", json!({"op":"power-view"}));
                }
                self.button("system-power", "电源", json!({"op":"open-power"}));
                "快速设置"
            }
            "@seelen/media-popup" => {
                self.media(true)?;
                "声音与媒体"
            }
            "@seelen/network-popup" => {
                self.radios();
                if let Some(selected) = self.selected.clone() {
                    let ssid = selected["ssid"].as_str().unwrap_or("");
                    self.info(
                        "network",
                        ssid,
                        format!(
                            "{} · {}%",
                            selected["auth"].as_str().unwrap_or(""),
                            selected["signal"]
                        ),
                    );
                    if selected["secured"] == true && selected["known"] != true {
                        self.add("password", "网络密码", "", "password", 0.0, Value::Null);
                    }
                    self.button("connect","连接",json!({"op":"wifi-connect","ssid":ssid,"secured":selected["secured"],"known":selected["known"]}));
                    if selected["connected"] == true {
                        self.button("disconnect", "断开连接", json!({"op":"wifi-disconnect"}));
                    }
                    if selected["known"] == true {
                        self.button(
                            "forget",
                            "忘记此网络",
                            json!({"op":"wifi-forget","ssid":ssid}),
                        );
                    }
                    self.button("back", "返回网络列表", json!({"op":"back"}));
                } else {
                    let networks = crate::modules::radios::wifi::WifiManager::instance()
                        .get_available_networks()?;
                    for (i, network) in networks.into_iter().take(48).enumerate() {
                        let row = serde_json::to_value(&network)?;
                        let name = network.ssid.as_deref().unwrap_or("隐藏网络");
                        self.add(
                            format!("network:{i}"),
                            format!("{}{}", if network.connected { "✓ " } else { "" }, name),
                            format!("{}% · {} · {}", network.signal, network.band, network.auth),
                            "button",
                            0.0,
                            json!({"op":"network-select","network":row}),
                        );
                    }
                    self.button("scan", "刷新网络", json!({"op":"wifi-scan"}));
                }
                self.button(
                    "network-settings",
                    "网络设置",
                    json!({"op":"settings","page":"network"}),
                );
                "网络"
            }
            "@seelen/bluetooth-popup" => {
                self.radios();
                if let Some((id, pair)) = self.pairing.clone() {
                    self.info("pair", "蓝牙配对", pair.to_string());
                    let needs = pair["needs"].as_str().unwrap_or("");
                    if matches!(
                        needs,
                        "ProvidePin" | "ProvidePasswordCredential" | "ProvideAddress"
                    ) {
                        self.add("pin", "PIN／地址", "", "text", 0.0, Value::Null);
                    }
                    if needs == "ProvidePasswordCredential" {
                        self.add("username", "用户名", "", "text", 0.0, Value::Null);
                        self.add("password", "密码", "", "password", 0.0, Value::Null);
                    }
                    self.button(
                        "pair-confirm",
                        "确认配对",
                        json!({"op":"pair-confirm","id":id,"needs":needs}),
                    );
                    self.button(
                        "pair-cancel",
                        "取消配对",
                        json!({"op":"pair-cancel","id":id}),
                    );
                } else {
                    for device in Handlers::get_bluetooth_devices().into_iter().take(32) {
                        self.info(
                            &format!("device:{}", device.id),
                            device.name,
                            if device.connected {
                                "已连接"
                            } else if device.paired {
                                "已配对"
                            } else {
                                "未配对"
                            },
                        );
                        if device.can_disconnect {
                            self.button(
                                format!("disconnect:{}", device.id),
                                "断开",
                                json!({"op":"bt-disconnect","id":device.id}),
                            );
                        }
                        if device.can_connect {
                            self.button(
                                format!("connect:{}", device.id),
                                "连接",
                                json!({"op":"bt-connect","id":device.id}),
                            );
                        }
                        if device.can_pair && !device.paired {
                            self.button(
                                format!("pair:{}", device.id),
                                "配对",
                                json!({"op":"pair-start","id":device.id}),
                            );
                        }
                        if device.paired {
                            self.button(
                                format!("forget:{}", device.id),
                                "移除配对",
                                json!({"op":"bt-forget","id":device.id}),
                            );
                        }
                    }
                }
                self.button(
                    "bt-settings",
                    "蓝牙设置",
                    json!({"op":"settings","page":"bluetooth"}),
                );
                "蓝牙"
            }
            "@seelen/notifications" => {
                let mode = serde_json::to_value(Handlers::get_notifications_mode()?)?;
                self.button("focus",format!("勿扰模式：{mode}"),json!({"op":"notification-mode","mode":if mode=="All"{"PriorityOnly"}else{"All"}}));
                for notification in Handlers::get_notifications().into_iter().take(40) {
                    let text = toast_text(&serde_json::to_value(&notification.content.visual)?, 0);
                    self.add(format!("notification:{}",notification.id),notification.app_name,text,"button",0.0,json!({"op":"notification-open","id":notification.id,"umid":notification.app_umid,"args":notification.content.launch,"activation":notification.content.activation_type}));
                    self.button(
                        format!("dismiss:{}", notification.id),
                        "关闭此通知",
                        json!({"op":"notification-close","id":notification.id}),
                    );
                    if let Some(actions) = notification.content.actions {
                        let value = serde_json::to_value(actions)?;
                        if let Some(children) = value["$value"].as_array() {
                            for (index, child) in children.iter().take(10).enumerate() {
                                if let Some(input) = child.get("input") {
                                    let id = input["@id"].as_str().unwrap_or("");
                                    self.add(
                                        format!("input:{}:{id}", notification.id),
                                        input["@title"].as_str().unwrap_or("回复"),
                                        "",
                                        "text",
                                        0.0,
                                        Value::Null,
                                    );
                                }
                                if let Some(action) = child.get("action") {
                                    self.button(format!("action:{}:{index}",notification.id),action["@content"].as_str().unwrap_or("打开"),json!({"op":"notification-open","id":notification.id,"umid":notification.app_umid,"args":action["@arguments"],"activation":action["@activationType"]}));
                                }
                            }
                        }
                    }
                }
                self.button("clear", "清除所有通知", json!({"op":"notification-clear"}));
                "通知"
            }
            "@seelen/system-tray" => {
                for icon in Handlers::get_system_tray_icons().into_iter().filter(|icon|icon.is_visible&&
                    !icon.guid.is_some_and(|g|["7820ae73-23e3-4229-82c1-e41cb67d5b9c","7820ae74-23e3-4229-82c1-e41cb67d5b9c","7820ae75-23e3-4229-82c1-e41cb67d5b9c"].contains(&g.to_string().as_str()))).take(96) {
                    self.add(
                        icon.registry_key.clone(),
                        if icon.tooltip.is_empty() {
                            icon.executable_path
                                .file_stem()
                                .unwrap_or_default()
                                .to_string_lossy()
                                .to_string()
                        } else {
                            icon.tooltip
                        },
                        if icon.is_promoted { "已固定" } else { "" },
                        "tray",
                        if icon.is_promoted {1.0}else{0.0},
                        json!({"op":"tray","id":icon.registry_key,"image":icon.icon_path.as_ref().unwrap_or(&icon.executable_path)}),
                    );
                    if let Some(row)=self.rows.last_mut(){row["glyph"]=json!(icon.is_glyph);row["imageVersion"]=json!(icon.icon_image_hash);}
                }
                "系统托盘"
            }
            _ => return Err("Unknown native panel".into()),
        };
        if !self.status.is_empty() {
            self.info("status", self.status.clone(), "");
        }
        Ok(
            json!({"language":"zh-CN","dark":Handlers::get_system_dark_mode().unwrap_or(false),"title":title,"rows":self.rows,"appsView":self.apps.as_ref().map(|a|if a.all||!self.query.is_empty(){"all"}else{"favorites"}),"fullscreen":self.apps.as_ref().is_some_and(|a|a.fullscreen),"query":self.query,"favoriteFolderOpen":self.apps.as_ref().is_some_and(|a|a.folder.is_some()),"favoriteFolderName":self.apps.as_ref().and_then(|a|a.folder.as_ref().and_then(|id|a.favorites.iter().find(|v|v["itemId"].as_str()==Some(id)).map(|v|v["name"].as_str().unwrap_or("").to_owned())))}),
        )
    }
    pub async fn execute(&mut self, result: &Value) -> Result<bool> {
        let key = result["id"].as_str().ok_or("Missing native action id")?;
        let command = self
            .commands
            .get(key)
            .filter(|c| !c.is_null())
            .ok_or("Native action was not offered")?
            .clone();
        let op = command["op"].as_str().ok_or("Missing native operation")?;
        let text = |id: &str| {
            result["fields"][id]
                .as_str()
                .unwrap_or("")
                .chars()
                .take(1024)
                .collect::<String>()
        };
        let value = result["value"]
            .as_f64()
            .filter(|n| n.is_finite())
            .unwrap_or(0.0)
            .clamp(0.0, 1.0);
        let string = |key: &str| command[key].as_str().unwrap_or("").to_owned();
        self.status.clear();
        match op {
            "open-power" => {
                super::trigger_widget_inner(seelen_core::state::WidgetTriggerPayload::new("@seelen/power-menu".into()),None)?;
                return Ok(true);
            }
            "system-action" => {
                let action = string("value");
                if action == "sleep" {
                    Handlers::suspend();
                } else if action == "lock" {
                    Handlers::lock()?;
                } else {
                    self.power_confirm = Some(action);
                }
            }
            "system-cancel" => self.power_confirm = None,
            "system-confirm" => {
                let action = string("value");
                if self.power_confirm.as_deref() != Some(action.as_str()) {
                    return Err("System confirmation expired".into());
                }
                self.power_confirm = None;
                if action == "restart" {
                    Handlers::restart(Some(false))?;
                } else if action == "shutdown" {
                    Handlers::shutdown(Some(false))?;
                }
            }
            "search" => {
                self.query=text("search");if let Some(apps)=self.apps.as_mut(){apps.offset=0;}
                if !self.query.is_empty()&&!self.query.starts_with("apps:")&&!self.query.starts_with("files:")&&!self.query.starts_with("web:")&&self.apps.as_ref().is_some_and(|a|a.settings.is_empty()){
                    let settings=tokio::task::spawn_blocking(Handlers::enum_system_settings).await?;if let Some(apps)=self.apps.as_mut(){apps.settings=settings.into_iter().filter_map(|v|serde_json::to_value(v).ok()).collect();}
                }
                let (filter,_,original)=super::native_apps::query(&self.query);if result["button"]==20&&filter=="web"{let encoded=url::form_urlencoded::byte_serialize(original.as_bytes()).collect::<String>();crate::windows_api::WindowsApi::execute(format!("https://www.google.com/search?q={encoded}"),None,None,false)?;return Ok(true);}
            },
            "apps-display"=>{if let Some(apps)=self.apps.as_mut(){apps.toggle_display()?;}},
            "apps-view"=>{if let Some(apps)=self.apps.as_mut(){apps.all=!apps.all;apps.folder=None;apps.offset=0;}self.query.clear();},
            "apps-page"=>{if let Some(apps)=self.apps.as_mut(){apps.offset=apps.offset.saturating_add_signed(command["delta"].as_i64().unwrap_or(0) as isize);}},
            "apps-back"=>{if let Some(apps)=self.apps.as_mut(){apps.folder=None;apps.offset=0;}},
            "apps-rename"=>{super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/apps-menu".into(),seelen_core::state::WidgetPerm::ModifyConfiguration)?;if let Some(apps)=self.apps.as_mut(){let id=apps.folder.clone().ok_or("Folder changed")?;apps.edit(19,&id,"",&text("folder-name"))?;}},
            "launch-app" => {
                let button=result["button"].as_u64().unwrap_or(0);let id=string("appId");
                if matches!(button,10|16|17|18|19|21){
                    super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/apps-menu".into(),seelen_core::state::WidgetPerm::ModifyConfiguration)?;
                    let dest=self.commands.get(result["destination"].as_str().unwrap_or("")).and_then(|v|v["appId"].as_str()).unwrap_or("").to_owned();
                    self.apps.as_mut().ok_or("Favorites unavailable")?.edit(button,&id,&dest,&text("folder-name"))?;return Ok(false);
                }
                if command["folder"]==true {self.apps.as_mut().ok_or("Favorites unavailable")?.folder=Some(id);self.apps.as_mut().unwrap().offset=0;return Ok(false);}
                let path = string("path");
                let target = if path.is_empty() {
                    format!("shell:AppsFolder\\{}", string("umid"))
                } else {
                    path.clone()
                };
                match button {
                    11=>{super::permissions::WIDGET_PERMISSIONS.request(&"@seelen/apps-menu".into(),seelen_core::state::WidgetPerm::ModifyConfiguration)?;let mut items=crate::state::application::WEG_ITEMS_MANAGER.get();let umid=command["umid"].as_str().map(str::to_owned);let path=std::path::PathBuf::from(string("path"));if !items.left.iter().chain(&items.center).chain(&items.right).any(|i|matches!(i,seelen_core::state::WegItem::AppOrFile(a) if (!path.as_os_str().is_empty()&&a.path==path)||(umid.is_some()&&a.umid==umid))){items.center.push(seelen_core::state::WegItem::AppOrFile(seelen_core::state::WegItemData{id:uuid::Uuid::new_v4(),display_name:self.rows.iter().find(|r|r["id"]==key).and_then(|r|r["label"].as_str()).unwrap_or("应用").to_owned(),umid,path,pinned:true,prevent_pinning:false,relaunch:None}));crate::state::application::WEG_ITEMS_MANAGER.write(items)?;}},
                    13=>Handlers::select_file_on_explorer(path.into())?,
                    14=>crate::windows_api::WindowsApi::execute("ms-settings:appsfeatures".into(),None,None,false)?,
                    15=>{let mut payload=seelen_core::state::WidgetTriggerPayload::new("@seelen/icon-editor".into());payload.add_custom_arg("entry",json!({"path":command["path"],"umid":command["umid"]}));super::trigger_widget_inner(payload,None)?;},
                    _=>crate::windows_api::WindowsApi::execute(target,None,None,button==12)?,
                }
            }
            "focus-window" => {
                super::permissions::WIDGET_PERMISSIONS.request(
                    &"@seelen/task-switcher".into(),
                    seelen_core::state::WidgetPerm::ManageAppWindows,
                )?;
                let hwnd = command["window"].as_i64().ok_or("Missing window")?;
                let window = crate::windows_api::window::Window::from(hwnd as isize);
                window.unminimize()?;
                window.focus()?;
            }
            "volume" => Handlers::set_volume_level(
                string("device"),
                command["session"].as_str().map(str::to_owned),
                value as f32,
            )?,
            "mute" => Handlers::media_toggle_mute(string("device"), None)?,
            "default" => {
                Handlers::media_set_default_device(string("device"), "multimedia".into())?;
                Handlers::media_set_default_device(string("device"), "communications".into())?;
            }
            "prev" => Handlers::media_prev(string("player"))?,
            "play" => Handlers::media_toggle_play_pause(string("player"))?,
            "next" => Handlers::media_next(string("player"))?,
            "brightness" => Handlers::set_monitor_brightness(
                MonitorId(string("monitor")),
                (value * 100.0).round() as u8,
            )?,
            "radio" => Handlers::set_radios_state(
                serde_json::from_value::<RadioDeviceKind>(command["kind"].clone())?,
                command["enabled"] == true,
            )?,
            "dark" => Handlers::set_system_dark_mode(command["enabled"] == true)?,
            "night" => Handlers::set_system_night_light_enabled(command["enabled"] == true)?,
            "network-select" => self.selected = Some(command["network"].clone()),
            "back" => self.selected = None,
            "wifi-connect" => {
                let password = if command["secured"] == true && command["known"] != true {
                    Some(text("password"))
                } else {
                    None
                };
                let okay = Handlers::wlan_connect(string("ssid"), password, false)?;
                self.status = if okay {
                    "已连接"
                } else {
                    "连接失败，请检查密码或系统权限"
                }
                .into();
                if okay {
                    self.selected = None;
                }
            }
            "wifi-disconnect" => {
                Handlers::wlan_disconnect()?;
                self.selected = None;
            }
            "wifi-forget" => {
                Handlers::wlan_forget(string("ssid"))?;
                self.selected = None;
            }
            "wifi-scan" => Handlers::wlan_scan(),
            "bt-connect" => Handlers::connect_bluetooth_device(string("id"))?,
            "bt-disconnect" => Handlers::disconnect_bluetooth_device(string("id"))?,
            "bt-forget" => Handlers::forget_bluetooth_device(string("id")).await?,
            "pair-start" => {
                let id = string("id");
                let action = Handlers::request_pair_bluetooth_device(id.clone()).await?;
                self.pairing = Some((id, serde_json::to_value(action)?));
            }
            "pair-confirm" | "pair-cancel" => {
                Handlers::confirm_bluetooth_device_pairing(
                    string("id"),
                    DevicePairingAnswer {
                        accept: op == "pair-confirm",
                        pin: Some(text("pin")),
                        username: Some(text("username")),
                        password: Some(text("password")),
                        address: Some(text("pin")),
                    },
                )
                .await?;
                self.pairing = None;
            }
            "notification-close" => {
                Handlers::notifications_close(command["id"].as_u64().unwrap_or(0) as u32)?
            }
            "notification-clear" => Handlers::notifications_close_all()?,
            "notification-mode" => {
                Handlers::set_notifications_mode(serde_json::from_value(command["mode"].clone())?)?
            }
            "notification-open" => {
                let id = command["id"].as_u64().unwrap_or(0) as u32;
                let prefix = format!("input:{id}:");
                let inputs = result["fields"]
                    .as_object()
                    .into_iter()
                    .flatten()
                    .filter_map(|(k, v)| {
                        k.strip_prefix(&prefix).map(|k| {
                            (
                                k.to_owned(),
                                v.as_str().unwrap_or("").chars().take(1024).collect(),
                            )
                        })
                    })
                    .collect();
                let activation =
                    serde_json::from_value(command["activation"].clone()).unwrap_or_default();
                Handlers::activate_notification(
                    id,
                    string("umid"),
                    command["args"].as_str().map(str::to_owned),
                    activation,
                    inputs,
                )?;
            }
            "tray" => match result["button"].as_u64().unwrap_or(0) {
                4=>{let id=string("id");let current=Handlers::get_system_tray_icons().into_iter().find(|i|i.registry_key==id).ok_or("Tray icon expired")?;
                    Handlers::set_system_tray_icon_promoted(id,!current.is_promoted)?;}
                5=>{let destination=result["destination"].as_str().ok_or("Missing tray destination")?;
                    let target=self.commands.get(destination).filter(|c|c["op"]=="tray").ok_or("Tray destination expired")?["id"].as_str().ok_or("Missing tray id")?;
                    let mut keys=Handlers::get_system_tray_icons().into_iter().map(|i|i.registry_key).collect::<Vec<_>>();
                    let from=keys.iter().position(|id|id==&string("id")).ok_or("Tray source expired")?;
                    let to=keys.iter().position(|id|id==target).ok_or("Tray destination expired")?;
                    let key=keys.remove(from);keys.insert(to.min(keys.len()),key);Handlers::set_system_tray_icons_order(keys)?;}
                button=>Handlers::send_system_tray_icon_action(string("id"),match button {2=>SystrayIconAction::RightClick,1=>SystrayIconAction::MiddleClick,3=>SystrayIconAction::LeftDoubleClick,_=>SystrayIconAction::LeftClick})?,
            },
            "settings" => super::native_popups::open_settings(match string("page").as_str() {
                "network" => "ms-settings:network-wifi",
                _ => "ms-settings:bluetooth",
            })?,
            "power-view" => self.power_view = true,
            "power-back" => self.power_view = false,
            "power-settings" => Handlers::open_power_settings()?,
            "refresh" => {}
            _ => return Err("Unknown native action".into()),
        }
        Ok(false)
    }
    pub fn events(&self) -> Vec<&'static str> {
        match self.id.as_str() {
            "@seelen/apps-menu" => vec![SeelenEvent::StartMenuItemsChanged,SeelenEvent::UserFolderChanged],
            "@seelen/task-switcher" => vec![SeelenEvent::UserAppWindowsChanged],
            "@seelen/media-popup" => vec![SeelenEvent::MediaDevices, SeelenEvent::MediaSessions],
            "@seelen/network-popup" => vec![
                SeelenEvent::NetworkWlanScanned,
                SeelenEvent::RadiosChanged,
                SeelenEvent::NetworkHotspotChanged,
            ],
            "@seelen/bluetooth-popup" => vec![
                SeelenEvent::BluetoothDevicesChanged,
                SeelenEvent::RadiosChanged,
            ],
            "@seelen/notifications" => vec![
                SeelenEvent::Notifications,
                SeelenEvent::NotificationsModeChanged,
            ],
            "@seelen/system-tray" => vec![SeelenEvent::SystemTrayChanged],
            _ => vec![
                SeelenEvent::MediaDevices,
                SeelenEvent::SystemMonitorsChanged,
                SeelenEvent::RadiosChanged,
                SeelenEvent::DarkModeChanged,
                SeelenEvent::PowerStatus,
                SeelenEvent::PowerMode,
                SeelenEvent::BatteriesStatus,
            ],
        }
    }
}
pub(super) fn toast_text(value: &Value, depth: usize) -> String {
    if depth > 8 {
        return String::new();
    }
    let mut text = String::new();
    match value {
        Value::Object(map) => {
            if let Some(Value::String(s)) = map.get("$value") {
                text.push_str(s);
                text.push(' ');
            }
            for (key, v) in map {
                if key != "$value" {
                    text.push_str(&toast_text(v, depth + 1));
                }
            }
        }
        Value::Array(items) => {
            for item in items.iter().take(32) {
                text.push_str(&toast_text(item, depth + 1));
            }
        }
        _ => {}
    }
    text.chars().take(512).collect()
}
