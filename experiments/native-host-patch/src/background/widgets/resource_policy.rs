//! Bounded housekeeping for the nine resident community popups only.
//! Reuse stays enabled. No suspension, navigation, process trimming or new hook.
use crate::{get_tokio_handle, hook::HookManager, windows_api::window::event::WinEvent};
use parking_lot::Mutex;
use std::{
    collections::HashMap,
    sync::{
        Arc, LazyLock, Once, Weak,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};
use windows::Win32::{Foundation::HWND, UI::WindowsAndMessaging::IsWindowVisible};

const IDLE_MEMORY_DELAY: Duration = Duration::from_secs(90);
const HIDDEN_PROBE_INTERVAL: Duration = Duration::from_secs(30);
static POPUPS: LazyLock<Mutex<HashMap<isize, Weak<PopupResources>>>> =
    LazyLock::new(Mutex::default);

pub struct PopupResourceGuard(Arc<PopupResources>);

impl PopupResourceGuard {
    pub fn register(id: &str, window: &tauri::WebviewWindow, hwnd: isize) -> Option<Self> {
        if !is_resident_popup(id) {
            return None;
        }
        static REGISTER: Once = Once::new();
        REGISTER.call_once(|| {
            HookManager::subscribe(|(event, origin)| {
                if !matches!(event, WinEvent::ObjectShow | WinEvent::ObjectHide) {
                    return;
                }
                let state = POPUPS.lock().get(&origin.address()).and_then(Weak::upgrade);
                // Never dispatch to the UI thread while holding the registry lock.
                if let Some(state) = state {
                    state.visibility_changed();
                }
            });
        });
        let state = Arc::new(PopupResources {
            window: window.clone(),
            hwnd,
            generation: AtomicU64::new(0),
            low: AtomicBool::new(false),
            supported: AtomicBool::new(true),
            pending: Mutex::new(None),
        });
        POPUPS.lock().insert(hwnd, Arc::downgrade(&state));
        state.visibility_changed();
        Some(Self(state))
    }
}

impl Drop for PopupResourceGuard {
    fn drop(&mut self) {
        POPUPS.lock().remove(&self.0.hwnd);
        self.0.generation.fetch_add(1, Ordering::AcqRel);
        if let Some(task) = self.0.pending.lock().take() {
            task.abort();
        }
    }
}

struct PopupResources {
    window: tauri::WebviewWindow,
    hwnd: isize,
    generation: AtomicU64,
    low: AtomicBool,
    supported: AtomicBool,
    pending: Mutex<Option<tokio::task::JoinHandle<()>>>,
}

impl PopupResources {
    fn visibility_changed(self: &Arc<Self>) {
        let mut pending = self.pending.lock();
        if let Some(task) = pending.take() {
            task.abort();
        }
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        if is_visible(self.hwnd) {
            if self.low.load(Ordering::Acquire) {
                self.set_target(false, generation);
            }
        } else if self.supported.load(Ordering::Acquire) && !self.low.load(Ordering::Acquire) {
            let weak = Arc::downgrade(self);
            // A single cancellable deadline, not a repeating memory-pressure loop.
            *pending = Some(get_tokio_handle().spawn(async move {
                tokio::time::sleep(IDLE_MEMORY_DELAY).await;
                if let Some(state) = weak.upgrade() {
                    state.set_target(true, generation);
                }
            }));
        }
    }

    fn set_target(self: &Arc<Self>, low: bool, generation: u64) {
        let weak = Arc::downgrade(self);
        let result = self.window.with_webview(move |platform| {
            use webview2_com::Microsoft::Web::WebView2::Win32::{
                COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_LOW,
                COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_NORMAL, ICoreWebView2_19,
            };
            use windows_core::Interface;
            let Some(state) = weak.upgrade() else {
                return;
            };
            // Recheck on UI: a queued hide must not reclaim a newly shown popup.
            if !target_is_current(
                generation,
                state.generation.load(Ordering::Acquire),
                low,
                is_visible(state.hwnd),
            ) {
                return;
            }
            let result = (|| unsafe {
                let core = platform
                    .controller()
                    .CoreWebView2()?
                    .cast::<ICoreWebView2_19>()?;
                core.SetMemoryUsageTargetLevel(if low {
                    COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_LOW
                } else {
                    COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_NORMAL
                })
            })();
            match result {
                Ok(()) => {
                    state.low.store(low, Ordering::Release);
                }
                Err(error) => {
                    // Older runtimes retain ordinary WebView behavior; no retry timer.
                    if state.supported.swap(false, Ordering::AcqRel) {
                        log::warn!("Popup memory target unavailable: {error}");
                    }
                }
            }
        });
        if let Err(error) = result {
            log::debug!("Popup memory target dispatch skipped: {error}");
        }
    }
}

#[derive(Default)]
pub struct ProbeCadence {
    last_attempt: Option<Instant>,
}

impl ProbeCadence {
    pub fn due(&self, now: Instant, hidden: bool) -> bool {
        !hidden
            || self
                .last_attempt
                .is_none_or(|last| now.saturating_duration_since(last) >= HIDDEN_PROBE_INTERVAL)
    }
    pub fn attempted(&mut self, now: Instant) {
        self.last_attempt = Some(now);
    }
    pub fn reset(&mut self) {
        self.last_attempt = None;
    }
}

pub fn is_resident_popup(id: &str) -> bool {
    matches!(
        id,
        "@seelen/quick-settings"
            | "@seelen/calendar-popup"
            | "@seelen/network-popup"
            | "@seelen/bluetooth-popup"
            | "@seelen/media-popup"
            | "@seelen/notifications"
            | "@seelen/keyboard-selector"
            | "@seelen/system-tray"
            | "@seelen/user-menu"
    )
}

// Restore before delivering a trigger so a reclaimed popup can prepare its paint.
// No deployment/pod map is held while queuing UI work.
pub fn prepare_trigger(label: &str) {
    let state = POPUPS
        .lock()
        .values()
        .filter_map(Weak::upgrade)
        .find(|state| state.window.label() == label);
    if let Some(state) = state {
        if let Some(task) = state.pending.lock().take() {
            task.abort();
        }
        let generation = state.generation.fetch_add(1, Ordering::AcqRel) + 1;
        if state.low.load(Ordering::Acquire) {
            state.set_target(false, generation);
        }
    }
}

fn target_is_current(expected: u64, actual: u64, low: bool, visible: bool) -> bool {
    expected == actual && (!low || !visible)
}

pub fn is_visible(hwnd: isize) -> bool {
    unsafe { IsWindowVisible(HWND(hwnd as _)).as_bool() }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stale_or_visible_reclaim_is_refused() {
        assert!(target_is_current(4, 4, true, false));
        assert!(!target_is_current(4, 5, true, false));
        assert!(!target_is_current(4, 4, true, true));
        assert!(target_is_current(4, 4, false, true));
        assert!(!target_is_current(4, 5, false, true));
    }
    #[test]
    fn only_known_resident_popups_are_managed() {
        assert!(is_resident_popup("@seelen/notifications"));
        for id in [
            "@seelen/weg",
            "@magic-community/desktop",
            "@other/popup",
            "@seelen/settings",
        ] {
            assert!(!is_resident_popup(id));
        }
    }
    #[test]
    fn visible_probe_is_never_delayed() {
        let now = Instant::now();
        let mut cadence = ProbeCadence::default();
        cadence.attempted(now);
        assert!(cadence.due(now + Duration::from_secs(5), false));
    }
    #[test]
    fn hidden_probe_is_bounded_and_recovery_can_reset_it() {
        let now = Instant::now();
        let mut cadence = ProbeCadence::default();
        assert!(cadence.due(now, true));
        cadence.attempted(now);
        assert!(!cadence.due(now + Duration::from_secs(29), true));
        assert!(cadence.due(now + Duration::from_secs(30), true));
        cadence.reset();
        assert!(cadence.due(now, true));
    }
}
