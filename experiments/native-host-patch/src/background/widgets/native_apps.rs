//! Uses the original apps-menu storage contract; no second favorites database.
use crate::error::Result;
use crate::utils::constants::SEELEN_COMMON;
use serde_json::{Value, json};
#[derive(Default)]
pub(super) struct Apps {
    pub favorites: Vec<Value>,
    pub all: bool,
    pub folder: Option<String>,
    pub fullscreen: bool,
    pub offset: usize,
    pub settings: Vec<Value>,
    baseline: Option<Vec<u8>>,
}
fn path() -> std::path::PathBuf {
    SEELEN_COMMON
        .app_data_dir()
        .join("data/seelen-apps-menu/favorites.json")
}
fn read() -> Result<Option<Vec<u8>>> {
    let p = path();
    match std::fs::metadata(&p) {
        Ok(m) => {
            if m.len() > 1024 * 1024 {
                return Err("Favorites file exceeds bound".into());
            }
            Ok(Some(std::fs::read(p)?))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e.into()),
    }
}
fn identity(v: &Value) -> &str {
    v["itemId"].as_str().unwrap_or("")
}
pub(super) fn app_id(v: &Value) -> String {
    v["umid"]
        .as_str()
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
        .unwrap_or_else(|| v["path"].as_str().unwrap_or("").to_lowercase())
}
impl Apps {
    pub async fn load() -> Result<Self> {
        let baseline = read()?;
        let mut favorites = match &baseline {
            Some(bytes) => serde_json::from_slice::<Vec<Value>>(bytes)?,
            None => Vec::new(),
        };
        if baseline.is_none() {
            if let Some(value) =
                std::fs::read(SEELEN_COMMON.app_cache_dir().join("start-layout.json"))
                    .ok()
                    .filter(|b| b.len() <= 1024 * 1024)
                    .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
            {
                let catalog = crate::tauri_handlers::Handlers::get_start_menu_items();
                for v in value["pinnedList"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .take(256)
                {
                    let link = v["desktopAppLink"].as_str().unwrap_or("");
                    let id = catalog
                        .iter()
                        .find(|a| a.path.to_string_lossy().eq_ignore_ascii_case(link))
                        .map(|a| {
                            a.umid
                                .clone()
                                .unwrap_or_else(|| a.path.to_string_lossy().to_lowercase())
                        })
                        .or_else(|| {
                            v["packagedAppId"]
                                .as_str()
                                .or(v["destopAppId"].as_str())
                                .or(v["desktopAppLink"].as_str())
                                .map(str::to_owned)
                        });
                    if let Some(id) = id {
                        favorites.push(json!({"type":"app","itemId":id}));
                    }
                }
            }
        }
        if favorites.len() > 1024 {
            return Err("Too many favorites".into());
        }
        let display = path().with_file_name("display_mode.json");
        let fullscreen = std::fs::read(display)
            .ok()
            .filter(|b| b.len() < 1024)
            .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
            .is_some_and(|v| v == "fullscreen");
        Ok(Self {
            favorites,
            baseline,
            fullscreen,
            ..Self::default()
        })
    }
    pub fn toggle_display(&mut self) -> Result<()> {
        let base = path()
            .parent()
            .ok_or("Missing app data directory")?
            .to_owned();
        std::fs::create_dir_all(&base)?;
        let target = super::resolve_safe_path(&base, "display_mode.json")?;
        let next = !self.fullscreen;
        super::atomic_write_file(
            &target,
            if next {
                b"\"fullscreen\""
            } else {
                b"\"normal\""
            },
        )?;
        self.fullscreen = next;
        Ok(())
    }
    pub fn contains(&self, id: &str) -> bool {
        self.favorites.iter().any(|v| {
            identity(v) == id
                || v["itemIds"]
                    .as_array()
                    .is_some_and(|a| a.iter().any(|v| v.as_str() == Some(id)))
        })
    }
    fn remove(&mut self, id: &str) {
        let mut result = Vec::new();
        for mut v in self.favorites.drain(..) {
            if identity(&v) == id {
                continue;
            }
            if let Some(members) = v.get_mut("itemIds").and_then(Value::as_array_mut) {
                members.retain(|v| v.as_str() != Some(id));
                if members.len() == 1 {
                    result.push(json!({"type":"app","itemId":members[0]}));
                    continue;
                }
                if members.is_empty() {
                    continue;
                }
            }
            result.push(v);
        }
        self.favorites = result;
    }
    pub fn edit(&mut self, verb: u64, id: &str, destination: &str, name: &str) -> Result<()> {
        if read()? != self.baseline {
            return Err("应用收藏已在其他界面修改，请关闭后重新打开。".into());
        }
        let old = self.favorites.clone();
        self.mutate(verb, id, destination, name)?;
        let content = serde_json::to_vec(&self.favorites)?;
        let save = (|| {
            let p = path();
            let base = p.parent().ok_or("Missing favorites directory")?;
            std::fs::create_dir_all(base)?;
            let target = super::resolve_safe_path(base, "favorites.json")?;
            super::atomic_write_file(&target, &content)
        })();
        if let Err(e) = save {
            self.favorites = old;
            return Err(e);
        }
        self.baseline = Some(content);
        Ok(())
    }
    fn mutate(&mut self, verb: u64, id: &str, destination: &str, name: &str) -> Result<()> {
        let old = self.favorites.clone();
        let mutation: Result<()> = (|| {
            match verb {
                10 => {
                    if self.contains(id) {
                        self.remove(id);
                    } else {
                        self.favorites.push(json!({"type":"app","itemId":id}));
                    }
                }
                16 => {
                    if id == destination {
                        return Err("Cannot group with itself".into());
                    }
                    let source = self.favorites.iter().find(|v| identity(v) == id).cloned();
                    let target = self
                        .favorites
                        .iter()
                        .find(|v| identity(v) == destination)
                        .cloned()
                        .ok_or("Destination favorite changed")?;
                    let mut members = source
                        .as_ref()
                        .and_then(|v| v["itemIds"].as_array())
                        .map(|v| {
                            v.iter()
                                .filter_map(Value::as_str)
                                .map(str::to_owned)
                                .collect::<Vec<_>>()
                        })
                        .unwrap_or_else(|| vec![id.to_owned()]);
                    let mut folder = if target["type"] == "folder" {
                        target.clone()
                    } else {
                        json!({"type":"folder","itemId":uuid::Uuid::new_v4(),"name":"收藏文件夹","itemIds":[destination]})
                    };
                    let list = folder["itemIds"].as_array_mut().ok_or("Invalid folder")?;
                    for id in members.drain(..) {
                        if !list.iter().any(|v| v.as_str() == Some(&id)) {
                            list.push(json!(id));
                        }
                    }
                    if list.len() > 256 {
                        return Err("收藏文件夹已达上限。".into());
                    }
                    self.remove(id);
                    let at = self
                        .favorites
                        .iter()
                        .position(|v| identity(v) == destination)
                        .ok_or("Destination changed")?;
                    self.favorites[at] = folder;
                }
                17 => {
                    if let Some(folder) = self.folder.clone() {
                        let list = self
                            .favorites
                            .iter_mut()
                            .find(|v| identity(v) == folder)
                            .and_then(|v| v["itemIds"].as_array_mut())
                            .ok_or("Folder changed")?;
                        let from = list
                            .iter()
                            .position(|v| v.as_str() == Some(id))
                            .ok_or("Source changed")?;
                        let to = list
                            .iter()
                            .position(|v| v.as_str() == Some(destination))
                            .ok_or("Destination changed")?;
                        let v = list.remove(from);
                        list.insert(to, v);
                    } else {
                        let from = self
                            .favorites
                            .iter()
                            .position(|v| identity(v) == id)
                            .ok_or("Source changed")?;
                        let to = self
                            .favorites
                            .iter()
                            .position(|v| identity(v) == destination)
                            .ok_or("Destination changed")?;
                        let v = self.favorites.remove(from);
                        self.favorites.insert(to, v);
                    }
                }
                21 => {
                    self.remove(id);
                    self.favorites.push(json!({"type":"app","itemId":id}));
                    if self
                        .folder
                        .as_ref()
                        .is_some_and(|folder| !self.favorites.iter().any(|v| identity(v) == folder))
                    {
                        self.folder = None;
                    }
                }
                18 => {
                    let at = self
                        .favorites
                        .iter()
                        .position(|v| identity(v) == id && v["type"] == "folder")
                        .ok_or("Folder changed")?;
                    let v = self.favorites.remove(at);
                    for (i, id) in v["itemIds"]
                        .as_array()
                        .ok_or("Invalid folder")?
                        .iter()
                        .enumerate()
                    {
                        self.favorites
                            .insert(at + i, json!({"type":"app","itemId":id}));
                    }
                }
                19 => {
                    let folder = self
                        .favorites
                        .iter_mut()
                        .find(|v| identity(v) == id && v["type"] == "folder")
                        .ok_or("Folder changed")?;
                    folder["name"] = json!(name.chars().take(80).collect::<String>());
                }
                _ => return Err("Unknown favorite edit".into()),
            }
            if self.favorites.len() > 1024 {
                return Err("Too many favorites".into());
            }
            Ok(())
        })();
        if let Err(error) = mutation {
            self.favorites = old;
            return Err(error);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> Apps {
        Apps {
            favorites: vec![
                json!({"type":"app","itemId":"a"}),
                json!({"type":"app","itemId":"b"}),
                json!({"type":"app","itemId":"external","retain":true}),
            ],
            ..Apps::default()
        }
    }
    #[test]
    fn group_disband_and_preserve_external() {
        let mut a = fixture();
        a.mutate(16, "a", "b", "").unwrap();
        assert_eq!(a.favorites.len(), 2);
        assert_eq!(a.favorites[0]["itemIds"], json!(["b", "a"]));
        assert_eq!(a.favorites[1]["retain"], true);
        let id = identity(&a.favorites[0]).to_owned();
        a.mutate(18, &id, "", "").unwrap();
        assert_eq!(
            a.favorites.iter().map(identity).collect::<Vec<_>>(),
            vec!["b", "a", "external"]
        );
    }
    #[test]
    fn unpin_folder_child_keeps_remaining_favorite() {
        let mut a = fixture();
        a.mutate(16, "a", "b", "").unwrap();
        a.mutate(10, "a", "", "").unwrap();
        assert_eq!(a.favorites[0], json!({"type":"app","itemId":"b"}));
    }
    #[test]
    fn invalid_destination_keeps_original() {
        let mut a = fixture();
        let old = a.favorites.clone();
        assert!(a.mutate(16, "a", "missing", "").is_err());
        assert_eq!(a.favorites, old);
    }
    #[test]
    fn reorder_and_rename_without_new_format() {
        let mut a = fixture();
        a.mutate(17, "a", "external", "").unwrap();
        assert_eq!(
            a.favorites.iter().map(identity).collect::<Vec<_>>(),
            vec!["b", "external", "a"]
        );
        a.mutate(16, "a", "b", "").unwrap();
        let id = identity(&a.favorites[0]).to_owned();
        a.mutate(19, &id, "", "项目").unwrap();
        assert_eq!(a.favorites[0]["name"], "项目");
    }
}

pub(super) fn matches_name(item: &Value, query: &str) -> bool {
    let name = item["displayName"].as_str().unwrap_or("").to_lowercase();
    let initials = name
        .split(' ')
        .filter_map(|part| part.chars().next())
        .collect::<String>();
    let path = item["path"].as_str().unwrap_or("").to_lowercase();
    let target = item["target"].as_str().unwrap_or("").to_lowercase();
    query.is_empty()
        || query.split_whitespace().all(|part| {
            name.contains(part)
                || initials.contains(part)
                || std::path::Path::new(&path)
                    .file_stem()
                    .is_some_and(|s| s.to_string_lossy().contains(part))
                || std::path::Path::new(&target)
                    .file_stem()
                    .is_some_and(|s| s.to_string_lossy().contains(part))
        })
}

#[cfg(test)]
mod search_tests {
    use super::*;
    #[test]
    fn names_stems_and_word_initials_match() {
        let app = json!({"displayName":"Visual Studio Code","path":"C:\\apps\\code.exe"});
        assert!(matches_name(&app, "vsc"));
        assert!(matches_name(&app, "code"));
        assert!(matches_name(&app, "visual code"));
        assert!(!matches_name(&app, "unrelated"));
    }
}

pub(super) fn query(text: &str) -> (String, String, String) {
    let raw = text.trim();
    if let Some((prefix, term)) = raw.split_once(':') {
        let prefix = prefix.to_lowercase();
        if ["apps", "files", "settings", "web"].contains(&prefix.as_str()) {
            return (prefix, term.trim().to_lowercase(), term.trim().to_owned());
        }
    }
    (String::new(), raw.to_lowercase(), raw.to_owned())
}
#[cfg(test)]
mod query_tests {
    use super::*;
    #[test]
    fn filters_accept_empty_terms_case_and_whitespace() {
        assert_eq!(
            query("  APPS: "),
            ("apps".to_owned(), String::new(), String::new())
        );
        assert_eq!(
            query(" web: CaseSensitiveID "),
            (
                "web".to_owned(),
                "casesensitiveid".to_owned(),
                "CaseSensitiveID".to_owned()
            )
        );
        assert_eq!(query("normal query").0, "");
    }
}
