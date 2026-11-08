use std::time::{Duration, Instant};

use seelen_core::state::WidgetStatus;

/// Pure timing and retry policy. The owner supplies the clock and performs all
/// webview operations after releasing its locks.
pub struct LivenessPolicy {
    grace: Duration,
    probe_not_before: Instant,
    generation: u64,
    failures: u16,
    max_reloads: u8,
    gave_up: bool,
}

impl LivenessPolicy {
    pub fn new(now: Instant, grace: Duration, max_reloads: u8) -> Self {
        Self {
            grace,
            probe_not_before: now + grace,
            generation: 0,
            failures: 0,
            max_reloads,
            gave_up: false,
        }
    }

    /// Invalidate outstanding probes and grant the new navigation time to load.
    /// A reload must preserve the failure count, or repeated failures never stop.
    pub fn begin_reload(&mut self, now: Instant) -> u64 {
        self.advance_generation();
        self.probe_not_before = now + self.grace;
        self.generation
    }

    /// A successful Ready or Pong ends the grace period and failure streak.
    pub fn healthy(&mut self, now: Instant) {
        self.advance_generation();
        self.probe_not_before = now;
        self.failures = 0;
        self.gave_up = false;
    }

    pub fn reset_after_resume(&mut self, now: Instant) {
        self.advance_generation();
        self.probe_not_before = now + self.grace;
        self.failures = 0;
        self.gave_up = false;
    }

    /// Capture this token before sending a probe. A timeout from an older
    /// navigation, session, or already completed probe must not reload this one.
    pub fn can_probe(&self, now: Instant) -> Option<u64> {
        if self.gave_up || now < self.probe_not_before {
            None
        } else {
            Some(self.generation)
        }
    }

    pub fn failed(&mut self, now: Instant, generation: u64) -> Outcome {
        if self.can_probe(now) != Some(generation) {
            return Outcome::Ignore;
        }

        // A timeout may be delivered only once for the captured token. The
        // caller then commits the actual reload with commit_retry().
        self.advance_generation();
        self.failures += 1;
        if self.failures > u16::from(self.max_reloads) {
            self.gave_up = true;
            Outcome::GiveUp {
                attempt: self.failures,
            }
        } else {
            Outcome::Reload {
                attempt: self.failures,
            }
        }
    }

    /// Recheck a failure decision after waiting for the owning pod's locks.
    /// The caller must separately verify the policy still belongs to that pod;
    /// generations intentionally have meaning only within one policy instance.
    pub fn can_commit_failure(&self, probe_generation: u64) -> bool {
        self.failures > 0 && self.generation == probe_generation.wrapping_add(1)
    }

    /// Validate and reserve a retry atomically with respect to healthy/resume.
    /// Returns false when health or another navigation superseded the decision.
    pub fn commit_retry(&mut self, now: Instant, probe_generation: u64) -> bool {
        if self.gave_up || !self.can_commit_failure(probe_generation) {
            return false;
        }
        self.begin_reload(now);
        true
    }

    pub fn terminal_is_current(&self, probe_generation: u64) -> bool {
        self.gave_up && self.can_commit_failure(probe_generation)
    }

    fn advance_generation(&mut self) {
        self.generation = self.generation.wrapping_add(1);
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Outcome {
    Ignore,
    // u16 represents the final attempt even when max_reloads is u8::MAX.
    Reload { attempt: u16 },
    GiveUp { attempt: u16 },
}

/// Session recovery must not cancel a new webview's first navigation or reload
/// an existing navigation again while it is still mounting.
pub fn should_restart_on_resume(status: WidgetStatus) -> bool {
    status == WidgetStatus::Ready
}

#[cfg(test)]
mod tests {
    use super::*;

    const GRACE: Duration = Duration::from_secs(10);

    #[test]
    fn creation_grace_ends_at_the_deadline() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 5);
        assert_eq!(policy.can_probe(now), None);
        assert_eq!(policy.can_probe(now + GRACE - Duration::from_nanos(1)), None);
        assert_eq!(policy.failed(now, 0), Outcome::Ignore);
        assert_eq!(policy.can_probe(now + GRACE), Some(0));
    }

    #[test]
    fn every_reload_has_grace_and_invalidates_old_probes() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 5);
        let first = policy.can_probe(now + GRACE).unwrap();
        let reloaded_at = now + GRACE + Duration::from_secs(1);
        let next = policy.begin_reload(reloaded_at);
        assert_ne!(first, next);
        assert_eq!(policy.can_probe(reloaded_at), None);
        assert_eq!(policy.failed(reloaded_at, first), Outcome::Ignore);
        assert_eq!(policy.failed(reloaded_at, next), Outcome::Ignore);
        assert_eq!(policy.can_probe(reloaded_at + GRACE), Some(next));
        assert_eq!(policy.failed(reloaded_at + GRACE, first), Outcome::Ignore);
        assert_eq!(
            policy.failed(reloaded_at + GRACE, next),
            Outcome::Reload { attempt: 1 }
        );
    }

    #[test]
    fn healthy_ends_grace_and_clears_the_failure_streak() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 5);
        let token = policy.can_probe(now + GRACE).unwrap();
        assert_eq!(
            policy.failed(now + GRACE, token),
            Outcome::Reload { attempt: 1 }
        );
        let restarting_at = now + GRACE;
        let stale = policy.begin_reload(restarting_at);
        let ready_at = restarting_at + Duration::from_secs(1);
        policy.healthy(ready_at);
        let current = policy.can_probe(ready_at).unwrap();
        assert_ne!(stale, current);
        assert_eq!(policy.failed(ready_at, stale), Outcome::Ignore);
        assert_eq!(policy.failed(ready_at, current), Outcome::Reload { attempt: 1 });
    }

    #[test]
    fn resume_clears_failures_and_grants_grace() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 1);
        let token = policy.can_probe(now + GRACE).unwrap();
        assert_eq!(
            policy.failed(now + GRACE, token),
            Outcome::Reload { attempt: 1 }
        );
        let resumed_at = now + GRACE;
        policy.reset_after_resume(resumed_at);
        assert_eq!(policy.can_probe(resumed_at), None);
        assert_eq!(policy.failed(resumed_at + GRACE, token), Outcome::Ignore);
        let token = policy.can_probe(resumed_at + GRACE).unwrap();
        assert_eq!(
            policy.failed(resumed_at + GRACE, token),
            Outcome::Reload { attempt: 1 }
        );
    }

    #[test]
    fn repeated_reloads_still_give_up_after_the_finite_budget() {
        let mut now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 5);
        for attempt in 1..=5 {
            now += GRACE;
            let token = policy.can_probe(now).unwrap();
            assert_eq!(policy.failed(now, token), Outcome::Reload { attempt });
            assert_eq!(policy.failed(now, token), Outcome::Ignore);
            assert!(policy.commit_retry(now, token));
            assert_eq!(policy.can_probe(now), None);
        }
        now += GRACE;
        let token = policy.can_probe(now).unwrap();
        assert_eq!(policy.failed(now, token), Outcome::GiveUp { attempt: 6 });
        assert!(policy.terminal_is_current(token));
        assert!(!policy.commit_retry(now, token));
        assert_eq!(policy.failed(now, token), Outcome::Ignore);
        assert_eq!(policy.can_probe(now + GRACE), None);
    }

    #[test]
    fn zero_budget_gives_up_on_the_first_failure() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, Duration::ZERO, 0);
        let token = policy.can_probe(now).unwrap();
        assert_eq!(policy.failed(now, token), Outcome::GiveUp { attempt: 1 });
        assert_eq!(policy.can_probe(now), None);
        policy.healthy(now);
        assert!(policy.can_probe(now).is_some());
    }

    #[test]
    fn maximum_u8_budget_can_report_the_final_attempt_without_overflow() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, Duration::ZERO, u8::MAX);
        for attempt in 1..=u16::from(u8::MAX) {
            let token = policy.can_probe(now).unwrap();
            assert_eq!(policy.failed(now, token), Outcome::Reload { attempt });
            assert!(policy.commit_retry(now, token));
        }
        let token = policy.can_probe(now).unwrap();
        assert_eq!(policy.failed(now, token), Outcome::GiveUp { attempt: 256 });
    }

    #[test]
    fn pong_after_retry_decision_prevents_reload_commit() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, Duration::ZERO, 5);
        let token = policy.can_probe(now).unwrap();
        assert_eq!(policy.failed(now, token), Outcome::Reload { attempt: 1 });
        assert!(policy.can_commit_failure(token));
        policy.healthy(now);
        assert!(!policy.can_commit_failure(token));
        assert!(!policy.commit_retry(now, token));
        assert!(policy.can_probe(now).is_some());
    }

    #[test]
    fn ready_after_terminal_decision_prevents_give_up_commit() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, Duration::ZERO, 0);
        let token = policy.can_probe(now).unwrap();
        assert_eq!(policy.failed(now, token), Outcome::GiveUp { attempt: 1 });
        assert!(policy.terminal_is_current(token));
        policy.healthy(now);
        assert!(!policy.terminal_is_current(token));
        assert!(!policy.commit_retry(now, token));
        assert!(policy.can_probe(now).is_some());
    }

    #[test]
    fn retry_decision_can_be_committed_only_once() {
        let now = Instant::now();
        let mut policy = LivenessPolicy::new(now, GRACE, 5);
        let at = now + GRACE;
        let token = policy.can_probe(at).unwrap();
        assert_eq!(policy.failed(at, token), Outcome::Reload { attempt: 1 });
        assert!(policy.commit_retry(at, token));
        assert!(!policy.commit_retry(at, token));
        assert!(!policy.can_commit_failure(token));
        assert_eq!(policy.can_probe(at), None);
        let next = policy.can_probe(at + GRACE).unwrap();
        assert_eq!(policy.failed(at + GRACE, next), Outcome::Reload { attempt: 2 });
    }

    #[test]
    fn resume_or_new_navigation_invalidates_pending_failure_decisions() {
        let now = Instant::now();
        for terminal in [false, true] {
            let mut policy = LivenessPolicy::new(now, Duration::ZERO, u8::from(!terminal));
            let token = policy.can_probe(now).unwrap();
            let _ = policy.failed(now, token);
            policy.reset_after_resume(now);
            assert!(!policy.can_commit_failure(token));
            assert!(!policy.terminal_is_current(token));
            assert!(!policy.commit_retry(now, token));

            let token = policy.can_probe(now).unwrap();
            let _ = policy.failed(now, token);
            policy.begin_reload(now);
            assert!(!policy.can_commit_failure(token));
            assert!(!policy.terminal_is_current(token));
            assert!(!policy.commit_retry(now, token));
        }
    }

    #[test]
    fn unfailed_replacement_does_not_accept_an_old_pods_failure() {
        let now = Instant::now();
        let mut old = LivenessPolicy::new(now, Duration::ZERO, 5);
        let token = old.can_probe(now).unwrap();
        let _ = old.failed(now, token);
        assert!(old.can_commit_failure(token));
        let mut replacement = LivenessPolicy::new(now, Duration::ZERO, 5);
        replacement.begin_reload(now);
        assert!(!replacement.can_commit_failure(token));
        assert!(!replacement.commit_retry(now, token));
        assert!(!replacement.terminal_is_current(token));
    }

    #[test]
    fn pod_identity_is_required_when_separate_policy_tokens_coincide() {
        use std::sync::Arc;

        let now = Instant::now();
        let mut old = LivenessPolicy::new(now, Duration::ZERO, 5);
        let old_token = old.can_probe(now).unwrap();
        let _ = old.failed(now, old_token);
        let old = Arc::new(old);
        let mut replacement = LivenessPolicy::new(now, Duration::ZERO, 5);
        let replacement_token = replacement.can_probe(now).unwrap();
        let _ = replacement.failed(now, replacement_token);
        let mut replacement = Arc::new(replacement);

        // Local generations may coincide: the owner must compare Arc identity
        // before attempting to commit a decision against its current pod.
        assert_eq!(old_token, replacement_token);
        assert!(replacement.can_commit_failure(old_token));
        let committed = Arc::ptr_eq(&old, &replacement)
            && Arc::get_mut(&mut replacement)
                .unwrap()
                .commit_retry(now, old_token);
        assert!(!committed);
        assert!(replacement.can_commit_failure(replacement_token));
    }

    #[test]
    fn only_ready_instances_restart_on_resume() {
        for status in [
            WidgetStatus::Pending,
            WidgetStatus::Creating,
            WidgetStatus::Mounting,
            WidgetStatus::CrashedOnCreation,
            WidgetStatus::Restarting,
        ] {
            assert!(!should_restart_on_resume(status), "{status:?}");
        }
        assert!(should_restart_on_resume(WidgetStatus::Ready));
    }
}
