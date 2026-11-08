mod demand;
pub mod gpu;
pub mod tauri;

use std::{
    sync::{LazyLock, atomic::Ordering},
    time::{Duration, Instant},
};

use demand::{Demand, SamplingGate, SamplingSchedule, rate_per_second};
use gpu::GpuMonitor;
use parking_lot::Mutex;
use seelen_core::system_state::{Core, Disk, Gpu, Memory, NetworkStatistics};

use crate::{
    error::{Result, ResultLogExt},
    event_manager,
    state::application::FULL_STATE,
    utils::lock_free::TracedMutex,
    windows_api::event_window::IS_INTERACTIVE_SESSION,
};

pub struct SystemInfo {
    disks: TracedMutex<sysinfo::Disks>,
    networks: TracedMutex<sysinfo::Networks>,
    system: TracedMutex<sysinfo::System>,
    /// None if the GPU performance counters are not available
    gpu: TracedMutex<Option<GpuMonitor>>,
    gate: SamplingGate,
    sample_lock: Mutex<()>,
    gpu_attempted: Mutex<bool>,
    sampled: Mutex<u8>,
    rate_times: Mutex<[Option<Instant>; 2]>,
    // Cache for change detection
    pub last_disks: TracedMutex<Vec<Disk>>,
    pub last_networks: TracedMutex<Vec<NetworkStatistics>>,
    pub last_memory: TracedMutex<Memory>,
    pub last_cores: TracedMutex<Vec<Core>>,
    pub last_gpus: TracedMutex<Vec<Gpu>>,
}

#[derive(Debug, Clone)]
#[allow(clippy::enum_variant_names)]
pub enum SystemInfoEvent {
    DisksChanged,
    NetworkChanged,
    MemoryChanged,
    CoresChanged,
    GpusChanged,
}

event_manager!(SystemInfo, SystemInfoEvent);

impl SystemInfo {
    pub fn instance() -> &'static SystemInfo {
        static INSTANCE: LazyLock<SystemInfo> = LazyLock::new(|| {
            let mut instance = SystemInfo::new();
            instance.init().log_error();
            instance
        });
        &INSTANCE
    }

    fn new() -> Self {
        Self {
            disks: TracedMutex::new(sysinfo::Disks::new()),
            networks: TracedMutex::new(sysinfo::Networks::new()),
            system: TracedMutex::new(sysinfo::System::new()),
            gpu: TracedMutex::new(None),
            gate: SamplingGate::default(),
            sample_lock: Mutex::new(()),
            gpu_attempted: Mutex::new(false),
            sampled: Mutex::new(0),
            rate_times: Mutex::new([None; 2]),
            // Cache for change detection
            last_disks: TracedMutex::new(Vec::new()),
            last_networks: TracedMutex::new(Vec::new()),
            last_memory: TracedMutex::new(Memory::default()),
            last_cores: TracedMutex::new(Vec::new()),
            last_gpus: TracedMutex::new(Vec::new()),
        }
    }

    fn init(&mut self) -> Result<()> {
        // Initialize no OS samplers here. Creating SystemInfo for a one-shot
        // memory request must not also enumerate disks or open GPU counters.
        std::thread::Builder::new()
            .name("system-info-demand".into())
            .spawn(|| {
                let manager = SystemInfo::instance();
                let mut previous = Demand::default();
                let mut schedule = SamplingSchedule::default();
                loop {
                    let active = manager.gate.wait(previous, schedule.deadline());
                    let interval = FULL_STATE.load().settings.polling_interval.max(1);
                    manager.release_inactive(active);
                    let plan = schedule.plan(active, Instant::now(), Duration::from_secs(interval));
                    if plan.sample.0 != 0 && IS_INTERACTIVE_SESSION.load(Ordering::Acquire) {
                        manager.check_and_emit_changes(plan.sample, plan.prime);
                    }
                    schedule.complete(&plan, Instant::now(), Duration::from_secs(interval));
                    previous = active;
                }
            })?;
        Ok(())
    }

    /// Called only by infrastructure after actual event subscription changes.
    pub fn update_demand(&self, read_current: impl FnOnce() -> u8) {
        if self.gate.update(|| Demand(read_current())) {
            // Transition-only diagnostics, never a periodic logging loop.
            log::debug!(
                "MAGIC_RESOURCE_DEMAND active_mask={}",
                self.gate.current().0
            );
        }
    }

    fn release_inactive(&self, active: Demand) {
        let _sample = self.sample_lock.lock();
        *self.sampled.lock() &= active.0;
        if !active.has(Demand::GPUS) {
            *self.gpu.lock() = None;
            *self.gpu_attempted.lock() = false;
        }
    }

    fn snapshot(&self, kind: u8) {
        let _sample = self.sample_lock.lock();
        // Additional screens get the shared current snapshot, rather than
        // refreshing rate counters twice within a few milliseconds.
        if self.gate.current().has(kind) && *self.sampled.lock() & kind != 0 {
            return;
        }
        let force = kind & !*self.sampled.lock();
        self.refresh_unlocked(Demand(kind), force, false);
    }

    /// Refresh only the requested kinds. One-shot getters pass `emit = false`;
    /// the single demand worker emits changes to currently subscribed kinds.
    fn refresh(&self, active: Demand, force: u8, emit: bool) {
        let _sample = self.sample_lock.lock();
        self.refresh_unlocked(active, force, emit);
    }

    fn refresh_unlocked(&self, active: Demand, force: u8, emit: bool) {
        macro_rules! refresh {
            ($kind:ident, $read:ident, $cache:ident, $changed:ident, $event:ident, $prime:expr) => {
                if active.has(Demand::$kind) {
                    let mut current = self.$read();
                    // Prime delta-based counters after an idle period. The
                    // first value is warming up, not a rate over the idle gap.
                    if force & Demand::$kind != 0 {
                        ($prime)(&mut current);
                    }
                    let mut last = self.$cache.lock();
                    let changed = Self::$changed(&last, &current);
                    *last = current;
                    if emit && (changed || force & Demand::$kind != 0) {
                        Self::send(SystemInfoEvent::$event);
                    }
                }
            };
        }
        refresh!(
            DISKS,
            disks,
            last_disks,
            disks_changed,
            DisksChanged,
            |rows: &mut Vec<Disk>| for row in rows {
                row.read_bytes = 0;
                row.written_bytes = 0;
            }
        );
        refresh!(
            NETWORK,
            network,
            last_networks,
            network_changed,
            NetworkChanged,
            |rows: &mut Vec<NetworkStatistics>| for row in rows {
                row.received = 0;
                row.transmitted = 0;
                row.packets_received = 0;
                row.packets_transmitted = 0;
            }
        );
        refresh!(
            MEMORY,
            memory,
            last_memory,
            memory_changed,
            MemoryChanged,
            |_value: &mut Memory| {}
        );
        refresh!(
            CORES,
            cores,
            last_cores,
            cores_changed,
            CoresChanged,
            |rows: &mut Vec<Core>| for row in rows {
                row.usage = 0.0;
            }
        );
        if active.has(Demand::GPUS) {
            let mut attempted = self.gpu_attempted.lock();
            if !*attempted {
                *attempted = true;
                *self.gpu.lock() = GpuMonitor::new().ok_logged();
            }
            drop(attempted);
            if let Some(mut current) = self.gpus() {
                if force & Demand::GPUS != 0 {
                    for gpu in &mut current {
                        gpu.usage = 0.0;
                    }
                }
                let mut last = self.last_gpus.lock();
                let changed = Self::gpus_changed(&last, &current);
                *last = current;
                if emit && (changed || force & Demand::GPUS != 0) {
                    Self::send(SystemInfoEvent::GpusChanged);
                }
            }
            // A direct get without a subscription is one-shot, including handles.
            if !self.gate.current().has(Demand::GPUS) {
                *self.gpu.lock() = None;
                *self.gpu_attempted.lock() = false;
            }
        }
        *self.sampled.lock() |= active.0;
    }

    fn check_and_emit_changes(&self, active: Demand, force: u8) {
        // A subscription can disappear between wake-up and sampling. Re-read
        // current demand so it never starts an obsolete scheduled sample.
        let still_active = Demand(active.0 & self.gate.current().0);
        self.refresh(still_active, force, true);
    }

    fn disks_changed(old: &[Disk], new: &[Disk]) -> bool {
        if old.len() != new.len() {
            return true;
        }
        for (old_disk, new_disk) in old.iter().zip(new.iter()) {
            if old_disk.available_space != new_disk.available_space
                || old_disk.read_bytes != new_disk.read_bytes
                || old_disk.written_bytes != new_disk.written_bytes
            {
                return true;
            }
        }
        false
    }

    fn network_changed(old: &[NetworkStatistics], new: &[NetworkStatistics]) -> bool {
        if old.len() != new.len() {
            return true;
        }
        for (old_net, new_net) in old.iter().zip(new.iter()) {
            if old_net.received != new_net.received || old_net.transmitted != new_net.transmitted {
                return true;
            }
        }
        false
    }

    fn memory_changed(old: &Memory, new: &Memory) -> bool {
        old.free != new.free || old.swap_free != new.swap_free
    }

    fn cores_changed(old: &[Core], new: &[Core]) -> bool {
        if old.len() != new.len() {
            return true;
        }
        for (old_core, new_core) in old.iter().zip(new.iter()) {
            if old_core.usage != new_core.usage {
                return true;
            }
        }
        false
    }

    fn gpus_changed(old: &[Gpu], new: &[Gpu]) -> bool {
        if old.len() != new.len() {
            return true;
        }
        for (old_gpu, new_gpu) in old.iter().zip(new.iter()) {
            if old_gpu.id != new_gpu.id
                || old_gpu.usage != new_gpu.usage
                || old_gpu.dedicated_memory_used != new_gpu.dedicated_memory_used
                || old_gpu.shared_memory_used != new_gpu.shared_memory_used
            {
                return true;
            }
        }
        false
    }

    fn rate_interval(&self, index: usize) -> Option<Duration> {
        let now = Instant::now();
        self.rate_times.lock()[index]
            .replace(now)
            .map(|previous| now.duration_since(previous))
    }

    fn disks(&self) -> Vec<Disk> {
        let mut guard = self.disks.lock();
        guard.refresh(true);
        let elapsed = self.rate_interval(0);
        guard
            .iter()
            .map(|disk| {
                let usage = disk.usage();
                Disk {
                    name: disk.name().to_string_lossy().to_string(),
                    file_system: disk.file_system().to_string_lossy().to_string(),
                    mount_point: disk.mount_point().to_path_buf(),
                    total_space: disk.total_space(),
                    available_space: disk.available_space(),
                    is_removable: disk.is_removable(),
                    read_bytes: rate_per_second(usage.read_bytes, elapsed),
                    written_bytes: rate_per_second(usage.written_bytes, elapsed),
                }
            })
            .collect()
    }

    fn network(&self) -> Vec<NetworkStatistics> {
        let mut guard = self.networks.lock();
        guard.refresh(true);
        let elapsed = self.rate_interval(1);
        guard
            .iter()
            .map(|(name, network)| NetworkStatistics {
                name: name.clone(),
                received: rate_per_second(network.received(), elapsed),
                transmitted: rate_per_second(network.transmitted(), elapsed),
                packets_received: rate_per_second(network.packets_received(), elapsed),
                packets_transmitted: rate_per_second(network.packets_transmitted(), elapsed),
            })
            .collect()
    }

    fn memory(&self) -> Memory {
        let mut system = self.system.lock();
        system.refresh_memory();

        Memory {
            total: system.total_memory(),
            free: system.free_memory(),
            swap_total: system.total_swap(),
            swap_free: system.free_swap(),
        }
    }

    fn cores(&self) -> Vec<Core> {
        let mut system = self.system.lock();
        system.refresh_cpu_all();
        system
            .cpus()
            .iter()
            .map(|cpu| Core {
                name: cpu.name().to_owned(),
                brand: cpu.brand().to_owned(),
                usage: cpu.cpu_usage(),
                frequency: cpu.frequency(),
            })
            .collect()
    }

    /// Returns None on failure, to keep the last known value
    fn gpus(&self) -> Option<Vec<Gpu>> {
        let mut guard = self.gpu.lock();
        match guard.as_mut()?.gpus() {
            Ok(gpus) => Some(gpus),
            Err(err) => {
                log::debug!("Failed to read GPU usage: {err:?}");
                None
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn one_shot_memory_does_not_start_unrequested_os_samplers() {
        // Construct without init: this test cannot create a monitoring thread.
        let info = SystemInfo::new();
        info.snapshot(Demand::MEMORY);
        assert!(info.last_memory.lock().total > 0);
        assert_eq!(*info.sampled.lock(), Demand::MEMORY);
        assert!(info.gpu.lock().is_none());
        assert!(!*info.gpu_attempted.lock());
        assert!(info.last_disks.lock().is_empty());
        assert!(info.last_networks.lock().is_empty());
        assert!(info.last_cores.lock().is_empty());
        assert!(info.last_gpus.lock().is_empty());
    }

    #[test]
    fn second_screen_reuses_current_snapshot_until_sampling_cycle() {
        let info = SystemInfo::new();
        info.snapshot(Demand::MEMORY);
        // Mark a known cached snapshot, then enable a real subscription demand.
        // An additional screen must return it rather than requery the OS.
        info.last_memory.lock().free = 1;
        info.update_demand(|| Demand::MEMORY);
        info.snapshot(Demand::MEMORY);
        assert_eq!(info.last_memory.lock().free, 1);
        info.update_demand(|| 0);
        info.release_inactive(Demand::default());
        assert_eq!(*info.sampled.lock(), 0);
        info.snapshot(Demand::MEMORY);
        assert_ne!(info.last_memory.lock().free, 1);
    }
}
