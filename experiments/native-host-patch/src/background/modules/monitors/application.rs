use std::{sync::LazyLock, time::Duration};

use seelen_core::system_state::{MonitorId, PhysicalMonitor};
use slu_utils::{Debounce, debounce};
use windows::{
    Devices::Display::Core::{
        DisplayManager, DisplayManagerChangedEventArgs, DisplayManagerDisabledEventArgs,
        DisplayManagerEnabledEventArgs, DisplayManagerOptions,
        DisplayManagerPathsFailedOrInvalidatedEventArgs,
    },
    Foundation::TypedEventHandler,
    Win32::UI::WindowsAndMessaging::{WM_DISPLAYCHANGE, WM_POWERBROADCAST, WM_WTSSESSION_CHANGE, PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND, WTS_SESSION_UNLOCK},
};

use crate::{
    error::{Result, ResultLogExt},
    event_manager,
    modules::system_settings::application::{SystemSettings, SystemSettingsEvent},
    utils::lock_free::SyncVec,
    windows_api::{
        MonitorEnumerator, event_window::subscribe_to_background_window,
    },
};

/// Builds a stable-ordered snapshot of the current physical monitors (rect/scale/primary),
/// used to detect real changes vs spurious WM_DISPLAYCHANGE/WinRT notifications that carry
/// no actual diff (e.g. the same set of targets, but re-reported without any property change).
fn snapshot_physical_monitors() -> Result<Vec<PhysicalMonitor>> {
    let mut monitors: Vec<PhysicalMonitor> = MonitorEnumerator::enumerate_win32()?
        .into_iter().map(PhysicalMonitor::try_from).collect::<Result<_>>()?;
    if monitors.is_empty() { return Err("Display stack has no usable monitors yet".into()); }
    monitors.sort_by(|a, b| a.id.0.cmp(&b.id.0));
    if monitors.windows(2).any(|pair| pair[0].id == pair[1].id) {
        return Err("Display stack returned duplicate monitor identities".into());
    }
    Ok(monitors)
}

pub struct MonitorManager {
    state_data: SyncVec<PhysicalMonitor>,
    /// DisplayManager manages critical hardware so be sure to be correctly used, or will make the app crash.
    /// https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager
    display_manager: DisplayManager,
    enabled_token: Option<i64>,
    disabled_token: Option<i64>,
    changed_token: Option<i64>,
    paths_failed_or_invalidated_token: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MonitorManagerEvent {
    /// the id used is the view primary target id
    ViewAdded(MonitorId),
    /// the id used is the view primary target id
    ViewRemoved(MonitorId),
    ViewsChanged,
}

event_manager!(MonitorManager, MonitorManagerEvent);

impl MonitorManager {
    fn create() -> Result<MonitorManager> {
        let display_manager = DisplayManager::Create(DisplayManagerOptions::None)?;
        Ok(MonitorManager {
            display_manager,
            state_data: SyncVec::from(snapshot_physical_monitors()?),
            enabled_token: None,
            disabled_token: None,
            changed_token: None,
            paths_failed_or_invalidated_token: None,
        })
    }

    pub fn instance() -> &'static MonitorManager {
        static MONITOR_MANAGER: LazyLock<MonitorManager> = LazyLock::new(|| {
            let mut m = MonitorManager::create().expect("Failed to create monitor manager");
            m.initialize().log_error();
            m
        });
        &MONITOR_MANAGER
    }

    fn initialize(&mut self) -> Result<()> {
        // DisplayManager.Start() requires subscribing to all events first
        // See: https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager.start
        self.enabled_token = self
            .display_manager
            .Enabled(&TypedEventHandler::new(Self::on_enabled))
            .ok();

        self.disabled_token = self
            .display_manager
            .Disabled(&TypedEventHandler::new(Self::on_disabled))
            .ok();

        self.changed_token = self
            .display_manager
            .Changed(&TypedEventHandler::new(Self::on_changed))
            .ok();

        self.paths_failed_or_invalidated_token = self
            .display_manager
            .PathsFailedOrInvalidated(&TypedEventHandler::new(
                Self::on_paths_failed_or_invalidated,
            ))
            .ok();

        self.display_manager.Start()?;
        SystemSettings::subscribe(|event| {
            if event == SystemSettingsEvent::TextScaleChanged {
                log::debug!("Text scale changed, re-emitting ViewsChanged");
                Self::request_display_state_refresh();
            }
        });

        subscribe_to_background_window(|event, w_param, _l_param| {
            if refresh_event(event, w_param) {
                log::debug!("Displays changed");
                Self::request_display_state_refresh();
            }
            Ok(())
        });
        Ok(())
    }

    /// Coalesces bursts of display notifications (WM_DISPLAYCHANGE + the several
    /// WinRT DisplayManager events can all fire for a single physical connect/disconnect)
    /// and waits for the Win32 topology to settle before diffing state, since Windows
    /// can take a moment after WinRT reports a target as connected to finish applying
    /// the mode/DPI/work-area for a newly attached monitor.
    fn request_display_state_refresh() {
        static DEBOUNCER: LazyLock<Debounce<()>> = LazyLock::new(|| {
            debounce(
                |_| match MonitorManager::check_for_display_changes() {
                    Ok(changed) => {
                        if changed {
                            MonitorManager::send(MonitorManagerEvent::ViewsChanged);
                        } else {
                            log::trace!("Display state refresh requested but nothing changed");
                        }
                    }
                    Err(e) => log::warn!("Failed to check for display changes: {e}"),
                },
                Duration::from_millis(400),
            )
        });
        DEBOUNCER.call(());
    }

    // Is recommended that subscribers re-enumerate all targets and state in this call,
    // since the system display stack could be left in any state before this event is raised.
    fn on_enabled(
        _sender: windows_core::Ref<DisplayManager>,
        args: windows_core::Ref<DisplayManagerEnabledEventArgs>,
    ) -> windows_core::Result<()> {
        log::trace!("DisplayManager enabled");

        // Critical!: app will crash if this is not set
        if let Some(args) = args.as_ref() {
            args.SetHandled(true)?;
        }
        Self::request_display_state_refresh();
        Ok(())
    }

    // Is recommended that subscribers attempt to clean up when Disabled is invoked.
    // Most display APIs will fail while the session display stack is disabled.
    fn on_disabled(
        _sender: windows_core::Ref<DisplayManager>,
        args: windows_core::Ref<DisplayManagerDisabledEventArgs>,
    ) -> windows_core::Result<()> {
        log::trace!("DisplayManager disabled");

        // Critical!: app will crash if this is not set
        if let Some(args) = args.as_ref() {
            args.SetHandled(true)?;
        }
        Ok(())
    }

    // this only detects changes on the display adapters like connect/disconnect of displays
    fn on_changed(
        _sender: windows_core::Ref<DisplayManager>,
        args: windows_core::Ref<DisplayManagerChangedEventArgs>,
    ) -> windows_core::Result<()> {
        log::trace!("DisplayManager changed");
        Self::request_display_state_refresh();

        // Critical!: app will crash if this is not set
        if let Some(args) = args.as_ref() {
            args.SetHandled(true)?;
        }
        Ok(())
    }

    fn on_paths_failed_or_invalidated(
        _sender: windows_core::Ref<DisplayManager>,
        args: windows_core::Ref<DisplayManagerPathsFailedOrInvalidatedEventArgs>,
    ) -> windows_core::Result<()> {
        log::trace!("DisplayManager paths failed or invalidated");
        // Treat this as a change event
        Self::request_display_state_refresh();

        // Critical!: app will crash if this is not set
        if let Some(args) = args.as_ref() {
            args.SetHandled(true)?;
        }
        Ok(())
    }

    /// Publish one coherent Win32 snapshot before notifying subscribers. WinRT
    /// remains a notification source; its transient DisplayView set must not be
    /// mixed with the physical list used by the frontend after sleep/reattach.
    fn check_for_display_changes() -> Result<bool> {
        let mut snapshot = snapshot_physical_monitors();
        // Event-local retries only; no steady-state timer or wakeups.
        for _ in 0..3 {
            if snapshot.is_ok() { break; }
            std::thread::sleep(Duration::from_millis(150));
            snapshot = snapshot_physical_monitors();
        }
        let next = snapshot?; // Preserve last known state on transient errors.
        let previous = Self::instance().state_data.to_vec();
        let events = topology_events(&previous, &next);
        let changed = previous != next;
        if changed {
            Self::instance().state_data.replace(next);
            log::info!("MAGIC_MONITOR_TOPOLOGY published ids={:?}", Self::instance().get_cached_ids());
            for event in events { Self::send(event); }
        }
        Ok(changed)
    }

    pub fn get_cached_ids(&self) -> Vec<MonitorId> {
        self.state_data.to_vec().into_iter().map(|monitor| monitor.id).collect()
    }

    pub fn get_cached_data(&self) -> Vec<PhysicalMonitor> {
        self.state_data.to_vec()
    }
}

impl Drop for MonitorManager {
    fn drop(&mut self) {
        self.display_manager.Stop().log_error();

        if let Some(enabled_token) = self.enabled_token {
            self.display_manager
                .RemoveEnabled(enabled_token)
                .log_error();
        }

        if let Some(disabled_token) = self.disabled_token {
            self.display_manager
                .RemoveDisabled(disabled_token)
                .log_error();
        }

        if let Some(changed_token) = self.changed_token {
            self.display_manager
                .RemoveChanged(changed_token)
                .log_error();
        }

        if let Some(paths_failed_or_invalidated_token) = self.paths_failed_or_invalidated_token {
            self.display_manager
                .RemovePathsFailedOrInvalidated(paths_failed_or_invalidated_token)
                .log_error();
        }
    }
}

// Existing background-window messages, no additional event thread.
fn refresh_event(event: u32, value: usize) -> bool {
    event == WM_DISPLAYCHANGE
        || (event == WM_POWERBROADCAST && matches!(value as u32, PBT_APMRESUMEAUTOMATIC | PBT_APMRESUMESUSPEND))
        || (event == WM_WTSSESSION_CHANGE && value as u32 == WTS_SESSION_UNLOCK)
}

fn topology_events(previous: &[PhysicalMonitor], next: &[PhysicalMonitor]) -> Vec<MonitorManagerEvent> {
    let mut events = Vec::new();
    for monitor in next {
        if !previous.iter().any(|old| old.id == monitor.id) { events.push(MonitorManagerEvent::ViewAdded(monitor.id.clone())); }
    }
    for monitor in previous {
        if !next.iter().any(|new| new.id == monitor.id) { events.push(MonitorManagerEvent::ViewRemoved(monitor.id.clone())); }
    }
    events
}

#[cfg(test)]
mod tests {
    use super::*;
    fn monitor(id: &str) -> PhysicalMonitor {
        PhysicalMonitor { id: id.into(), name: id.into(), rect: seelen_core::rect::Rect { left:0,top:0,right:1920,bottom:1080 }, scale_factor:1.0,is_primary:true,hdr:None,brightness:None }
    }
    #[test] fn simulated_sleep_target_is_replaced_by_real_monitors() {
        let old=vec![monitor("SIMULATED")];let next=vec![monitor("MAIN"),monitor("SECONDARY")];
        assert_eq!(topology_events(&old,&next),vec![MonitorManagerEvent::ViewAdded("MAIN".into()),MonitorManagerEvent::ViewAdded("SECONDARY".into()),MonitorManagerEvent::ViewRemoved("SIMULATED".into())]);
    }
    #[test] fn unchanged_monitors_do_not_restart_widgets() {
        let current=vec![monitor("MAIN"),monitor("SECONDARY")];assert!(topology_events(&current,&current).is_empty());
    }
    #[test] fn reconnect_retains_existing_screen_identity() {
        assert_eq!(topology_events(&[monitor("MAIN")],&[monitor("MAIN"),monitor("SECONDARY")]),vec![MonitorManagerEvent::ViewAdded("SECONDARY".into())]);
    }
    #[test] fn scale_change_still_refreshes_without_add_remove() {
        let old=monitor("MAIN");let mut next=old.clone();next.scale_factor=1.5;assert_ne!(old,next);assert!(topology_events(&[old],&[next]).is_empty());
    }
    #[test] fn resume_and_unlock_refresh_without_polling() {
        assert!(refresh_event(WM_DISPLAYCHANGE,0));assert!(refresh_event(WM_POWERBROADCAST,PBT_APMRESUMEAUTOMATIC as usize));assert!(refresh_event(WM_POWERBROADCAST,PBT_APMRESUMESUSPEND as usize));assert!(refresh_event(WM_WTSSESSION_CHANGE,WTS_SESSION_UNLOCK as usize));
        assert!(!refresh_event(WM_POWERBROADCAST,4));assert!(!refresh_event(WM_WTSSESSION_CHANGE,7));assert!(!refresh_event(0x200,0));
    }
}
