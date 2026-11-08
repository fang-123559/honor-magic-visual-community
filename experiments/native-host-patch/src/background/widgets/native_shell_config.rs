//! Native bar editing uses the existing state managers and widget permissions.
use super::*;
use crate::state::application::TOOLBAR_ITEMS_MANAGER;
use seelen_core::state::value::{KnownPlugin, PluginValue};
use seelen_core::state::{ToolbarState, WegItems};

pub(super) fn native_toolbar_item(item: &Value) -> bool {
    item.as_str().is_some_and(|id| TB.contains(&id))
        || (item["onClick"].is_null()
            && item["onWheelUp"].is_null()
            && item["onWheelDown"].is_null()
            && item["render"].is_null()
            && item["badge"].is_null()
            && item["tooltip"].is_null()
            && item["style"].as_object().is_none_or(|m| m.is_empty())
            && item["remoteData"].as_object().is_none_or(|m| m.is_empty())
            && item["template"].as_str().is_some_and(|s| {
                s == r#"return "|""#
                    || s == r#"return " ""#
                    || s == r#"return focusedApp.title ? "-" : """#
            }))
}
fn excluded_workspace(id: &str) -> bool {
    matches!(id, "@default/workspaces-numbered" | "@default/workspaces-named" | "@default/workspaces-dotted")
}
pub(super) fn catalog(dock: bool) -> Value {
    let target = if dock {
        "@seelen/weg"
    } else {
        "@seelen/fancy-toolbar"
    };
    let language = FULL_STATE.load().settings.language.clone();
    let values=Handlers::state_get_plugins().into_iter().filter_map(|plugin| {
        let matches=match &plugin.plugin {
            PluginValue::Known(value)=>matches!((dock,value.as_ref()),(true,KnownPlugin::Weg(_))|(false,KnownPlugin::FacyToolbar(_))),
            PluginValue::Any(value)=>value.target().to_string()==target,
        };
        if !matches{return None;}
        let id=plugin.id.to_string();
        if !dock && excluded_workspace(&id) { return None; }
        Some(json!({"id":id,"label":plugin.metadata.display_name.get(&language),"icon":plugin.icon,"native":if dock {DOCK.contains(&id.as_str())}else{TB.contains(&id.as_str())}}))
    }).take(128).collect::<Vec<_>>();
    json!(values)
}
// Resolve app-specific entries from the same active packs as Seelen. Extension,
// redirect and mask theme semantics remain in the compatibility renderer.
pub(super) fn app_icon_entry(item: &Value, dark: bool) -> Value {
    use seelen_core::state::IconPackEntry;
    let path = item["path"].as_str().unwrap_or("");
    let umid = item["umid"].as_str();
    for id in FULL_STATE.load().settings.active_icon_packs.iter().rev() {
        let found = crate::resources::RESOURCES.icon_packs.read_sync(id, |_, pack| {
            let matched = pack.entries.iter().find(|entry| matches!(entry,
                IconPackEntry::Unique(entry) if umid.is_some() && entry.umid.as_deref() == umid))
                .or_else(|| pack.entries.iter().find(|entry| matches!(entry,
                    IconPackEntry::Unique(entry) if entry.path.as_ref().is_some_and(|p| p.to_string_lossy().eq_ignore_ascii_case(path)))));
            let IconPackEntry::Unique(entry) = matched? else { return None; };
            let mut value = serde_json::to_value(matched?).ok()?;
            if let Some(icon) = &entry.icon {
                for (key, image) in [("base", &icon.base), ("light", &icon.light), ("dark", &icon.dark)] {
                    if let Some(image) = image {
                        let image = std::path::Path::new(image);
                        let image = if image.is_absolute() { image.to_path_buf() } else { pack.metadata.internal.path.join(image) };
                        value["icon"][key] = json!(image);
                    }
                }
                let selected = if dark { "dark" } else { "light" };
                value["resolvedImage"] = value["icon"][selected].as_str().or(value["icon"]["base"].as_str()).map(|s| json!(s)).unwrap_or(Value::Null);
            }
            Some(value)
        }).flatten();
        if let Some(entry) = found { return entry; }
    }
    json!({"type":"unique","path":item["path"],"umid":item["umid"]})
}

pub(super) fn offered_window(command: &Value, target: Option<i64>) -> Result<Option<i64>> {
    let windows = command["windows"].as_array();
    if let Some(target) = target.filter(|v| *v != 0) {
        if !windows.is_some_and(|rows| rows.iter().any(|w| w["hwnd"].as_i64() == Some(target))) {
            return Err("Window preview target expired".into());
        }
        return Ok(Some(target));
    }
    Ok(windows
        .and_then(|rows| rows.first())
        .and_then(|w| w["hwnd"].as_i64()))
}
fn identity(item: &Value) -> &str {
    item.as_str().or_else(|| item["id"].as_str()).unwrap_or("")
}
pub(super) fn execute(
    dock: bool,
    item: Option<&str>,
    value: &Value,
    offered: &HashMap<String, Value>,
) -> Result<()> {
    let widget = if dock {
        "@seelen/weg"
    } else {
        "@seelen/fancy-toolbar"
    };
    let verb = value["verb"].as_str().unwrap_or("");
    // Community settings are handled by the authenticated desktop adapter,
    // using the same callback as the desktop context menu. Never open Seelen
    // or Windows settings as a fallback for this menu action.
    if verb == "settings" { return Err("Community settings must use the desktop adapter".into()); }
    if verb == "show-desktop" {
        super::super::permissions::WIDGET_PERMISSIONS.request(&widget.into(),WidgetPerm::ManageAppWindows)?;
        return crate::windows_api::Com::run_with_context(|| {
            use windows::Win32::UI::Shell::{IShellDispatch6,Shell};
            let shell:IShellDispatch6=crate::windows_api::Com::create_instance(&Shell)?;
            unsafe {shell.ToggleDesktop()?;} Ok(())
        });
    }
    if verb == "task-manager" {
        super::super::permissions::WIDGET_PERMISSIONS
            .request(&widget.into(), WidgetPerm::OpenFile)?;
        WindowsApi::execute("Taskmgr.exe".into(), None, None, false)?;
        return Ok(());
    }
    super::super::permissions::WIDGET_PERMISSIONS
        .request(&widget.into(), WidgetPerm::ModifyConfiguration)?;
    if dock && verb == "pin-file" {
        use tauri_plugin_dialog::DialogExt;
        if let Some(files)=crate::app::get_app_handle().dialog().file().blocking_pick_files(){
            let mut items=WEG_ITEMS_MANAGER.get();
            for file in files.into_iter().take(128){let path=file.into_path().map_err(|_|"Selected file has no local path")?;if !path.is_file(){return Err("Selected file no longer exists".into());}
                let umid=WindowsApi::get_file_umid(&path).ok();let mut found=false;
                for group in [&mut items.left,&mut items.center,&mut items.right] {for item in group {if let WegItem::AppOrFile(data)=item {if (umid.is_some()&&data.umid==umid)||(umid.is_none()&&data.path.to_string_lossy().eq_ignore_ascii_case(&path.to_string_lossy())){data.pinned=true;found=true;}}}}
                if found {continue;}
                let display_name=WindowsApi::get_executable_display_name(&path).unwrap_or_else(|_|path.file_stem().unwrap_or_default().to_string_lossy().to_string());
                items.center.push(WegItem::AppOrFile(WegItemData{id:uuid::Uuid::new_v4(),path,umid,display_name,pinned:true,prevent_pinning:false,relaunch:None}));
            }
            WEG_ITEMS_MANAGER.write(items)?;
        }
        return Ok(());
    }
    let mut state = if dock {
        serde_json::to_value(WEG_ITEMS_MANAGER.get())?
    } else {
        serde_json::to_value(TOOLBAR_ITEMS_MANAGER.get())?
    };
    if dock && verb == "reorder" && item == value["destination"].as_str() {
        return Ok(());
    }
    let mut edit = value.clone();
    if dock&&verb=="order" {
        let list=edit["order"].as_array().filter(|v|v.len()<=290).ok_or("Invalid dock order")?.clone();
        let mut projected=Vec::new();
        for key in list {let key=key.as_str().ok_or("Invalid dock identity")?;
            if key.starts_with("running:") {
                let mut row=offered.get(key).filter(|v|v["op"]=="dock").ok_or("Running item expired")?["item"].clone();
                let id=uuid::Uuid::new_v4().to_string();row["id"]=json!(id);row["pinned"]=json!(false);if row["path"].is_null(){row["path"]=json!("");}
                state["center"].as_array_mut().unwrap().push(row);projected.push(json!(id));
            }else{projected.push(json!(key));}
        }
        edit["order"]=json!(projected);
    }

    if dock && verb == "separator" {
        let destination=edit["destination"].as_str().unwrap_or("").to_owned();
        if let Some((side,key))=destination.split_once(':') {
            if !["before","after"].contains(&side){return Err("Invalid separator position".into());}
            if key.starts_with("running:") {
                let mut row=offered.get(key).filter(|v|v["op"]=="dock").ok_or("Running item expired")?["item"].clone();let id=uuid::Uuid::new_v4().to_string();row["id"]=json!(id);row["pinned"]=json!(false);if row["path"].is_null(){row["path"]=json!("");}state["center"].as_array_mut().unwrap().push(row);edit["destination"]=json!(format!("{side}:{id}"));
            }
        }
    }
    let mut source = item.unwrap_or("").to_owned();
    if dock
        && matches!(
            verb,
            "move-left"
                | "move-right"
                | "section-left"
                | "section-center"
                | "section-right"
                | "reorder"
        )
    {
        let destination = value["destination"].as_str().unwrap_or("").to_owned();
        for key in [source.clone(), destination] {
            if !key.starts_with("running:") {
                continue;
            }
            let mut row = offered
                .get(&key)
                .filter(|v| v["op"] == "dock")
                .ok_or("Running item expired")?["item"]
                .clone();
            let id = uuid::Uuid::new_v4().to_string();
            row["id"] = json!(id);
            row["pinned"] = json!(false);
            if row["path"].is_null() {
                row["path"] = json!("");
            }
            state["center"].as_array_mut().unwrap().push(row);
            if source == key {
                source = id.clone();
            }
            if edit["destination"] == key {
                edit["destination"] = json!(id);
            }
        }
    }
    apply(&mut state, dock, Some(&source), &edit, &catalog(dock))?;
    if dock {
        WEG_ITEMS_MANAGER.write(serde_json::from_value::<WegItems>(state)?)?;
    } else {
        TOOLBAR_ITEMS_MANAGER.write(serde_json::from_value::<ToolbarState>(state)?)?;
    }
    // Executable third-party modules retain only the existing component WebView.
    if !super::selection().contains(widget) {
        if let Some(deploy) = super::super::manager::WIDGET_MANAGER
            .deployments
            .get(&widget.into(), |d| d.clone())
        {
            deploy.start_all_webviews();
        }
    }
    Ok(())
}

fn apply(
    state: &mut Value,
    dock: bool,
    item: Option<&str>,
    value: &Value,
    modules: &Value,
) -> Result<()> {
    let verb = value["verb"].as_str().unwrap_or("");
    let id = item.unwrap_or("");
    let raw_destination = value["destination"].as_str().unwrap_or("");
    let destination = if dock {
        raw_destination
    } else {
        raw_destination
            .strip_prefix("toolbar:")
            .unwrap_or(raw_destination)
    };
    if verb == "lock" {
        state["isReorderDisabled"] = json!(!state["isReorderDisabled"].as_bool().unwrap_or(false));
    } else if verb == "order" {
        if state["isReorderDisabled"]==true{return Err("Bar ordering is locked".into());}
        let order=value["order"].as_array().filter(|a|a.len()<=290).ok_or("Invalid toolbar order")?;
        let mut existing=HashMap::new();for group in ["left","center","right"] {for row in state[group].as_array().into_iter().flatten(){let id=if dock{identity(row).to_owned()}else{format!("toolbar:{}",identity(row))};if existing.insert(id,row.clone()).is_some(){return Err("Duplicate toolbar identity".into());}}}
        if order.len()!=existing.len()+2{return Err("Toolbar order expired".into());}
        let mut seen=HashSet::new();let mut boundaries=Vec::new();
        for (index,id) in order.iter().enumerate(){let id=id.as_str().ok_or("Invalid toolbar identity")?;
            if !seen.insert(id){return Err("Duplicate toolbar identity".into());}
            if ["@boundary:left","@boundary:right"].contains(&id){boundaries.push(index);}else if !existing.contains_key(id){return Err("Toolbar item expired".into());}
        }
        if boundaries.len()!=2{return Err("Missing toolbar boundary".into());}
        for group in ["left","center","right"] {state[group]=json!([]);}
        for (index,id) in order.iter().enumerate(){if boundaries.contains(&index){continue;}let group=if index<boundaries[0]{"left"}else if index<boundaries[1]{"center"}else{"right"};state[group].as_array_mut().unwrap().push(existing.remove(id.as_str().unwrap()).ok_or("Toolbar item expired")?);}
    } else if verb == "restore" && !dock {
        *state = serde_json::to_value(crate::state::application::default_toolbar_items())?;
        for group in ["left", "center", "right"] {
            state[group].as_array_mut().unwrap().retain(|v| !excluded_workspace(identity(v)));
        }
    } else if verb == "toggle-module" {
        if !modules
            .as_array()
            .is_some_and(|a| a.iter().any(|p| p["id"] == destination))
        {
            return Err("Module is no longer installed".into());
        }
        let present = ["left", "center", "right"].iter().any(|g| {
            state[*g].as_array().is_some_and(|a| {
                a.iter().any(|v| {
                    if dock {
                        v["plugin"] == destination
                    } else {
                        v.as_str() == Some(destination)
                    }
                })
            })
        });
        if present {
            for group in ["left", "center", "right"] {
                state[group].as_array_mut().unwrap().retain(|v| {
                    if dock {
                        v["plugin"] != destination
                    } else {
                        v.as_str() != Some(destination)
                    }
                });
            }
        } else {
            state["right"]
                .as_array_mut()
                .unwrap()
                .push(if dock {
                    json!({"type":"Plugin","id":uuid::Uuid::new_v4(),"plugin":destination})
                } else {
                    json!(destination)
                });
        }
    } else if verb=="toggle-media"&&dock {
        let present=["left","center","right"].iter().any(|g|state[*g].as_array().is_some_and(|v|v.iter().any(|v|v["type"]=="Media")));
        if present {for group in ["left","center","right"] {state[group].as_array_mut().unwrap().retain(|v|v["type"]!="Media");}}
        else {state["right"].as_array_mut().unwrap().push(json!({"type":"Media","id":uuid::Uuid::new_v4()}));}
    } else if verb == "separator" && dock {
        let mut section="center";let mut position=state[section].as_array().unwrap().len();
        if let Some((side,key))=destination.split_once(':') {
            let after=match side {"after"=>true,"before"=>false,_=>return Err("Invalid separator position".into())};
            if key=="@boundary:left" {section=if after{"center"}else{"left"};position=if after{0}else{state[section].as_array().unwrap().len()};}
            else if key=="@boundary:right" {section=if after{"right"}else{"center"};position=if after{0}else{state[section].as_array().unwrap().len()};}
            else {let mut found=false;for group in ["left","center","right"] {if let Some(index)=state[group].as_array().unwrap().iter().position(|row|identity(row)==key){section=group;position=index+usize::from(after);found=true;break;}}if !found{return Err("Separator target expired".into());}}
        } else if !destination.is_empty(){return Err("Invalid separator position".into());}
        state[section].as_array_mut().unwrap().insert(position,json!({"type":"Separator","id":uuid::Uuid::new_v4()}));
    } else if matches!(
        verb,
        "remove"
            | "move-left"
            | "move-right"
            | "section-left"
            | "section-center"
            | "section-right"
            | "reorder"
    ) {
        if verb == "reorder" && state["isReorderDisabled"] == true {
            return Err("Bar ordering is locked".into());
        }
        let source = ["left", "center", "right"]
            .into_iter()
            .find_map(|g| {
                state[g]
                    .as_array()
                    .and_then(|a| a.iter().position(|v| identity(v) == id))
                    .map(|i| (g, i))
            })
            .ok_or("Bar item expired")?;
        if verb == "move-left" || verb == "move-right" {
            let a = state[source.0].as_array_mut().unwrap();
            let to = if verb == "move-left" {
                source.1.saturating_sub(1)
            } else {
                (source.1 + 1).min(a.len() - 1)
            };
            let v = a.remove(source.1);
            a.insert(to, v);
        } else if verb == "reorder" && !dock && destination.starts_with("toolbar-drop:") {
            let mut parts=destination.split(':');parts.next();let group=parts.next().ok_or("Missing destination section")?;
            if !["left","center","right"].contains(&group){return Err("Invalid destination section".into());}
            let index=parts.next().ok_or("Missing insertion index")?.parse::<usize>()?;
            if parts.next().is_some(){return Err("Invalid insertion target".into());}
            let length=state[group].as_array().ok_or("Missing section")?.len()-usize::from(source.0==group);
            if index>length{return Err("Insertion target expired".into());}
            let item=state[source.0].as_array_mut().unwrap().remove(source.1);state[group].as_array_mut().unwrap().insert(index,item);
        } else if verb == "reorder" {
            let dest = ["left", "center", "right"]
                .into_iter()
                .find_map(|g| {
                    state[g]
                        .as_array()
                        .and_then(|a| a.iter().position(|v| identity(v) == destination))
                        .map(|i| (g, i))
                })
                .ok_or("Destination expired")?;
            let v = state[source.0].as_array_mut().unwrap().remove(source.1);
            let a = state[dest.0].as_array_mut().unwrap();
            a.insert(dest.1.min(a.len()), v);
        } else {
            let v = state[source.0].as_array_mut().unwrap().remove(source.1);
            if let Some(group) = verb.strip_prefix("section-") {
                state[group].as_array_mut().unwrap().push(v);
            }
        }
    } else {
        return Err("Unknown bar edit".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> Value {
        json!({"left":["a","b","third-party"],"center":["c"],"right":["d"],"isReorderDisabled":false,"unrelated":{"retain":true}})
    }
    #[test]
    fn explicit_preview_target_never_falls_back_to_another_window() {
        let command = json!({"windows":[{"hwnd":7},{"hwnd":8}]});
        assert_eq!(offered_window(&command, Some(8)).unwrap(), Some(8));
        assert!(offered_window(&command, Some(9)).is_err());
        assert_eq!(offered_window(&command, Some(0)).unwrap(), Some(7));
    }
    #[test]
    fn executable_inline_templates_keep_compatibility() {
        let mut item = json!({"template":r#"return "|""#});
        assert!(native_toolbar_item(&item));
        item["onClick"] = json!("return customAction()");
        assert!(!native_toolbar_item(&item));
    }
    #[test]
    fn toolbar_reorder_uses_offered_prefix_and_retains_plugins() {
        let mut v = fixture();
        apply(
            &mut v,
            false,
            Some("a"),
            &json!({"verb":"reorder","destination":"toolbar:c"}),
            &json!([]),
        )
        .unwrap();
        assert_eq!(v["left"], json!(["b", "third-party"]));
        assert_eq!(v["center"], json!(["a", "c"]));
        assert_eq!(v["unrelated"]["retain"], true);
    }
    #[test]
    fn same_group_reorder_moves_instead_of_swapping() {
        let mut v = fixture();
        apply(
            &mut v,
            false,
            Some("a"),
            &json!({"verb":"reorder","destination":"toolbar:third-party"}),
            &json!([]),
        )
        .unwrap();
        assert_eq!(v["left"], json!(["b", "third-party", "a"]));
    }
    #[test]
    fn lock_rejects_drag_without_mutation() {
        let mut v = fixture();
        v["isReorderDisabled"] = json!(true);
        let old = v.clone();
        assert!(
            apply(
                &mut v,
                false,
                Some("a"),
                &json!({"verb":"reorder","destination":"toolbar:c"}),
                &json!([])
            )
            .is_err()
        );
        assert_eq!(v, old);
    }
    #[test]
    fn move_section_and_remove_only_selected() {
        let mut v = fixture();
        apply(
            &mut v,
            false,
            Some("b"),
            &json!({"verb":"section-right"}),
            &json!([]),
        )
        .unwrap();
        assert_eq!(v["right"], json!(["d", "b"]));
        apply(
            &mut v,
            false,
            Some("b"),
            &json!({"verb":"remove"}),
            &json!([]),
        )
        .unwrap();
        assert_eq!(v["left"], json!(["a", "third-party"]));
    }
    #[test]
    fn missing_destination_preserves_everything() {
        let mut v = fixture();
        let old = v.clone();
        assert!(
            apply(
                &mut v,
                false,
                Some("a"),
                &json!({"verb":"reorder","destination":"gone"}),
                &json!([])
            )
            .is_err()
        );
        assert_eq!(v, old);
    }
    #[test]
    fn catalog_validation_and_toggle_preserve_external_order() {
        let mut v = fixture();
        let old = v.clone();
        assert!(
            apply(
                &mut v,
                false,
                None,
                &json!({"verb":"toggle-module","destination":"unknown"}),
                &json!([])
            )
            .is_err()
        );
        assert_eq!(v, old);
        apply(
            &mut v,
            false,
            None,
            &json!({"verb":"toggle-module","destination":"external"}),
            &json!([{"id":"external"}]),
        )
        .unwrap();
        assert_eq!(v["right"], json!(["d", "external"]));
    }
    #[test]
    fn restore_reuses_authoritative_defaults() {
        let mut v = fixture();
        apply(&mut v, false, None, &json!({"verb":"restore"}), &json!([])).unwrap();
        let typed: ToolbarState = serde_json::from_value(v).unwrap();
        assert_eq!(typed.left.len(), 5);
        assert_eq!(typed.right.len(), 8);
    }
    #[test]
    fn predicted_gap_supports_empty_section_and_exact_insertion() {
        let mut v=fixture();v["right"]=json!([]);
        apply(&mut v,false,Some("a"),&json!({"verb":"reorder","destination":"toolbar-drop:right:0"}),&json!([])).unwrap();
        assert_eq!(v["right"],json!(["a"]));assert_eq!(v["left"],json!(["b","third-party"]));
        apply(&mut v,false,Some("third-party"),&json!({"verb":"reorder","destination":"toolbar-drop:center:1"}),&json!([])).unwrap();
        assert_eq!(v["center"],json!(["c","third-party"]));assert_eq!(v["unrelated"]["retain"],true);
    }
    #[test]
    fn predicted_gap_rejects_stale_index_and_locked_order() {
        let mut v=fixture();let before=v.clone();
        assert!(apply(&mut v,false,Some("a"),&json!({"verb":"reorder","destination":"toolbar-drop:right:99"}),&json!([])).is_err());assert_eq!(v,before);
        v["isReorderDisabled"]=json!(true);let before=v.clone();
        assert!(apply(&mut v,false,Some("a"),&json!({"verb":"reorder","destination":"toolbar-drop:right:0"}),&json!([])).is_err());assert_eq!(v,before);
    }

}
