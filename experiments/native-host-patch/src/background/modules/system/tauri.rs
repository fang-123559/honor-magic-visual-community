use std::sync::{Once, atomic::Ordering};

use seelen_core::{
    handlers::SeelenEvent,
    system_state::{Core, Disk, Gpu, Memory, NetworkStatistics},
};
use windows::Win32::UI::WindowsAndMessaging::WM_WTSSESSION_CHANGE;

use crate::{
    modules::system::{SystemInfo, SystemInfoEvent, demand::Demand},
    server::events::EVENTS_HUB,
    windows_api::event_window::{IS_INTERACTIVE_SESSION, subscribe_to_background_window},
};

const RESOURCE_EVENTS: [(&str, u8); 5] = [
    (SeelenEvent::SystemDisksChanged, Demand::DISKS),
    (SeelenEvent::SystemNetworkChanged, Demand::NETWORK),
    (SeelenEvent::SystemMemoryChanged, Demand::MEMORY),
    (SeelenEvent::SystemCoresChanged, Demand::CORES),
    (SeelenEvent::SystemGpusChanged, Demand::GPUS),
];

fn current_demand() -> u8 {
    if !IS_INTERACTIVE_SESSION.load(Ordering::Acquire) {
        return 0;
    }
    RESOURCE_EVENTS.iter().fold(0, |mask, (event, kind)| {
        if EVENTS_HUB.has_listeners(event) {
            mask | kind
        } else {
            mask
        }
    })
}

/// Resource events use the SDK's subscribed transport. Do not additionally
/// serialize/broadcast them to unrelated legacy Tauri webviews.
fn publish<T: serde::Serialize>(event: &str, value: &T) {
    if !IS_INTERACTIVE_SESSION.load(Ordering::Acquire) || !EVENTS_HUB.has_listeners(event) {
        return;
    }
    match serde_json::to_string(value) {
        Ok(payload) => EVENTS_HUB.publish(event, &payload),
        Err(error) => log::error!("Failed to serialize resource event {event}: {error}"),
    }
}

fn get_system_info() -> &'static SystemInfo {
    static REGISTRATION: Once = Once::new();
    REGISTRATION.call_once(|| {
        SystemInfo::subscribe(|event| {
            let info = SystemInfo::instance();
            match event {
                SystemInfoEvent::DisksChanged => {
                    publish(SeelenEvent::SystemDisksChanged, &*info.last_disks.lock())
                }
                SystemInfoEvent::NetworkChanged => publish(
                    SeelenEvent::SystemNetworkChanged,
                    &*info.last_networks.lock(),
                ),
                SystemInfoEvent::MemoryChanged => {
                    publish(SeelenEvent::SystemMemoryChanged, &*info.last_memory.lock())
                }
                SystemInfoEvent::CoresChanged => {
                    publish(SeelenEvent::SystemCoresChanged, &*info.last_cores.lock())
                }
                SystemInfoEvent::GpusChanged => {
                    publish(SeelenEvent::SystemGpusChanged, &*info.last_gpus.lock())
                }
            }
        });
        EVENTS_HUB.observe_demand(|event| {
            if RESOURCE_EVENTS
                .iter()
                .any(|(resource, _)| *resource == event)
            {
                SystemInfo::instance().update_demand(current_demand);
            }
        });
        // The existing background window updates IS_INTERACTIVE_SESSION before
        // notifying observers. Lock/user-switch can therefore sleep the sampler
        // indefinitely too; resume wakes it without adding a session poller.
        subscribe_to_background_window(|message, _, _| {
            if message == WM_WTSSESSION_CHANGE {
                SystemInfo::instance().update_demand(current_demand);
            }
            Ok(())
        });
    });
    let info = SystemInfo::instance();
    // A subscription normally precedes its first getter. Observe that already
    // active demand too; do not depend on another subscription event to start.
    info.update_demand(current_demand);
    info
}

impl crate::tauri_handlers::Handlers {
    pub fn get_system_disks() -> Vec<Disk> {
        let info = get_system_info();
        info.snapshot(Demand::DISKS);
        info.last_disks.lock().clone()
    }

    pub fn get_system_network() -> Vec<NetworkStatistics> {
        let info = get_system_info();
        info.snapshot(Demand::NETWORK);
        info.last_networks.lock().clone()
    }

    pub fn get_system_memory() -> Memory {
        let info = get_system_info();
        info.snapshot(Demand::MEMORY);
        info.last_memory.lock().clone()
    }

    pub fn get_system_cores() -> Vec<Core> {
        let info = get_system_info();
        info.snapshot(Demand::CORES);
        info.last_cores.lock().clone()
    }

    pub fn get_system_gpus() -> Vec<Gpu> {
        let info = get_system_info();
        info.snapshot(Demand::GPUS);
        info.last_gpus.lock().clone()
    }
}
