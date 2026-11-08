use std::collections::BTreeMap;

pub const INTERIM_CLASS: &str = "$$$Secure UAP Dummy Window Class For Interim Dialog";

#[derive(Default)]
pub struct PendingRequests {
    entries: BTreeMap<isize, PendingEntry>,
    generation: u64,
}

impl PendingRequests {
    pub fn observe(&mut self, candidate: Candidate) -> bool {
        if let Some(entry) = self.entries.get_mut(&candidate.hwnd)
            && entry.candidate.pid == candidate.pid
        {
            if entry.candidate == candidate {
                return false;
            }
            entry.candidate = candidate;
            return true;
        }
        self.generation = self
            .generation
            .checked_add(1)
            .expect("UAC entry id exhausted");
        self.entries.insert(
            candidate.hwnd,
            PendingEntry {
                id: self.generation.to_string(),
                candidate,
            },
        );
        true
    }

    pub fn entries(&self) -> Vec<PendingEntry> {
        let mut entries: Vec<_> = self.entries.values().cloned().collect();
        entries.sort_by_key(|entry| entry.id.parse::<u64>().unwrap_or_default());
        entries
    }

    pub fn find(&self, id: &str) -> Option<PendingEntry> {
        self.entries.values().find(|entry| entry.id == id).cloned()
    }

    pub fn contains(&self, hwnd: isize) -> bool {
        self.entries.contains_key(&hwnd)
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn remove(&mut self, hwnd: isize) -> bool {
        self.entries.remove(&hwnd).is_some()
    }

    // An old click/completion must never remove a newer request reusing the HWND.
    pub fn remove_id(&mut self, id: &str) -> bool {
        let hwnd = self
            .entries
            .values()
            .find(|entry| entry.id == id)
            .map(|e| e.candidate.hwnd);
        hwnd.is_some_and(|hwnd| self.remove(hwnd))
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PendingEntry {
    pub id: String,
    pub candidate: Candidate,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Candidate {
    pub hwnd: isize,
    pub pid: u32,
    pub title: String,
}

pub fn matches_candidate(class: &str, executable: &str, pid: u32, has_owner: bool) -> bool {
    class == INTERIM_CLASS
        && executable.eq_ignore_ascii_case("consent.exe")
        && pid != 0
        && !has_owner
}

pub fn may_observe(visible: bool, from_flash: bool) -> bool {
    visible || from_flash
}

#[cfg(test)]
mod tests {
    use super::*;

    fn candidate(hwnd: isize, pid: u32) -> Candidate {
        Candidate {
            hwnd,
            pid,
            title: format!("request {pid}"),
        }
    }

    #[test]
    fn only_interim_consent_windows_qualify() {
        assert!(matches_candidate(INTERIM_CLASS, "CONSENT.EXE", 42, false));
        assert!(!matches_candidate(INTERIM_CLASS, "updater.exe", 42, false));
        assert!(!matches_candidate(
            "ConsentPromptBox",
            "consent.exe",
            42,
            false
        ));
        assert!(!matches_candidate(INTERIM_CLASS, "consent.exe", 0, false));
        assert!(!matches_candidate(INTERIM_CLASS, "consent.exe", 42, true));
    }

    #[test]
    fn repeated_flashes_do_not_duplicate_or_republish() {
        let mut state = PendingRequests::default();
        assert!(state.observe(candidate(10, 42)));
        let id = state.entries()[0].id.clone();
        assert!(!state.observe(candidate(10, 42)));
        assert_eq!(state.entries().len(), 1);
        assert_eq!(state.entries()[0].id, id);
    }

    #[test]
    fn early_flash_can_precede_visibility_but_startup_and_hide_cannot() {
        assert!(may_observe(false, true));
        assert!(!may_observe(false, false));
        assert!(may_observe(true, false));
    }

    #[test]
    fn title_ready_after_flash_preserves_click_identity() {
        let mut state = PendingRequests::default();
        state.observe(candidate(10, 42));
        let id = state.entries()[0].id.clone();
        let mut ready = candidate(10, 42);
        ready.title = "Updater is requesting permission".into();
        assert!(state.observe(ready.clone()));
        assert_eq!(state.find(&id).unwrap().candidate, ready);
    }

    #[test]
    fn reused_hwnd_invalidates_old_click_and_completion() {
        let mut state = PendingRequests::default();
        state.observe(candidate(10, 42));
        let old = state.entries()[0].id.clone();
        state.observe(candidate(10, 43));
        assert!(state.find(&old).is_none());
        assert!(!state.remove_id(&old));
        assert_eq!(state.entries()[0].candidate.pid, 43);
    }

    #[test]
    fn same_process_reusing_a_destroyed_hwnd_gets_a_new_id() {
        let mut state = PendingRequests::default();
        state.observe(candidate(10, 42));
        let old = state.entries()[0].id.clone();
        assert!(state.remove(10));
        state.observe(candidate(10, 42));
        assert!(state.find(&old).is_none());
    }

    #[test]
    fn multiple_requests_are_independent_and_keep_arrival_order() {
        let mut state = PendingRequests::default();
        state.observe(candidate(20, 42));
        state.observe(candidate(10, 43));
        assert_eq!(state.entries()[0].candidate.hwnd, 20);
        let first = state.entries()[0].id.clone();
        assert!(state.remove_id(&first));
        assert_eq!(state.entries()[0].candidate.hwnd, 10);
        assert!(!state.remove_id(&first));
    }
}
