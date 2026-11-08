//! Community runtime identity. Legacy builds never consult these files or pipes.
//! The protected installation manifest is an input; processes cannot mint acceptance evidence.
use crate::error::Result;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    io::{Error, ErrorKind, Read},
    path::{Path, PathBuf},
    sync::OnceLock,
};
use windows::{
    Win32::{
        Foundation::{CloseHandle, HLOCAL, HMODULE, LocalFree},
        Security::{
            ACCESS_ALLOWED_ACE, ACE_HEADER, ACL,
            Authorization::{ConvertSidToStringSidW, GetNamedSecurityInfoW, SE_FILE_OBJECT},
            DACL_SECURITY_INFORMATION, GetAce, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR,
            PSID,
        },
        System::{
            LibraryLoader::{
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, GetModuleFileNameW,
                GetModuleHandleExW,
            },
            Threading::{
                OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
                QueryFullProcessImageNameW,
            },
        },
    },
    core::{PCWSTR, PWSTR},
};
pub const PROTOCOL: &str = "Magic.System/1";
pub const DATA_NAMESPACE: &str = "MagicCommunity/System";
pub const CORE_CAPABILITIES: [&str; 6] = [
    "system-data",
    "window-tracking",
    "tray",
    "notifications",
    "taskbar",
    "configuration",
];
pub fn enabled() -> bool {
    cfg!(feature = "community-system")
}
fn denied(text: impl Into<String>) -> crate::error::Error {
    Error::new(ErrorKind::PermissionDenied, text.into()).into()
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CapabilityEvidence {
    pub provider_sha256: String,
    pub evidence_path: String,
    pub evidence_sha256: String,
    pub status: String,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RuntimeManifest {
    pub schema_version: u32,
    pub protocol: String,
    pub instance_id: String,
    pub project_root: PathBuf,
    pub data_directory: PathBuf,
    pub files: BTreeMap<String, String>,
    pub capabilities: BTreeMap<String, CapabilityEvidence>,
}
static MANIFEST: OnceLock<std::result::Result<RuntimeManifest, String>> = OnceLock::new();
pub fn module_path() -> Result<PathBuf> {
    unsafe {
        let mut module = HMODULE::default();
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            PCWSTR(module_path as *const () as *const u16),
            &mut module,
        )?;
        let mut buf = vec![0u16; 32768];
        let n = GetModuleFileNameW(Some(module), &mut buf) as usize;
        if n == 0 || n >= buf.len() {
            return Err(denied("Cannot resolve owned runtime module"));
        }
        Ok(PathBuf::from(String::from_utf16_lossy(&buf[..n])))
    }
}
pub fn hash(path: &Path) -> Result<String> {
    let mut file = std::fs::File::open(path)?;
    if file.metadata()?.len() > 256 * 1024 * 1024 {
        return Err(denied("Runtime file exceeds bound"));
    }
    let mut state = Sha256::new();
    let mut buf = [0u8; 65536];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        state.update(&buf[..n]);
    }
    Ok(format!("{:x}", state.finalize()))
}
pub fn plain(path: &Path) -> Result<PathBuf> {
    use std::os::windows::fs::MetadataExt;
    let full = std::path::absolute(path)?;
    for ancestor in full.ancestors() {
        if std::fs::symlink_metadata(ancestor)?.file_attributes() & 0x400 != 0 {
            return Err(denied("Runtime path contains a reparse point"));
        }
    }
    Ok(full.canonicalize()?)
}
fn sid_text(sid: PSID) -> Result<String> {
    unsafe {
        let mut p = PWSTR::null();
        ConvertSidToStringSidW(sid, &mut p)?;
        let result = p.to_string();
        LocalFree(Some(HLOCAL(p.0.cast())));
        Ok(result.map_err(std::io::Error::other)?)
    }
}
pub fn protected(path: &Path) -> Result<()> {
    unsafe {
        let name = path
            .as_os_str()
            .to_string_lossy()
            .encode_utf16()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let mut owner = PSID::default();
        let mut dacl: *mut ACL = std::ptr::null_mut();
        let mut sd = PSECURITY_DESCRIPTOR::default();
        GetNamedSecurityInfoW(
            PCWSTR(name.as_ptr()),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            Some(&mut owner),
            None,
            Some(&mut dacl),
            None,
            &mut sd,
        )
        .ok()?;
        struct Free(PSECURITY_DESCRIPTOR);
        impl Drop for Free {
            fn drop(&mut self) {
                unsafe {
                    LocalFree(Some(HLOCAL(self.0.0)));
                }
            }
        }
        let _free = Free(sd);
        let allowed = |s: &str| s == "S-1-5-18" || s == "S-1-5-32-544";
        if !allowed(&sid_text(owner)?) || dacl.is_null() {
            return Err(denied(
                "Runtime must be administrator-owned with a non-null DACL",
            ));
        }
        for index in 0..(*dacl).AceCount as u32 {
            let mut ace = std::ptr::null_mut();
            GetAce(dacl, index, &mut ace)?;
            let head = &*(ace as *const ACE_HEADER);
            // Installer grants ordinary users read/execute only. Reject unknown allow ACE shapes.
            if head.AceType == 0 {
                let item = &*(ace as *const ACCESS_ALLOWED_ACE);
                let sid = PSID((&item.SidStart as *const u32).cast_mut().cast());
                if item.Mask & 0x500d0156 != 0 && !allowed(&sid_text(sid)?) {
                    return Err(denied("Runtime grants a non-administrator write access"));
                }
            } else if head.AceType != 1 && head.AceType != 2 {
                return Err(denied("Runtime has an unsupported ACL entry"));
            }
        }
        Ok(())
    }
}
fn relative(root: &Path, value: &str) -> Result<PathBuf> {
    use std::path::Component;
    let path = Path::new(value);
    if path.is_absolute() || !path.components().all(|c| matches!(c, Component::Normal(_))) {
        return Err(denied("Runtime relative path escaped installation"));
    }
    let result = plain(&root.join(path))?;
    if !result.starts_with(root) {
        return Err(denied("Runtime resolved outside installation"));
    }
    protected(&result)?;
    Ok(result)
}
fn load() -> Result<RuntimeManifest> {
    let module = plain(&module_path()?)?;
    let folder = module
        .parent()
        .ok_or_else(|| denied("Missing runtime directory"))?;
    let program = plain(
        &PathBuf::from(
            std::env::var_os("ProgramFiles").ok_or_else(|| denied("ProgramFiles unavailable"))?,
        )
        .join("MagicCommunity/SystemRuntime"),
    )?;
    if !folder.starts_with(&program) || folder == program {
        return Err(denied(
            "Independent runtime requires a protected versioned installation",
        ));
    }
    for ancestor in folder.ancestors() {
        protected(ancestor)?;
        if Some(ancestor) == program.parent() {
            break;
        }
    }
    let file = relative(folder, "magic-system-runtime.json")?;
    let bytes = std::fs::read(file)?;
    if bytes.len() > 512 * 1024 {
        return Err(denied("Runtime manifest exceeds bound"));
    }
    let mut m: RuntimeManifest =
        serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes))?;
    if m.schema_version != 1
        || m.protocol != PROTOCOL
        || m.instance_id.len() != 32
        || !m.instance_id.bytes().all(|b| b.is_ascii_hexdigit())
        || m.files.len() > 4096
    {
        return Err(denied("Invalid independent runtime identity"));
    }
    let project = plain(&m.project_root)?;
    let data = if m.data_directory.exists() {
        plain(&m.data_directory)?
    } else {
        plain(
            m.data_directory
                .parent()
                .ok_or_else(|| denied("Missing data parent"))?,
        )?
        .join(
            m.data_directory
                .file_name()
                .ok_or_else(|| denied("Missing data name"))?,
        )
    };
    if data != project.join("data/system") {
        return Err(denied("Independent configuration namespace differs"));
    }
    m.project_root = project;
    m.data_directory = data;
    for name in [
        "magic-system-host.exe",
        "magic-system-service.exe",
        "magic-system-ctl.exe",
        "sluhk.dll",
    ] {
        if !m.files.contains_key(name) {
            return Err(denied("Incomplete independent runtime"));
        }
    }
    for (name, expected) in &m.files {
        if expected.len() != 64
            || !expected.bytes().all(|b| b.is_ascii_hexdigit())
            || Path::new(name).is_absolute()
            || !Path::new(name)
                .components()
                .all(|c| matches!(c, std::path::Component::Normal(_)))
        {
            return Err(denied(format!("Invalid runtime payload identity: {name}")));
        }
    }
    // Each role verifies its own module now; other images are verified when actually used.
    // The launcher validates the complete immutable payload before takeover. In particular,
    // the Explorer hook must not hash thousands of static assets on its tray thread.
    let own = module
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or_else(|| denied("Invalid module name"))?;
    if m.files.get(own).map(String::as_str) != Some(hash(&module)?.as_str()) {
        return Err(denied("Current module is not in the protected manifest"));
    }
    Ok(m)
}
pub fn manifest() -> Result<&'static RuntimeManifest> {
    match MANIFEST.get_or_init(|| load().map_err(|e| e.to_string())) {
        Ok(m) => Ok(m),
        Err(e) => Err(denied(e.clone())),
    }
}
pub fn pipe(kind: &str, session: u32) -> Result<String> {
    let m = manifest()?;
    if !["host", "service"].contains(&kind) {
        return Err(denied("Unknown IPC role"));
    }
    Ok(format!(
        r"\\.\pipe\magic-community-{}-{}-{}",
        m.instance_id, kind, session
    ))
}
fn resolve_executable(role: &str) -> Result<PathBuf> {
    let folder = module_path()?
        .parent()
        .ok_or_else(|| denied("Missing runtime folder"))?
        .to_path_buf();
    let name = match role {
        "host" => "magic-system-host.exe",
        "service" => "magic-system-service.exe",
        "ctl" => "magic-system-ctl.exe",
        _ => return Err(denied("Unknown runtime role")),
    };
    let m = manifest()?;
    let p = relative(&plain(&folder)?, name)?;
    if m.files.get(name) != Some(&hash(&p)?) {
        return Err(denied("Runtime role identity differs"));
    }
    Ok(p)
}
pub fn process_path(pid: u32) -> Result<PathBuf> {
    unsafe {
        let p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid)?;
        let mut buf = vec![0u16; 32768];
        let mut len = buf.len() as u32;
        let result =
            QueryFullProcessImageNameW(p, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len);
        CloseHandle(p)?;
        result?;
        Ok(PathBuf::from(String::from_utf16_lossy(
            &buf[..len as usize],
        )))
    }
}
pub fn verify_peer(pid: u32, roles: &[&str]) -> Result<()> {
    if !enabled() {
        return Ok(());
    }
    let actual = plain(&process_path(pid)?)?;
    for role in roles {
        let expected = executable(role)?;
        if actual == expected {
            return Ok(());
        }
    }
    Err(denied("IPC peer does not match a protected community role"))
}
pub fn verify_hook_sender(pid: u32) -> Result<()> {
    let expected = plain(
        &PathBuf::from(
            std::env::var_os("WINDIR").ok_or_else(|| denied("Windows directory unavailable"))?,
        )
        .join("explorer.exe"),
    )?;
    if plain(&process_path(pid)?)? == expected {
        Ok(())
    } else {
        Err(denied("Tray notification sender is not the Windows shell"))
    }
}
impl RuntimeManifest {
    pub fn accepted(&self, capability: &str) -> Result<bool> {
        let Some(e) = self.capabilities.get(capability) else {
            return Ok(false);
        };
        if e.status != "accepted" {
            return Ok(false);
        };
        let folder = plain(
            module_path()?
                .parent()
                .ok_or_else(|| denied("Missing runtime folder"))?,
        )?;
        let expected = self
            .files
            .get("magic-system-host.exe")
            .ok_or_else(|| denied("Missing host identity"))?;
        if &e.provider_sha256 != expected {
            return Ok(false);
        }
        let evidence = relative(&folder, &e.evidence_path)?;
        if hash(&evidence)? != e.evidence_sha256 {
            return Ok(false);
        }
        let input = std::fs::read(evidence)?;
        if input.len() > 65536 {
            return Err(denied("Capability receipt exceeds bound"));
        }
        let receipt: serde_json::Value = serde_json::from_slice(&input)?;
        if receipt["protocol"].as_str() != Some(PROTOCOL)
            || receipt["instanceId"].as_str() != Some(self.instance_id.as_str())
            || receipt["capability"].as_str() != Some(capability)
            || receipt["reviewedBy"].as_str().is_none_or(str::is_empty)
        {
            return Ok(false);
        }
        for file in [
            "magic-system-host.exe",
            "magic-system-service.exe",
            "magic-system-ctl.exe",
            "sluhk.dll",
        ] {
            if receipt["runtimeFiles"][file].as_str() != self.files.get(file).map(String::as_str) {
                return Ok(false);
            }
        }
        let checks: &[&str] = match capability {
            "system-data" => &["snapshot-actions", "idle-resources"],
            "window-tracking" => &["open-close-focus", "mixed-dpi", "elevated-window"],
            "tray" => &["icons-actions", "explorer-restart"],
            "notifications" => &["authorize-revoke", "arrive-dismiss", "exit-restore"],
            "taskbar" => &["dock-actions", "uac-entry", "crash-restore"],
            "configuration" => &["import-checksums", "plugin-order", "startup-rollback"],
            _ => return Ok(false),
        };
        Ok(checks.iter().all(|check| {
            receipt["checks"][*check]["passed"] == true
                && receipt["checks"][*check]["evidence"]
                    .as_str()
                    .is_some_and(|text| !text.is_empty())
        }))
    }
}
// Protected immutable installations are verified once per process, never hashed per tray/window event.
static HOST: OnceLock<std::result::Result<PathBuf, String>> = OnceLock::new();
static SERVICE: OnceLock<std::result::Result<PathBuf, String>> = OnceLock::new();
static CONTROLLER: OnceLock<std::result::Result<PathBuf, String>> = OnceLock::new();
pub fn executable(role: &str) -> Result<PathBuf> {
    let cell = match role {
        "host" => &HOST,
        "service" => &SERVICE,
        "ctl" => &CONTROLLER,
        _ => return Err(denied("Unknown runtime role")),
    };
    match cell.get_or_init(|| resolve_executable(role).map_err(|e| e.to_string())) {
        Ok(path) => Ok(path.clone()),
        Err(e) => Err(denied(e.clone())),
    }
}
pub fn authorize_app_sender(pid: u32, notification: bool) -> Result<()> {
    if !enabled() {
        return Ok(());
    }
    if verify_peer(pid, &["host", "service", "ctl"]).is_ok() {
        return Ok(());
    }
    if notification {
        return verify_hook_sender(pid);
    }
    Err(denied("Untrusted application IPC sender"))
}

pub fn task_folder() -> &'static str {
    if enabled() {
        r"\MagicCommunity"
    } else {
        r"\Seelen"
    }
}
pub fn task_name() -> &'static str {
    if enabled() {
        "Magic Community System Service"
    } else {
        "Seelen UI Service"
    }
}
pub fn runtime_directory() -> Result<PathBuf> {
    Ok(plain(&module_path()?)?
        .parent()
        .ok_or_else(|| denied("Missing runtime directory"))?
        .to_path_buf())
}
pub fn payload(relative_path: &str) -> Result<PathBuf> {
    let m = manifest()?;
    let expected = m
        .files
        .get(relative_path)
        .ok_or_else(|| denied("Resource is not in the owned manifest"))?;
    let path = relative(&runtime_directory()?, relative_path)?;
    // Immutable runtime input; full payload validation is cached in manifest().
    if hash(&path)? != *expected {
        return Err(denied("Owned payload hash differs"));
    }
    Ok(path)
}
pub fn service_logs() -> Result<PathBuf> {
    let folder = runtime_directory()?.join("logs");
    // Installer creates this directory. An elevated process never follows a user-writable log path.
    let path = plain(&folder)?;
    protected(&path)?;
    for name in ["SLU Service.log", "SLU Service.log.bak"] {
        let file = path.join(name);
        if file.exists() {
            plain(&file)?;
            protected(&file)?;
        }
    }
    Ok(path)
}

/// ShellExecute/Explorer use the regular Win32 spelling. Identity comparisons
/// continue to use canonical paths returned by executable().
pub fn shell_executable(role: &str) -> Result<PathBuf> {
    let path = executable(role)?;
    let text = path.to_string_lossy();
    Ok(PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(&text)))
}
