use std::time::{Duration, Instant};

use parking_lot::{Condvar, Mutex};

/// Independent resource bits. A connection can need one resource without waking
/// the others. Counts themselves belong to the existing events hub.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Demand(pub u8);

impl Demand {
    pub const DISKS: u8 = 1;
    pub const NETWORK: u8 = 2;
    pub const MEMORY: u8 = 4;
    pub const CORES: u8 = 8;
    pub const GPUS: u8 = 16;

    pub fn has(self, kind: u8) -> bool {
        self.0 & kind != 0
    }
}

/// No timer is armed while demand is empty. Only real subscription transitions
/// wake the worker; there is one worker regardless of monitor/subscriber count.
#[derive(Default)]
pub struct SamplingGate {
    state: Mutex<Demand>,
    changed: Condvar,
}

impl SamplingGate {
    pub fn update(&self, read_current: impl FnOnce() -> Demand) -> bool {
        let mut state = self.state.lock();
        // Read the authoritative counts under this lock. Two concurrent observer
        // callbacks cannot overwrite a newer snapshot with an older snapshot.
        let next = read_current();
        if *state != next {
            *state = next;
            self.changed.notify_one();
            return true;
        }
        false
    }

    pub fn current(&self) -> Demand {
        *self.state.lock()
    }

    pub fn wait(&self, previous: Demand, deadline: Option<Instant>) -> Demand {
        let mut state = self.state.lock();
        loop {
            if *state != previous {
                return *state;
            }
            if state.0 == 0 {
                // No timeout while idle, even if the caller retained an old deadline.
                self.changed.wait(&mut state);
            } else if let Some(deadline) = deadline {
                if Instant::now() >= deadline
                    || self.changed.wait_until(&mut state, deadline).timed_out()
                {
                    return *state;
                }
                // Spurious wakes never restart the normal sampling interval.
            } else {
                return *state;
            }
        }
    }
}

/// A shared periodic deadline survives demand changes. Only newly activated
/// kinds are sampled immediately; existing rate counters keep their cadence.
#[derive(Default)]
pub struct SamplingSchedule {
    previous: Demand,
    deadline: Option<Instant>,
}

impl SamplingSchedule {
    pub fn deadline(&self) -> Option<Instant> {
        self.deadline
    }

    pub fn plan(&mut self, active: Demand, now: Instant, interval: Duration) -> SamplePlan {
        let added = active.0 & !self.previous.0;
        let periodic = self.deadline.is_some_and(|deadline| now >= deadline);
        let rearm_after_completion = active.0 != 0 && (self.deadline.is_none() || periodic);
        let sample = if periodic { active } else { Demand(added) };
        if active.0 == 0 {
            self.deadline = None;
        } else if self.deadline.is_none() || periodic {
            self.deadline = Some(now + interval);
        }
        self.previous = active;
        SamplePlan {
            sample,
            prime: added,
            rearm_after_completion,
        }
    }

    pub fn complete(&mut self, plan: &SamplePlan, now: Instant, interval: Duration) {
        if plan.rearm_after_completion {
            // Slow PDH/disk calls must not cause a catch-up busy loop. A normal
            // cycle sleeps its interval after completion, like the old worker.
            self.deadline = Some(now + interval);
        }
    }
}

pub struct SamplePlan {
    pub sample: Demand,
    pub prime: u8,
    rearm_after_completion: bool,
}

/// sysinfo returns byte/packet deltas. Use actual elapsed time, including a short
/// first cycle for a resource added between shared periodic ticks.
pub fn rate_per_second(delta: u64, elapsed: Option<Duration>) -> u64 {
    match elapsed.filter(|duration| !duration.is_zero()) {
        Some(duration) => ((u128::from(delta) * 1_000_000_000) / duration.as_nanos())
            .min(u128::from(u64::MAX)) as u64,
        None => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        sync::{Arc, mpsc},
        thread,
    };

    #[test]
    fn empty_demand_does_not_use_periodic_timeout() {
        let gate = Arc::new(SamplingGate::default());
        let (tx, rx) = mpsc::channel();
        let worker = gate.clone();
        let handle = thread::spawn(move || {
            tx.send(worker.wait(
                Demand::default(),
                Some(Instant::now() + Duration::from_millis(1)),
            ))
            .unwrap();
        });
        // The supplied active interval expires many times; an idle worker must
        // still be asleep, until the actual first subscription arrives.
        assert!(rx.recv_timeout(Duration::from_millis(35)).is_err());
        gate.update(|| Demand(Demand::MEMORY));
        assert_eq!(
            rx.recv_timeout(Duration::from_secs(1)).unwrap(),
            Demand(Demand::MEMORY)
        );
        handle.join().unwrap();
    }

    #[test]
    fn changed_resource_set_wakes_without_waiting_for_interval() {
        let gate = Arc::new(SamplingGate::default());
        gate.update(|| Demand(Demand::MEMORY));
        let (tx, rx) = mpsc::channel();
        let worker = gate.clone();
        let handle = thread::spawn(move || {
            tx.send(worker.wait(
                Demand(Demand::MEMORY),
                Some(Instant::now() + Duration::from_secs(60)),
            ))
            .unwrap();
        });
        gate.update(|| Demand(Demand::CORES));
        assert_eq!(
            rx.recv_timeout(Duration::from_secs(1)).unwrap(),
            Demand(Demand::CORES)
        );
        handle.join().unwrap();
    }

    #[test]
    fn final_unsubscribe_returns_once_for_native_handle_cleanup() {
        let gate = SamplingGate::default();
        assert_eq!(gate.wait(Demand(Demand::GPUS), None), Demand::default());
    }

    #[test]
    fn demand_changes_only_sample_new_kinds_and_preserve_existing_periodic_deadline() {
        let now = Instant::now();
        let interval = Duration::from_secs(1);
        let mut schedule = SamplingSchedule::default();
        let first = schedule.plan(Demand(Demand::CORES), now, interval);
        assert_eq!(first.sample, Demand(Demand::CORES));
        assert_eq!(first.prime, Demand::CORES);
        assert_eq!(schedule.deadline(), Some(now + interval));
        let added = schedule.plan(
            Demand(Demand::CORES | Demand::MEMORY),
            now + Duration::from_millis(100),
            interval,
        );
        assert_eq!(added.sample, Demand(Demand::MEMORY));
        assert_eq!(added.prime, Demand::MEMORY);
        assert_eq!(schedule.deadline(), Some(now + interval));
        let removed = schedule.plan(
            Demand(Demand::CORES),
            now + Duration::from_millis(200),
            interval,
        );
        assert_eq!(removed.sample, Demand::default());
        assert_eq!(schedule.deadline(), Some(now + interval));
        let tick = schedule.plan(Demand(Demand::CORES), now + interval, interval);
        assert_eq!(tick.sample, Demand(Demand::CORES));
        assert_eq!(tick.prime, 0);
        assert_eq!(schedule.deadline(), Some(now + interval * 2));
    }

    #[test]
    fn idle_schedule_has_no_deadline_and_reactivation_is_primed() {
        let now = Instant::now();
        let interval = Duration::from_secs(2);
        let mut schedule = SamplingSchedule::default();
        schedule.plan(Demand(Demand::GPUS), now, interval);
        let stop = schedule.plan(Demand::default(), now + Duration::from_millis(5), interval);
        assert_eq!(stop.sample, Demand::default());
        assert_eq!(schedule.deadline(), None);
        let resume = schedule.plan(
            Demand(Demand::GPUS),
            now + Duration::from_secs(60),
            interval,
        );
        assert_eq!(resume.sample, Demand(Demand::GPUS));
        assert_eq!(resume.prime, Demand::GPUS);
    }

    #[test]
    fn byte_and_packet_rates_use_elapsed_time_instead_of_configured_interval() {
        assert_eq!(rate_per_second(100, Some(Duration::from_millis(50))), 2000);
        assert_eq!(rate_per_second(100, Some(Duration::from_millis(500))), 200);
        assert_eq!(rate_per_second(100, Some(Duration::from_secs(5))), 20);
        assert_eq!(rate_per_second(100, None), 0);
        assert_eq!(rate_per_second(100, Some(Duration::ZERO)), 0);
        assert_eq!(
            rate_per_second(u64::MAX, Some(Duration::from_nanos(1))),
            u64::MAX
        );
    }

    #[test]
    fn slow_periodic_read_gets_a_full_sleep_but_added_kind_does_not_push_existing_deadline() {
        let now = Instant::now();
        let interval = Duration::from_secs(1);
        let mut schedule = SamplingSchedule::default();
        let first = schedule.plan(Demand(Demand::CORES), now, interval);
        schedule.complete(&first, now + Duration::from_millis(1500), interval);
        let deadline = now + Duration::from_millis(2500);
        assert_eq!(schedule.deadline(), Some(deadline));
        let added = schedule.plan(
            Demand(Demand::CORES | Demand::MEMORY),
            now + Duration::from_millis(1600),
            interval,
        );
        schedule.complete(&added, now + Duration::from_millis(1800), interval);
        assert_eq!(schedule.deadline(), Some(deadline));
        let tick = schedule.plan(Demand(Demand::CORES | Demand::MEMORY), deadline, interval);
        schedule.complete(&tick, deadline + Duration::from_millis(1500), interval);
        assert_eq!(
            schedule.deadline(),
            Some(deadline + Duration::from_millis(2500))
        );
    }
}
