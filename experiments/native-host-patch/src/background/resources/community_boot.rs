//! Owned community profiles are verified and loaded before any widget is made.
//! No watcher, task registration, privileged action or background worker here.
use std::path::{Component, Path, PathBuf};

use seelen_core::resource::ResourceKind;
use serde::Deserialize;
use slu_utils::checksums::calculate_sha256;

use crate::error::Result;
use super::RESOURCES;

const PACKAGE: &str = "Seelen.SeelenUI_2.8.9.0_x64__p6yyn03m1894e";
const SERVICE_SHA: &str = "9107271073c0499142852d636b4fba57118fee3acc7bbe5b15b6e5dcceb66a24";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Boot {
    owner: PathBuf,
    service_executable: PathBuf,
    service_sha256: String,
    files: Vec<File>,
    resources: Vec<Resource>,
}
#[derive(Deserialize)]
struct File { path: String, sha256: String }
#[derive(Deserialize)]
struct Resource { kind: String, path: String, id: String }

fn owned_config() -> Result<Option<(PathBuf, Boot)>> {
    if slu_ipc::community::enabled() {
        let root=slu_ipc::community::runtime_directory()?;
        let config:Boot=serde_json::from_slice(&std::fs::read(slu_ipc::community::payload("community-boot.json")?)?)?;
        if config.owner.canonicalize()? != slu_ipc::community::manifest()?.project_root { return Err("Independent boot owner differs".into()); }
        let service=slu_ipc::community::executable("service")?;
        if config.service_executable.canonicalize()? != service || config.service_sha256 != slu_ipc::community::hash(&service)? { return Err("Independent boot service differs".into()); }
        return Ok(Some((root,config)));
    }
    let exe = std::env::current_exe()?;
    let profile = exe.parent().ok_or("Missing profile directory")?;
    let native = profile.parent().ok_or("Missing native directory")?;
    let profiles = native.parent().ok_or("Missing profiles directory")?;
    if native.file_name().and_then(|n| n.to_str()) != Some("native-host")
        || profiles.file_name().and_then(|n| n.to_str()) != Some("profiles") {
        return Ok(None);
    }
    let root = profiles.parent().ok_or("Missing project directory")?.canonicalize()?;
    let config: Boot = serde_json::from_slice(&std::fs::read(profile.join("community-boot.json"))?)?;
    if config.owner.canonicalize()? != root { return Err("Community boot owner differs".into()); }
    Ok(Some((root, config)))
}

fn profile_file(root: &Path, relative: &str) -> Result<PathBuf> {
    if slu_ipc::community::enabled() {
        let path=slu_ipc::community::payload(relative)?;
        if !path.starts_with(root) { return Err("Independent boot resource escapes runtime".into()); }
        return Ok(path);
    }
    let path = Path::new(relative);
    if path.is_absolute() || !path.components().all(|c| matches!(c, Component::Normal(_)))
        || path.components().next() != Some(Component::Normal("profiles".as_ref())) {
        return Err("Community boot path escapes profiles".into());
    }
    let full = root.join(path).canonicalize()?;
    if !full.starts_with(root.join("profiles").canonicalize()?) {
        return Err("Community boot profile resolves outside project".into());
    }
    Ok(full)
}

pub fn service_path() -> Result<Option<PathBuf>> {
    if slu_ipc::community::enabled() { return Ok(Some(slu_ipc::community::executable("service")?)); }
    let Some((_, config)) = owned_config()? else { return Ok(None); };
    let expected = PathBuf::from(std::env::var_os("ProgramFiles").ok_or("ProgramFiles missing")?)
        .join("WindowsApps").join(PACKAGE).join("slu-service.exe");
    if config.service_executable != expected || config.service_sha256 != SERVICE_SHA
        || calculate_sha256(&std::fs::read(&expected)?) != SERVICE_SHA {
        return Err("Original signed service baseline differs".into());
    }
    Ok(Some(expected))
}

pub async fn load() -> Result<()> {
    let Some((root, config)) = owned_config()? else { return Ok(()); };
    let expected = [("theme", "@default/theme"), ("plugin", "@seelen/tb-keyboard-selector"), ("plugin", "@default/power")];
    if config.resources.len() != expected.len() { return Err("Community boot resource count differs".into()); }
    let verified = verify_payload(&root, &config.files)?;
    for (resource, (kind, id)) in config.resources.iter().zip(expected) {
        if resource.kind != kind || resource.id != id { return Err("Community boot resource allowlist differs".into()); }
        let path = profile_file(&root, &resource.path)?;
        if !verified.contains(&path) { return Err("Community boot metadata not verified".into()); }
        let kind = if kind == "theme" { ResourceKind::Theme } else { ResourceKind::Plugin };
        let loaded = RESOURCES.load(&kind, &path).await?;
        if loaded.map(|value| value.to_string()).as_deref() != Some(id) { return Err("Community boot resource ID differs".into()); }
        let _ = RESOURCES.manual.insert_async(path).await;
    }
    log::info!("MAGIC_COMMUNITY_BOOT_READY resources=3 before_widgets=true");
    Ok(())
}

fn verify_payload(root: &Path, files: &[File]) -> Result<std::collections::HashSet<PathBuf>> {
    let mut verified = std::collections::HashSet::new();
    for file in files {
        let path = profile_file(root, &file.path)?;
        if calculate_sha256(&std::fs::read(&path)?) != file.sha256 { return Err("Community boot payload hash differs".into()); }
        if !verified.insert(path) { return Err("Duplicate community boot payload".into()); }
    }
    Ok(verified)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn traversal_and_absolute_profiles_are_rejected() {
        for name in ["../data/file", "profiles/../data/file", "C:\\Windows\\file", "data/session.json", "\\\\server\\file"] {
            assert!(profile_file(Path::new("C:\\nonexistent-fixture"), name).is_err());
        }
    }
    #[test] fn valid_profile_loads_but_tampered_payload_is_rejected() {
        let root = std::env::temp_dir().join(format!("magic-boot-test-{}",uuid::Uuid::new_v4()));
        let folder=root.join("profiles").join("fixture");std::fs::create_dir_all(&folder).unwrap();
        let file=folder.join("metadata.yml");std::fs::write(&file,b"accepted").unwrap();
        let files=[File{path:"profiles/fixture/metadata.yml".into(),sha256:calculate_sha256(b"accepted")}];
        assert!(verify_payload(&root,&files).is_ok());
        std::fs::write(&file,b"changed").unwrap();assert!(verify_payload(&root,&files).is_err());
        std::fs::remove_file(file).unwrap();std::fs::remove_dir(&folder).unwrap();std::fs::remove_dir(root.join("profiles")).unwrap();std::fs::remove_dir(root).unwrap();
    }
}
