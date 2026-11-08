use std::{collections::HashSet, os::windows::ffi::OsStrExt, path::{Path, PathBuf}, time::{Duration, Instant}};

use serde::Deserialize;
use windows::{
    core::{implement, Interface, BOOL, HRESULT, PCWSTR},
    Win32::{
        Foundation::{DRAGDROP_S_CANCEL, DRAGDROP_S_DROP, DRAGDROP_S_USEDEFAULTCURSORS, HWND, S_OK},
        System::{
            Com::{CoTaskMemFree, IDataObject},
            Ole::{DoDragDrop, IDropSource, IDropSource_Impl, OleInitialize, OleUninitialize, DROPEFFECT, DROPEFFECT_COPY, DROPEFFECT_MOVE},
            SystemServices::MODIFIERKEYS_FLAGS,
            Threading::{GetCurrentThreadId, GetExitCodeProcess},
        },
        UI::{
            Input::KeyboardAndMouse::{GetAsyncKeyState, VK_ESCAPE, VK_LBUTTON},
            Shell::{BHID_DataObject, Common::ITEMIDLIST, IDataObjectAsyncCapability, SHCreateDataObject, SHParseDisplayName},
            WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId, IsWindow, SetForegroundWindow},
        },
    },
};

use crate::{error::Result, windows_api::WindowsApi};
use super::webview::WidgetWebviewLabel;

#[derive(Deserialize)]
pub(super) struct BridgeSession { pub(super) endpoint: String, pub(super) token: String, pid: u32, pub(super) version: String,
    #[serde(default, rename="nativePopups")] pub(super) native_popups: Vec<String>,
    #[serde(default, rename="nativeRendererPid")] pub(super) native_renderer_pid: u32 }

impl BridgeSession {
    pub(super) fn project_root() -> Result<PathBuf> {
        if slu_ipc::community::enabled() { let path=slu_ipc::community::manifest()?.project_root.to_string_lossy();return Ok(PathBuf::from(path.strip_prefix(r"\\?\").unwrap_or(&path))); }
        // Never accept an endpoint, token, source path or HWND from JavaScript.
        // This command belongs only to our versioned community host checkout.
        let exe = std::env::current_exe()?;
        let directory = exe.parent().ok_or("Missing host directory")?;
        let native = directory.parent().ok_or("Missing native directory")?;
        let profiles = native.parent().ok_or("Missing profiles directory")?;
        let root = profiles.parent().ok_or("Missing project directory")?;
        if native.file_name().and_then(|s| s.to_str()) != Some("native-host") || profiles.file_name().and_then(|s| s.to_str()) != Some("profiles") {
            return Err("Native desktop drag requires the owned community host".into());
        }
        Ok(root.to_path_buf())
    }
    pub(super) fn current() -> Result<Self> {
        let root=Self::project_root()?;
        let session: Self = serde_json::from_slice(&std::fs::read(root.join("runtime/desktop/session.json"))?)?;
        if !valid_app_version(&session.version) || session.token.len() != 64 || !session.token.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err("Invalid desktop session".into());
        }
        // A retained process handle can still reveal the path of an exited adapter.
        // The host starts before the replacement adapter; ignore the old session.
        let selected: serde_json::Value = serde_json::from_slice(&std::fs::read(root.join("app.current.json"))?)?;
        if selected["version"].as_str() != Some(session.version.as_str()) {
            return Err("Desktop session does not match the selected version".into());
        }
        let process = crate::windows_api::process::Process::from_id(session.pid).open_limited_handle()?;
        let mut exit_code = 0;
        unsafe { GetExitCodeProcess(*process, &mut exit_code)?; }
        if exit_code != 259 {
            return Err("Desktop adapter has exited".into());
        }
        let expected = root.join("versions").join(&session.version).join("MagicCommunity.exe");
        let running = WindowsApi::exe_path_by_process(session.pid)?;
        if !expected.to_string_lossy().eq_ignore_ascii_case(&running.to_string_lossy()) {
            return Err("Desktop adapter process identity differs".into());
        }
        validate_endpoint(&session.endpoint)?;
        Ok(session)
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Source {
    source: PathBuf,
    drag_id: String,
    #[serde(default)]
    sources: Vec<PathBuf>,
    #[serde(default)]
    drag_ids: Vec<String>,
}

impl Source {
    fn intent_ids(&self) -> Vec<String> {
        let mut ids = self.drag_ids.clone();
        if !ids.contains(&self.drag_id) { ids.push(self.drag_id.clone()); }
        let mut seen = HashSet::new();
        ids.retain(|id| !id.is_empty() && seen.insert(id.clone()));
        ids
    }

    fn paths(&self, expected: usize) -> Result<Vec<PathBuf>> {
        let paths = if self.sources.is_empty() { vec![self.source.clone()] } else { self.sources.clone() };
        if paths.len() != expected || self.intent_ids().len() != expected || paths.iter().any(|path| !path.is_absolute()) {
            return Err("Desktop drag source/intent selection differs".into());
        }
        Ok(paths)
    }
}

struct OwnedPidl(*mut ITEMIDLIST);

impl OwnedPidl {
    fn from_path(path: &Path) -> Result<Self> {
        let mut wide: Vec<u16> = path.as_os_str().encode_wide().collect();
        if wide.contains(&0) { return Err("Invalid Shell source path".into()); }
        wide.push(0);
        let mut pointer = std::ptr::null_mut();
        let result = unsafe { SHParseDisplayName(PCWSTR(wide.as_ptr()), None, &mut pointer, 0, None) };
        let owned = Self(pointer);
        result?;
        if owned.0.is_null() { return Err("Shell source PIDL is missing".into()); }
        Ok(owned)
    }
}

impl Drop for OwnedPidl {
    fn drop(&mut self) {
        if !self.0.is_null() { unsafe { CoTaskMemFree(Some(self.0.cast())); } }
    }
}

#[implement(IDropSource)]
struct DesktopDropSource { hwnd: isize }

impl IDropSource_Impl for DesktopDropSource_Impl {
    fn QueryContinueDrag(&self, escape: BOOL, _: MODIFIERKEYS_FLAGS) -> HRESULT {
        let alive = unsafe { IsWindow(Some(HWND(self.hwnd as _))).as_bool() };
        let left = unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) as u16 & 0x8000 != 0 };
        let escape = escape.as_bool() || unsafe { GetAsyncKeyState(VK_ESCAPE.0 as i32) as u16 & 0x8000 != 0 };
        drag_status(escape, left, alive)
    }
    fn GiveFeedback(&self, _: DROPEFFECT) -> HRESULT { DRAGDROP_S_USEDEFAULTCURSORS }
}

fn drag_status(escape: bool, left: bool, alive: bool) -> HRESULT {
    if escape || !alive { DRAGDROP_S_CANCEL } else if !left { DRAGDROP_S_DROP } else { S_OK }
}

fn validate_endpoint(value: &str) -> Result<()> {
    let url = reqwest::Url::parse(value).map_err(|_| String::from("Invalid local adapter URL"))?;
    if url.scheme() != "http" || url.host_str() != Some("127.0.0.1") || url.port().is_none() || url.path() != "/" || url.query().is_some() || url.fragment().is_some() || !url.username().is_empty() || url.password().is_some() {
        return Err("Only the owned loopback adapter is allowed".into());
    }
    Ok(())
}

fn selected_item_ids(item_id: String, item_ids: Option<Vec<String>>) -> Result<Vec<String>> {
    let ids = item_ids.unwrap_or_else(|| vec![item_id.clone()]);
    let is_id = |id: &str| id.len() == 64 && id.bytes().all(|byte| byte.is_ascii_hexdigit());
    let mut seen = HashSet::new();
    if !is_id(&item_id) || ids.is_empty() || ids.len() > 256 || !ids.iter().any(|id| id.eq_ignore_ascii_case(&item_id))
        || ids.iter().any(|id| !is_id(id) || !seen.insert(id.to_ascii_lowercase())) {
        return Err("Desktop drag requires 1..256 distinct selected item IDs".into());
    }
    Ok(ids)
}

fn shell_drag_data(paths: &[PathBuf]) -> Result<IDataObject> {
    if paths.is_empty() || paths.len() > 256 { return Err("Invalid Shell drag selection".into()); }
    if paths.len() == 1 {
        // Preserve the established single-item Shell data object and formats.
        let item = WindowsApi::get_shell_item(&paths[0])?;
        return Ok(unsafe { item.BindToHandler(None, &BHID_DataObject)? });
    }
    let pidls: Vec<OwnedPidl> = paths.iter().map(|path| OwnedPidl::from_path(path)).collect::<Result<_>>()?;
    let children: Vec<*const ITEMIDLIST> = pidls.iter().map(|pidl| pidl.0 as *const ITEMIDLIST).collect();
    // The empty Desktop root is the common Shell root for User/Public desktop
    // and virtual-group files. Keep complete PIDLs; last-child IDs from different
    // parents would identify the wrong objects. SHCreateDataObject packages the
    // aggregate (including Shell IDList); never bind a cross-parent array through
    // BHID_SFUIObject, which requires a common immediate parent.
    let desktop_root: u16 = 0;
    Ok(unsafe { SHCreateDataObject(Some((&desktop_root as *const u16).cast()), Some(&children), None::<&IDataObject>)? })
}

fn drag_on_ui_thread(hwnd: isize, paths: &[PathBuf], started: Instant) -> Result<u32> {
    let hwnd = HWND(hwnd as _);
    if unsafe { GetWindowThreadProcessId(hwnd, None) } != unsafe { GetCurrentThreadId() } {
        return Err("Native drag did not reach its source UI thread".into());
    }
    if unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) as u16 & 0x8000 == 0 } { return Ok(0); }
    unsafe { OleInitialize(None)?; }
    struct Ole;
    impl Drop for Ole { fn drop(&mut self) { unsafe { OleUninitialize(); } } }
    let _ole = Ole;
    log::info!("MAGIC_DESKTOP_NATIVE_DRAG stage=bind ui_thread=true count={}", paths.len());
    let data = shell_drag_data(paths)?;
    if let Ok(asynchronous) = data.cast::<IDataObjectAsyncCapability>() { unsafe { asynchronous.SetAsyncMode(false)?; } }
    if unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) as u16 & 0x8000 == 0 } { return Ok(0); }
    // Only the source's own window; never simulate keys or focus another app.
    if unsafe { GetForegroundWindow() } != hwnd && !unsafe { SetForegroundWindow(hwnd).as_bool() } {
        return Err("Windows refused the native drag source focus".into());
    }
    let source: IDropSource = DesktopDropSource { hwnd: hwnd.0 as isize }.into();
    let mut effect = DROPEFFECT::default();
    log::info!("MAGIC_DESKTOP_NATIVE_DRAG stage=loop ui_thread=true source_init_ms={:.2}", started.elapsed().as_secs_f64()*1000.0);
    let result = unsafe { DoDragDrop(&data, &source, DROPEFFECT_COPY | DROPEFFECT_MOVE, &mut effect) };
    log::info!("MAGIC_DESKTOP_NATIVE_DRAG stage=end status={:X} effect={}", result.0, effect.0);
    result.ok()?;
    // The Shell owns physical transfer/source notifications. No manual deletion.
    Ok(if result == DRAGDROP_S_CANCEL { 0 } else { effect.0 })
}

impl crate::tauri_handlers::Handlers {
    pub async fn magic_start_desktop_file_drag(webview: tauri::WebviewWindow, item_id: String, item_ids: Option<Vec<String>>) -> Result<u32> {
        let started = Instant::now();
        let owner = WidgetWebviewLabel::try_from_raw(webview.label())?;
        if owner.widget_id.to_string() != "@magic-community/desktop" { return Err("Only the community desktop can start this drag".into()); }
        let item_ids = selected_item_ids(item_id.clone(), item_ids)?;
        let expected_count = item_ids.len();
        let monitor = owner.monitor_id.ok_or("Missing desktop monitor")?.to_string();
        let hwnd = webview.hwnd()?.0 as isize;
        let session = BridgeSession::current()?;
        let client = reqwest::Client::builder().no_proxy().redirect(reqwest::redirect::Policy::none()).timeout(Duration::from_secs(3)).build()?;
        let source: Source = client.post(format!("{}/desktop-native-drag-source", session.endpoint.trim_end_matches('/'))).bearer_auth(&session.token)
            .json(&serde_json::json!({"monitorId":monitor,"windowId":hwnd,"itemId":item_id,"itemIds":item_ids})).send().await?.error_for_status()?.json().await?;
        // No early returns after receiving intents: even malformed source arrays,
        // dispatch errors, cancellation and failed COM binding finish every intent.
        let result: Result<u32> = match source.paths(expected_count) {
            Ok(paths) => {
                let (sender, receiver) = tokio::sync::oneshot::channel();
                let dispatched = webview.run_on_main_thread(move || { let _ = sender.send(drag_on_ui_thread(hwnd, &paths, started)); });
                match dispatched { Ok(()) => match receiver.await { Ok(value) => value, Err(_) => Err("Native drag callback ended".into()) }, Err(error) => Err(error.into()) }
            }
            Err(error) => Err(error),
        };
        let cleanup = client.post(format!("{}/desktop-native-drag-finish", session.endpoint.trim_end_matches('/'))).bearer_auth(&session.token)
            .json(&serde_json::json!({"monitorId":monitor,"windowId":hwnd,"dragId":source.drag_id,"dragIds":source.intent_ids()})).send().await;
        let cleanup = cleanup.and_then(|response| response.error_for_status());
        if let Err(error) = cleanup {
            log::warn!("MAGIC_DESKTOP_NATIVE_DRAG cleanup_failed count={expected_count} error={error}");
            if result.is_ok() { return Err(error.into()); }
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn source_policy() {
        assert_eq!(drag_status(true, true, true), DRAGDROP_S_CANCEL);
        assert_eq!(drag_status(false, true, false), DRAGDROP_S_CANCEL);
        assert_eq!(drag_status(false, false, true), DRAGDROP_S_DROP);
        assert_eq!(drag_status(false, true, true), S_OK);
    }
    #[test] fn only_exact_loopback_session_url() {
        assert!(validate_endpoint("http://127.0.0.1:12345").is_ok());
        for url in ["https://127.0.0.1:12345", "http://localhost:12345", "http://127.0.0.1:12345/path", "http://127.0.0.1:12345/?token=x", "http://user@127.0.0.1:12345", "http://example.com"] { assert!(validate_endpoint(url).is_err()); }
    }
    #[test] fn native_source_vtable() {
        use windows::core::IUnknown;
        let source: IDropSource = DesktopDropSource { hwnd: 0 }.into();
        let unknown: IUnknown = source.cast().unwrap();
        let roundtrip: IDropSource = unknown.cast().unwrap();
        assert_eq!(unsafe { roundtrip.QueryContinueDrag(false, MODIFIERKEYS_FLAGS(1)) }, DRAGDROP_S_CANCEL);
        assert_eq!(unsafe { roundtrip.GiveFeedback(DROPEFFECT_MOVE) }, DRAGDROP_S_USEDEFAULTCURSORS);
    }
    #[test] fn installed_or_test_process_cannot_use_project_session() {
        assert!(BridgeSession::current().is_err());
    }
    #[test] fn selected_ids_preserve_legacy_and_reject_ambiguous_groups() {
        let first = "a".repeat(64);
        let second = "b".repeat(64);
        assert_eq!(selected_item_ids(first.clone(), None).unwrap(), [first.clone()]);
        assert_eq!(selected_item_ids(first.clone(), Some(vec![second.clone(), first.clone()])).unwrap(), [second.clone(), first.clone()]);
        for ids in [vec![], vec![second], vec![first.clone(), first.clone()], vec![first.clone(), "A".repeat(64)], vec![first.clone(), "bad".to_string()]] {
            assert!(selected_item_ids(first.clone(), Some(ids)).is_err());
        }
        let maximum: Vec<String> = (0..256).map(|index| format!("{index:064x}")).collect();
        assert_eq!(selected_item_ids(maximum[0].clone(), Some(maximum.clone())).unwrap().len(), 256);
        let mut oversized = maximum.clone(); oversized.push(format!("{:064x}", 256));
        assert!(selected_item_ids(maximum[0].clone(), Some(oversized)).is_err());
        assert!(selected_item_ids("g".repeat(64), None).is_err());
    }
    #[test] fn bridge_sources_validate_cardinality_but_keep_all_cleanup_ids() {
        let legacy: Source = serde_json::from_value(serde_json::json!({"source":"C:\\source.txt","dragId":"one"})).unwrap();
        assert_eq!(legacy.paths(1).unwrap(), [PathBuf::from("C:\\source.txt")]);
        assert_eq!(legacy.intent_ids(), ["one"]);
        let group: Source = serde_json::from_value(serde_json::json!({"source":"C:\\source.txt","dragId":"one","sources":["C:\\source.txt","D:\\other.txt"],"dragIds":["one","two"]})).unwrap();
        assert_eq!(group.paths(2).unwrap().len(), 2);
        assert!(group.paths(1).is_err());
        assert_eq!(group.intent_ids(), ["one", "two"]);
        let mismatched: Source = serde_json::from_value(serde_json::json!({"source":"C:\\source.txt","dragId":"one","sources":["C:\\source.txt"],"dragIds":["one","two"]})).unwrap();
        assert!(mismatched.paths(1).is_err());
        assert_eq!(mismatched.intent_ids(), ["one", "two"]);
        let relative: Source = serde_json::from_value(serde_json::json!({"source":"relative.txt","dragId":"one"})).unwrap();
        assert!(relative.paths(1).is_err());
        assert_eq!(relative.intent_ids(), ["one"]);
    }
    #[test] fn cross_parent_shell_data_object_contains_all_paths_without_transfer() {
        // Independent STA only for this non-interactive data-object test. Product
        // DoDragDrop remains on the source WebView's original UI thread.
        std::thread::spawn(|| {
            use windows::Win32::{
                System::{Com::{DVASPECT_CONTENT, FORMATETC, STGMEDIUM, TYMED_HGLOBAL}, Ole::{CF_HDROP, ReleaseStgMedium}},
                UI::Shell::{DragQueryFileW, HDROP},
            };
            struct Files { root: PathBuf, paths: Vec<PathBuf> }
            impl Drop for Files {
                fn drop(&mut self) {
                    for path in &self.paths { let _ = std::fs::remove_file(path); }
                    for name in ["one", "two"] { let _ = std::fs::remove_dir(self.root.join(name)); }
                    let _ = std::fs::remove_dir(&self.root);
                }
            }
            struct Ole;
            impl Drop for Ole { fn drop(&mut self) { unsafe { OleUninitialize(); } } }
            struct Medium(STGMEDIUM);
            impl Drop for Medium { fn drop(&mut self) { unsafe { ReleaseStgMedium(&mut self.0); } } }
            let mut files = Files { root: std::env::temp_dir().join(format!("magic-native-multi-drag-{}", uuid::Uuid::new_v4())), paths: Vec::new() };
            std::fs::create_dir(&files.root).unwrap();
            for name in ["one", "two"] {
                let parent = files.root.join(name); std::fs::create_dir(&parent).unwrap();
                let path = parent.join("same-name.txt"); std::fs::write(&path, name).unwrap(); files.paths.push(path);
            }
            unsafe { OleInitialize(None).unwrap(); }
            let _ole = Ole;
            let data = shell_drag_data(&files.paths).unwrap();
            let format = FORMATETC { cfFormat: CF_HDROP.0, ptd: std::ptr::null_mut(), dwAspect: DVASPECT_CONTENT.0, lindex: -1, tymed: TYMED_HGLOBAL.0 as u32 };
            let medium = Medium(unsafe { data.GetData(&format).unwrap() });
            let drop = HDROP(unsafe { medium.0.u.hGlobal.0 });
            assert_eq!(unsafe { DragQueryFileW(drop, u32::MAX, None) }, 2);
            let mut actual = Vec::new();
            for index in 0..2 {
                let length = unsafe { DragQueryFileW(drop, index, None) };
                let mut buffer = vec![0; length as usize + 1];
                let copied = unsafe { DragQueryFileW(drop, index, Some(&mut buffer)) };
                actual.push(PathBuf::from(String::from_utf16(&buffer[..copied as usize]).unwrap()));
            }
            // The Shell expands Windows 8.3 TEMP aliases (LITTLE~1) to their
            // long form. Compare filesystem identity, not spelling of the path.
            let canonical = |paths: &[PathBuf]| paths.iter().map(|path| std::fs::canonicalize(path).unwrap()).collect::<Vec<_>>();
            assert_eq!(canonical(&actual), canonical(&files.paths));
            for (index, path) in files.paths.iter().enumerate() {
                assert_eq!(std::fs::read_to_string(path).unwrap(), if index == 0 { "one" } else { "two" });
            }
            // No clipboard publication, DoDragDrop, target Drop, or real file move.
        }).join().unwrap();
    }
}

// Version directories are deployment inputs; backend protocol compatibility is independent.
fn valid_app_version(value:&str)->bool{
    if value.len()>64{return false;}
    let (core,suffix)=value.split_once('-').map_or((value,None),|(a,b)|(a,Some(b)));
    let numbers=core.split('.').collect::<Vec<_>>();
    numbers.len()==3&&numbers.iter().all(|n|!n.is_empty()&&n.bytes().all(|b|b.is_ascii_digit()))&&
    suffix.is_none_or(|s|!s.is_empty()&&s.split('.').all(|part|!part.is_empty()&&part.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'-')))
}
