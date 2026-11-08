use std::{
    collections::HashSet,
    sync::{LazyLock, atomic::Ordering},
    time::Duration,
};

use parking_lot::Mutex;
use seelen_core::system_state::{PendingUacRequest, UacActivationOutcome};
use windows::Win32::{
    Foundation::{CloseHandle, GetLastError, LPARAM, SetLastError, WIN32_ERROR, WPARAM},
    System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    },
    UI::WindowsAndMessaging::{
        HSHELL_HIGHBIT, HSHELL_REDRAW, SC_RESTORE, SMTO_ABORTIFHUNG, SMTO_BLOCK,
        SendMessageTimeoutW, WM_SYSCOMMAND,
    },
};

use crate::{
    error::Result,
    event_manager,
    hook::HookManager,
    windows_api::{
        WindowEnumerator,
        event_window::{WM_SHELLHOOKMESSAGE, subscribe_to_background_window},
        window::{Window, event::WinEvent},
    },
};

use super::domain::{Candidate, INTERIM_CLASS, PendingRequests, matches_candidate, may_observe};

const HSHELL_FLASH: u32 = HSHELL_REDRAW | HSHELL_HIGHBIT;
static MANAGER: LazyLock<PendingUacManager> = LazyLock::new(PendingUacManager::new);

pub struct PendingUacManager {
    pending: Mutex<PendingRequests>,
    opening: Mutex<HashSet<String>>,
}

#[derive(Clone)]
pub enum PendingUacEvent {
    Observe(isize, bool),
    Reconcile,
    Changed,
}
event_manager!(PendingUacManager, PendingUacEvent);

impl PendingUacManager {
    pub fn instance() -> &'static Self {
        &MANAGER
    }

    fn new() -> Self {
        // The module's event dispatcher also handles native observations. It blocks
        // on its channel when idle, with no separate worker or periodic wakeup.
        Self::subscribe(|event| {
            let manager = Self::instance();
            match event {
                PendingUacEvent::Observe(hwnd, allow_hidden) => manager.observe(hwnd, allow_hidden),
                PendingUacEvent::Reconcile => {
                    let entries = manager.pending.lock().entries();
                    for entry in entries {
                        manager.observe(entry.candidate.hwnd, false);
                    }
                }
                PendingUacEvent::Changed => {}
            }
        });
        // Reuse the host's existing shell-hook window. No new WinEvent hook or timer.
        subscribe_to_background_window(move |msg, wparam, hwnd| {
            if msg == WM_SHELLHOOKMESSAGE.load(Ordering::Relaxed)
                && wparam as u32 == HSHELL_FLASH
                && Window::from(hwnd).class() == INTERIM_CLASS
            {
                // At the first flash the interim window may not yet be visible.
                Self::send(PendingUacEvent::Observe(hwnd, true));
            }
            Ok(())
        });
        HookManager::subscribe(move |(event, window)| {
            let hwnd = window.address();
            match event {
                WinEvent::ObjectCreate
                | WinEvent::ObjectShow
                | WinEvent::ObjectNameChange
                | WinEvent::ObjectStateChange => {
                    if window.class() == INTERIM_CLASS
                        || Self::instance().pending.lock().contains(hwnd)
                    {
                        Self::send(PendingUacEvent::Observe(hwnd, false));
                    }
                }
                WinEvent::ObjectHide | WinEvent::ObjectDestroy => {
                    if Self::instance().pending.lock().contains(hwnd) {
                        Self::send(PendingUacEvent::Observe(hwnd, false));
                    }
                }
                WinEvent::SystemForeground | WinEvent::SystemDesktopSwitch => {
                    if !Self::instance().pending.lock().is_empty() {
                        Self::send(PendingUacEvent::Reconcile);
                    }
                }
                _ => {}
            }
        });
        let mut pending = PendingRequests::default();
        // One startup scan covers requests which were already waiting before Dock loaded.
        let _ = WindowEnumerator::new().for_each(|window| {
            if let Some(candidate) = read_candidate(window, false) {
                pending.observe(candidate);
            }
        });
        Self {
            pending: Mutex::new(pending),
            opening: Mutex::default(),
        }
    }

    pub fn requests(&self) -> Vec<PendingUacRequest> {
        self.pending
            .lock()
            .entries()
            .into_iter()
            .map(|entry| PendingUacRequest {
                id: entry.id,
                title: entry.candidate.title,
            })
            .collect()
    }

    fn observe(&self, hwnd: isize, allow_hidden: bool) {
        let candidate = read_candidate(Window::from(hwnd), allow_hidden);
        let changed = {
            let mut pending = self.pending.lock();
            match candidate {
                Some(candidate) => pending.observe(candidate),
                None => pending.remove(hwnd),
            }
        };
        if changed {
            Self::send(PendingUacEvent::Changed);
        }
    }

    // Called only by the permission-checked command after a user clicks the entry.
    // Does not select Allow/No and never changes the input desktop or UAC policy.
    pub fn open(&self, id: String) -> Result<UacActivationOutcome> {
        if !self.opening.lock().insert(id.clone()) {
            return Ok(UacActivationOutcome::AlreadyOpening);
        }
        let outcome = self.restore(&id);
        self.opening.lock().remove(&id);
        outcome
    }

    fn restore(&self, id: &str) -> Result<UacActivationOutcome> {
        let Some(entry) = self.pending.lock().find(id) else {
            return Ok(UacActivationOutcome::NoLongerPending);
        };
        let window = Window::from(entry.candidate.hwnd);
        let valid = read_candidate(window, true).is_some_and(|now| now.pid == entry.candidate.pid);
        // A lifecycle event may have invalidated this generation while Toolhelp ran.
        let still_tracked = self.pending.lock().find(id).is_some();
        if !valid || !still_tracked {
            return Ok(self.clear(id));
        }

        let mut message_result = 0;
        let (sent, error) = unsafe {
            SetLastError(WIN32_ERROR(0));
            let sent = SendMessageTimeoutW(
                window.hwnd(),
                WM_SYSCOMMAND,
                WPARAM(SC_RESTORE as usize),
                LPARAM(0),
                SMTO_ABORTIFHUNG | SMTO_BLOCK,
                1000,
                Some(&mut message_result),
            );
            (sent.0 != 0, GetLastError().0)
        };
        // A bounded, one-off observation after a click, never a recurring polling loop.
        std::thread::sleep(Duration::from_millis(150));
        let remains =
            read_candidate(window, false).is_some_and(|now| now.pid == entry.candidate.pid);
        log::info!("[uac] restore id={id} sent={sent} error={error} remains={remains}");
        // Neither the API return value nor disappearance proves that the user granted permission.
        if remains {
            Ok(UacActivationOutcome::StillPending)
        } else {
            Ok(self.clear(id))
        }
    }

    fn clear(&self, id: &str) -> UacActivationOutcome {
        if self.pending.lock().remove_id(id) {
            Self::send(PendingUacEvent::Changed);
        }
        UacActivationOutcome::NoLongerPending
    }
}

fn read_candidate(window: Window, allow_hidden: bool) -> Option<Candidate> {
    if !window.is_window()
        || window.class() != INTERIM_CLASS
        || window.owner().is_some()
        || !may_observe(window.is_visible(), allow_hidden)
    {
        return None;
    }
    let pid = window.process().id();
    let executable = process_basename(pid)?;
    if !matches_candidate(&window.class(), &executable, pid, window.owner().is_some()) {
        return None;
    }
    let title = window.title();
    // Revalidate HWND identity after Toolhelp: protected consent.exe cannot be opened for a path query.
    if !window.is_window()
        || window.process().id() != pid
        || window.class() != INTERIM_CLASS
        || window.owner().is_some()
        || !may_observe(window.is_visible(), allow_hidden)
    {
        return None;
    }
    Some(Candidate {
        hwnd: window.address(),
        pid,
        title,
    })
}

fn process_basename(pid: u32) -> Option<String> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0).ok()?;
        let mut entry = PROCESSENTRY32W {
            dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        let mut found = None;
        if Process32FirstW(snapshot, &mut entry).is_ok() {
            loop {
                if entry.th32ProcessID == pid {
                    let length = entry
                        .szExeFile
                        .iter()
                        .position(|c| *c == 0)
                        .unwrap_or(entry.szExeFile.len());
                    found = Some(String::from_utf16_lossy(&entry.szExeFile[..length]));
                    break;
                }
                if Process32NextW(snapshot, &mut entry).is_err() {
                    break;
                }
            }
        }
        let _ = CloseHandle(snapshot);
        found
    }
}
