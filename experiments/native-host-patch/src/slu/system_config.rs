//! Explicit configuration import. No account tokens or live host are touched.
use super::Result;
use serde::Serialize;
use std::{
    collections::BTreeMap,
    io::Write,
    path::{Path, PathBuf},
};

const MAX_FILES: usize = 100_000;
const MAX_TOTAL: u64 = 2 * 1024 * 1024 * 1024;
const MAX_FILE: u64 = 128 * 1024 * 1024;
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportedFile {
    path: String,
    source_sha256: String,
    imported_sha256: String,
    rewritten_paths: u32,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportReceipt {
    schema_version: u32,
    protocol: &'static str,
    source: PathBuf,
    destination: PathBuf,
    files: Vec<ImportedFile>,
    clipboard_history_included: bool,
    source_retained: bool,
    legacy_runtime: Option<PathBuf>,
    unresolved_references: Vec<String>,
    activation_performed: bool,
}

pub fn process(args: &[String]) -> Result<bool> {
    if !slu_ipc::community::enabled()
        || args.get(1).map(String::as_str) != Some("--import-system-config")
    {
        return Ok(false);
    }
    if args.len() < 3 {
        return Err("Missing configuration source".into());
    }
    let mut clipboard = false;
    let mut legacy_runtime = None;
    let mut at = 3;
    while at < args.len() {
        match args[at].as_str(){
        "--include-clipboard-history" if !clipboard=>{clipboard=true;at+=1;},
        "--legacy-runtime" if legacy_runtime.is_none()&&at+1<args.len()=>{legacy_runtime=Some(slu_ipc::community::plain(Path::new(&args[at+1]))?);at+=2;},
        _=>return Err("Usage: magic-system-ctl --import-system-config <source> [--include-clipboard-history] [--legacy-runtime <old runtime>]".into()),
    }
    }
    if let Some(path) = &legacy_runtime {
        if !path.join("static").is_dir() || !path.join("seelen-ui.exe").is_file() {
            return Err("Explicit legacy runtime is not a Seelen resource root".into());
        }
    }
    let m = slu_ipc::community::manifest()?;
    // Do not race a backend holding configuration in memory. Import is an offline operation.
    use slu_ipc::IPC;
    if slu_ipc::AppIpc::can_stablish_connection() || slu_ipc::ServiceIpc::can_stablish_connection()
    {
        return Err("Stop the selected independent system before import".into());
    }
    let source = slu_ipc::community::plain(Path::new(&args[2]))?;
    let destination = &m.data_directory;
    if destination.exists() {
        return Err("Destination exists; import never overwrites or merges configuration".into());
    }
    if source.starts_with(destination) || destination.starts_with(&source) {
        return Err("Import source and destination overlap".into());
    }
    if !source.join("settings.json").is_file() {
        return Err("Import source is not a system configuration directory".into());
    }

    let mut files = BTreeMap::new();
    let mut total = 0u64;
    for entry in walkdir::WalkDir::new(&source).follow_links(false) {
        let entry = entry?;
        if entry.file_type().is_symlink() {
            return Err("Import contains a symbolic link".into());
        }
        let path = slu_ipc::community::plain(entry.path())?;
        if !path.starts_with(&source) {
            return Err("Import path escapes source".into());
        }
        if entry.file_type().is_dir() {
            continue;
        }
        let relative = path.strip_prefix(&source)?.to_path_buf();
        if !clipboard && relative == Path::new("clipboard_history.bin") {
            continue;
        }
        // Session credentials are not configuration; authentication remains an explicit user action.
        if ["session.json", "auth.json", "tokens.json"]
            .iter()
            .any(|n| relative == Path::new(n))
        {
            continue;
        }
        let size = entry.metadata()?.len();
        total = total.checked_add(size).ok_or("Import exceeds size bound")?;
        if files.len() >= MAX_FILES || size > MAX_FILE || total > MAX_TOTAL {
            return Err("Import exceeds 100k files / 128 MiB per file / 2 GiB total".into());
        }
        files.insert(relative, (path, size));
    }
    let staging = destination.with_file_name(format!("system-import-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir(&staging)?;
    // Failures retain staging and source for inspection; nothing is deleted automatically.
    let mut receipt = ImportReceipt {
        schema_version: 1,
        protocol: slu_ipc::community::PROTOCOL,
        source: source.clone(),
        destination: destination.clone(),
        files: Vec::new(),
        clipboard_history_included: clipboard,
        source_retained: true,
        legacy_runtime: legacy_runtime.clone(),
        unresolved_references: Vec::new(),
        activation_performed: false,
    };
    let old = display_path(&source);
    let new = display_path(destination);
    let mut mappings = vec![(old.clone(), new)];
    if let Some(path) = &legacy_runtime {
        mappings.push((
            display_path(path),
            display_path(&slu_ipc::community::runtime_directory()?),
        ));
    }
    for (relative, (path, size)) in files {
        let before = slu_ipc::community::hash(&path)?;
        let mut data = std::fs::read(&path)?;
        if data.len() as u64 != size || slu_ipc::community::hash(&path)? != before {
            return Err(format!("Import source changed: {}", path.display()).into());
        }
        let mut rewritten = 0;
        match relative
            .extension()
            .and_then(|v| v.to_str())
            .map(str::to_ascii_lowercase)
            .as_deref()
        {
            Some("json") => {
                let mut value: serde_json::Value =
                    serde_json::from_slice(data.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&data))
                        .map_err(|error| {
                            format!("Invalid import JSON {}: {error}", relative.display())
                        })?;
                for (old, new) in &mappings {
                    rewrite_json(&mut value, old, new, &mut rewritten);
                }
                if rewritten > 0 {
                    data = serde_json::to_vec_pretty(&value)?;
                }
            }
            Some("yml" | "yaml") => {
                let mut value: serde_yaml::Value =
                    serde_yaml::from_slice(&data).map_err(|error| {
                        format!("Invalid import YAML {}: {error}", relative.display())
                    })?;
                for (old, new) in &mappings {
                    rewrite_yaml(&mut value, old, new, &mut rewritten);
                }
                if rewritten > 0 {
                    data = serde_yaml::to_string(&value)?.into_bytes();
                }
            }
            _ => {}
        }
        if let Ok(text) = std::str::from_utf8(&data) {
            let mut normal = text.replace('/', "\\").to_lowercase();
            while normal.contains("\\\\") {
                normal = normal.replace("\\\\", "\\");
            }
            if mappings
                .iter()
                .any(|(old, _)| normal.contains(&old.to_lowercase()))
                || normal.contains(r"windowsapps\seelen.seelenui_")
            {
                receipt
                    .unresolved_references
                    .push(relative.to_string_lossy().into_owned());
            }
        }
        let target = staging.join(&relative);
        std::fs::create_dir_all(target.parent().ok_or("Missing import parent")?)?;
        let mut output = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&target)?;
        output.write_all(&data)?;
        output.sync_all()?;
        receipt.files.push(ImportedFile {
            path: relative.to_string_lossy().replace('\\', "/"),
            source_sha256: before,
            imported_sha256: slu_ipc::community::hash(&target)?,
            rewritten_paths: rewritten,
        });
    }
    // Recheck the complete source snapshot before publishing the atomic destination.
    for file in &receipt.files {
        if slu_ipc::community::hash(&source.join(&file.path))? != file.source_sha256 {
            return Err("Import source changed before commit; staging retained".into());
        }
    }
    let bytes = serde_json::to_vec_pretty(&receipt)?;
    let mut journal = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(staging.join("magic-system-import.json"))?;
    journal.write_all(&bytes)?;
    journal.sync_all()?;
    drop(journal);
    if !receipt.unresolved_references.is_empty() {
        return Err(format!("Import has {} unresolved legacy references; inspect retained staging {} and provide an explicit runtime mapping if needed",receipt.unresolved_references.len(),staging.display()).into());
    }
    // rename stays on this volume; an existing destination is rejected by Windows.
    std::fs::rename(&staging, destination)?;
    println!(
        "Imported configuration; previous data retained. No startup, ownership or runtime selection changed."
    );
    Ok(true)
}
fn display_path(path: &Path) -> String {
    path.to_string_lossy()
        .trim_start_matches(r"\\?\")
        .trim_end_matches('\\')
        .to_owned()
}
fn rewrite(value: &mut String, old: &str, new: &str, count: &mut u32) {
    let normal = value.replace('/', "\\");
    if normal
        .get(..old.len())
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case(old))
        && (normal.len() == old.len() || normal.as_bytes().get(old.len()) == Some(&b'\\'))
    {
        *value = format!("{new}{}", &normal[old.len()..]);
        *count += 1;
    }
}
fn rewrite_json(value: &mut serde_json::Value, old: &str, new: &str, count: &mut u32) {
    match value {
        serde_json::Value::String(s) => rewrite(s, old, new, count),
        serde_json::Value::Array(a) => {
            for v in a {
                rewrite_json(v, old, new, count)
            }
        }
        serde_json::Value::Object(o) => {
            for v in o.values_mut() {
                rewrite_json(v, old, new, count)
            }
        }
        _ => {}
    }
}
fn rewrite_yaml(value: &mut serde_yaml::Value, old: &str, new: &str, count: &mut u32) {
    match value {
        serde_yaml::Value::String(s) => rewrite(s, old, new, count),
        serde_yaml::Value::Sequence(a) => {
            for v in a {
                rewrite_yaml(v, old, new, count)
            }
        }
        serde_yaml::Value::Mapping(o) => {
            for v in o.values_mut() {
                rewrite_yaml(v, old, new, count)
            }
        }
        serde_yaml::Value::Tagged(t) => rewrite_yaml(&mut t.value, old, new, count),
        _ => {}
    }
}
