use std::{
    collections::{HashMap, HashSet, VecDeque},
    sync::{Arc, LazyLock},
};

use parking_lot::{Mutex, RwLock};
use seelen_core::handlers::SeelenEvent;
use tokio::sync::Notify;

use super::protocol::event_frame;

const MAX_PENDING_FRAMES: usize = 256;

pub static EVENTS_HUB: LazyLock<EventsHub> = LazyLock::new(EventsHub::default);

/// One transaction protects active connection identity and global counts.
/// Lock order: hub state > connection state > pending frames. Observers run
/// after every one of these locks has been released.
#[derive(Default)]
pub struct EventsHub {
    state: RwLock<HubState>,
    demand_observers: RwLock<Vec<Arc<dyn Fn(&str) + Send + Sync>>>,
    #[cfg(test)]
    after_subscription_insert: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
}

impl EventsHub {
    pub fn observe_demand(&self, observer: impl Fn(&str) + Send + Sync + 'static) {
        self.demand_observers.write().push(Arc::new(observer));
    }

    fn demand_changed(&self, events: impl IntoIterator<Item = String>) {
        let observers = self.demand_observers.read().clone();
        for event in events {
            for observer in &observers {
                observer(&event);
            }
        }
    }

    pub fn has_listeners(&self, event: &str) -> bool {
        self.state
            .read()
            .listeners
            .get(event)
            .is_some_and(|count| *count > 0)
    }

    pub fn publish(&self, event: &str, payload_json: &str) {
        if !self.has_listeners(event) {
            return;
        }
        let frame = match event_frame(event, payload_json) {
            Ok(frame) => Arc::<str>::from(frame),
            Err(err) => {
                log::error!("Failed to serialize event {event}: {err}");
                return;
            }
        };
        let coalesce = !SeelenEvent::is_queued(event);
        for connection in self.state.read().connections.values() {
            connection.push(event, &frame, coalesce);
        }
    }

    pub fn connect(&self, label: &str) -> Arc<Connection> {
        let connection = Arc::new(Connection {
            label: label.to_owned(),
            ..Connection::default()
        });
        let (previous, events) = {
            let mut state = self.state.write();
            let previous = state
                .connections
                .insert(label.to_owned(), connection.clone());
            // Closed + drained + decremented are one transaction with identity
            // replacement. A late recv on the old socket cannot resubscribe.
            let events = previous
                .as_ref()
                .map(|old| state.close(old))
                .unwrap_or_default();
            (previous, events)
        };
        if let Some(previous) = previous {
            previous.notify.notify_one();
        }
        self.demand_changed(events);
        connection
    }

    pub fn disconnect(&self, label: &str, connection: &Arc<Connection>) {
        let events = {
            let mut state = self.state.write();
            if state
                .connections
                .get(label)
                .is_some_and(|current| Arc::ptr_eq(current, connection))
            {
                state.connections.remove(label);
            }
            state.close(connection)
        };
        connection.notify.notify_one();
        self.demand_changed(events);
    }

    /// False for unknown events, closed connections, or replaced connections.
    pub fn subscribe(&self, connection: &Connection, event: &str) -> bool {
        if !SeelenEvent::exists(event) {
            return false;
        }
        let inserted = {
            let mut state = self.state.write();
            if !state.is_current(connection) {
                return false;
            }
            let mut local = connection.state.lock();
            if local.closed {
                return false;
            }
            let inserted = local.subscriptions.insert(event.to_owned());
            if inserted {
                #[cfg(test)]
                {
                    let hook = self.after_subscription_insert.lock().clone();
                    if let Some(hook) = hook {
                        hook();
                    }
                }
                *state.listeners.entry(event.to_owned()).or_default() += 1;
            }
            inserted
        };
        if inserted {
            self.demand_changed([event.to_owned()]);
        }
        true
    }

    pub fn unsubscribe(&self, connection: &Connection, event: &str) {
        let removed = {
            let mut state = self.state.write();
            if !state.is_current(connection) {
                return;
            }
            let mut local = connection.state.lock();
            if local.closed {
                return;
            }
            let removed = local.subscriptions.remove(event);
            if removed {
                state.decrement(event);
            }
            removed
        };
        if removed {
            self.demand_changed([event.to_owned()]);
        }
    }
}

#[derive(Default)]
struct HubState {
    listeners: HashMap<String, usize>,
    connections: HashMap<String, Arc<Connection>>,
}

impl HubState {
    fn is_current(&self, connection: &Connection) -> bool {
        self.connections
            .get(&connection.label)
            .is_some_and(|current| std::ptr::eq(Arc::as_ptr(current), connection))
    }

    fn decrement(&mut self, event: &str) {
        if let Some(count) = self.listeners.get_mut(event) {
            *count -= 1;
            if *count == 0 {
                self.listeners.remove(event);
            }
        }
    }

    fn close(&mut self, connection: &Connection) -> Vec<String> {
        let mut local = connection.state.lock();
        local.closed = true;
        let events: Vec<_> = local.subscriptions.drain().collect();
        for event in &events {
            self.decrement(event);
        }
        connection.pending.lock().clear();
        events
    }
}

#[derive(Default)]
pub struct Connection {
    label: String,
    state: Mutex<ConnectionState>,
    pending: Mutex<VecDeque<PendingFrame>>,
    notify: Notify,
}

impl Connection {
    pub async fn notified(&self) {
        self.notify.notified().await
    }
    pub fn is_closed(&self) -> bool {
        self.state.lock().closed
    }
    pub fn drain(&self) -> VecDeque<PendingFrame> {
        std::mem::take(&mut *self.pending.lock())
    }

    fn push(&self, event: &str, frame: &Arc<str>, coalesce: bool) {
        let state = self.state.lock();
        if state.closed || !state.subscriptions.contains(event) {
            return;
        }
        {
            let mut pending = self.pending.lock();
            if coalesce {
                pending.retain(|p| p.event != event);
            }
            if pending.len() >= MAX_PENDING_FRAMES {
                log::warn!("Events connection is not reading, dropping oldest pending event");
                pending.pop_front();
            }
            pending.push_back(PendingFrame {
                event: event.to_owned(),
                frame: frame.clone(),
            });
        }
        drop(state);
        self.notify.notify_one();
    }
}

#[derive(Default)]
struct ConnectionState {
    subscriptions: HashSet<String>,
    closed: bool,
}

pub struct PendingFrame {
    event: String,
    pub frame: Arc<str>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        sync::{
            Barrier,
            atomic::{AtomicBool, Ordering},
            mpsc,
        },
        thread,
        time::Duration,
    };

    fn frames(connection: &Connection) -> Vec<String> {
        connection
            .drain()
            .into_iter()
            .map(|p| p.frame.to_string())
            .collect()
    }

    #[test]
    fn publish_without_listeners_does_nothing() {
        let hub = EventsHub::default();
        let connection = hub.connect("hub-test");
        hub.publish(SeelenEvent::PowerMode, "1");
        assert!(frames(&connection).is_empty());
        assert!(!hub.has_listeners(SeelenEvent::PowerMode));
    }

    #[test]
    fn unknown_events_are_rejected() {
        let hub = EventsHub::default();
        let connection = hub.connect("hub-test");
        assert!(!hub.subscribe(&connection, "not-an-event"));
        assert!(!hub.has_listeners("not-an-event"));
    }

    #[test]
    fn snapshots_are_coalesced_and_queued_are_not() {
        let hub = EventsHub::default();
        let connection = hub.connect("hub-test");
        hub.subscribe(&connection, SeelenEvent::PowerMode);
        hub.subscribe(&connection, SeelenEvent::WegAddItem);

        hub.publish(SeelenEvent::PowerMode, "1");
        hub.publish(SeelenEvent::WegAddItem, r#""a""#);
        hub.publish(SeelenEvent::PowerMode, "2");
        hub.publish(SeelenEvent::WegAddItem, r#""b""#);

        let frames = frames(&connection);
        assert_eq!(frames.len(), 3);
        assert!(frames[0].contains(r#""payload":"a""#));
        assert!(frames[1].contains(r#""payload":2"#));
        assert!(frames[2].contains(r#""payload":"b""#));
    }

    #[test]
    fn listeners_are_counted_per_connection() {
        let hub = EventsHub::default();
        let a = hub.connect("hub-test-a");
        let b = hub.connect("hub-test-b");

        hub.subscribe(&a, SeelenEvent::PowerMode);
        hub.subscribe(&a, SeelenEvent::PowerMode);
        hub.subscribe(&b, SeelenEvent::PowerMode);

        hub.unsubscribe(&a, SeelenEvent::PowerMode);
        assert!(hub.has_listeners(SeelenEvent::PowerMode));
        hub.disconnect("hub-test-b", &b);
        assert!(!hub.has_listeners(SeelenEvent::PowerMode));
    }

    #[test]
    fn replaced_connection_does_not_remove_the_new_one() {
        let hub = EventsHub::default();
        let old = hub.connect("hub-test");
        hub.subscribe(&old, SeelenEvent::PowerMode);

        let new = hub.connect("hub-test");
        assert!(old.is_closed());
        assert!(!hub.has_listeners(SeelenEvent::PowerMode));

        hub.subscribe(&new, SeelenEvent::PowerMode);
        hub.disconnect("hub-test", &old);
        assert!(hub.has_listeners(SeelenEvent::PowerMode));

        hub.publish(SeelenEvent::PowerMode, "1");
        assert_eq!(frames(&new).len(), 1);
    }

    #[test]
    fn demand_observer_sees_authoritative_multi_screen_counts_after_unlock() {
        let hub = Arc::new(EventsHub::default());
        let weak = Arc::downgrade(&hub);
        let observed = Arc::new(Mutex::new(Vec::new()));
        let seen = observed.clone();
        hub.observe_demand(move |event| {
            // Taking the listeners read lock here also proves callbacks are not
            // invoked under the listeners write lock.
            seen.lock()
                .push(weak.upgrade().unwrap().has_listeners(event));
        });
        let a = hub.connect("resource-screen-a");
        let b = hub.connect("resource-screen-b");
        hub.subscribe(&a, SeelenEvent::SystemMemoryChanged);
        hub.subscribe(&a, SeelenEvent::SystemMemoryChanged);
        hub.subscribe(&b, SeelenEvent::SystemMemoryChanged);
        hub.unsubscribe(&a, SeelenEvent::SystemMemoryChanged);
        hub.disconnect("resource-screen-b", &b);
        hub.disconnect("resource-screen-b", &b);
        assert_eq!(*observed.lock(), [true, true, true, false]);
    }

    #[test]
    fn resource_reload_releases_old_demand_without_stopping_replacement() {
        let hub = EventsHub::default();
        let old = hub.connect("resource-screen");
        hub.subscribe(&old, SeelenEvent::SystemCoresChanged);
        let replacement = hub.connect("resource-screen");
        assert!(!hub.has_listeners(SeelenEvent::SystemCoresChanged));
        hub.subscribe(&replacement, SeelenEvent::SystemCoresChanged);
        hub.disconnect("resource-screen", &old);
        assert!(hub.has_listeners(SeelenEvent::SystemCoresChanged));
        hub.disconnect("resource-screen", &replacement);
        assert!(!hub.has_listeners(SeelenEvent::SystemCoresChanged));
    }

    fn overlap_insert_with_close(reload: bool) {
        let hub = Arc::new(EventsHub::default());
        let old = hub.connect("overlap-screen");
        let inserted = Arc::new(Barrier::new(2));
        let resume = Arc::new(Barrier::new(2));
        let first = Arc::new(AtomicBool::new(true));
        let inserted_hook = inserted.clone();
        let resume_hook = resume.clone();
        *hub.after_subscription_insert.lock() = Some(Arc::new(move || {
            if first.swap(false, Ordering::AcqRel) {
                // Pause at exactly the formerly unsafe insert / count gap.
                inserted_hook.wait();
                resume_hook.wait();
            }
        }));
        let subscribing_hub = hub.clone();
        let subscribing_old = old.clone();
        let subscriber = thread::spawn(move || {
            subscribing_hub.subscribe(&subscribing_old, SeelenEvent::SystemMemoryChanged)
        });
        inserted.wait();
        // A deterministic lock probe, independent of whether the competing
        // thread gets scheduled within the timeout below: the formerly split
        // insert/count gap must itself still own the Hub transaction lock.
        let transaction_held = hub.state.try_write().is_none();
        let closing_hub = hub.clone();
        let closing_old = old.clone();
        let (attempt_tx, attempt_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::channel();
        let closer = thread::spawn(move || {
            attempt_tx.send(()).unwrap();
            let replacement = if reload {
                Some(closing_hub.connect("overlap-screen"))
            } else {
                closing_hub.disconnect("overlap-screen", &closing_old);
                None
            };
            done_tx.send(replacement).unwrap();
        });
        attempt_rx.recv_timeout(Duration::from_secs(1)).unwrap();
        // Replacement/close cannot split set insertion from count commit.
        let was_blocked = done_rx.recv_timeout(Duration::from_millis(25)).is_err();
        resume.wait();
        assert!(subscriber.join().unwrap());
        let replacement = done_rx.recv_timeout(Duration::from_secs(1)).unwrap();
        closer.join().unwrap();
        assert!(transaction_held);
        assert!(was_blocked);
        assert!(old.is_closed());
        assert!(old.state.lock().subscriptions.is_empty());
        assert!(!hub.has_listeners(SeelenEvent::SystemMemoryChanged));
        assert!(!hub.subscribe(&old, SeelenEvent::SystemMemoryChanged));
        if let Some(replacement) = replacement {
            assert!(hub.subscribe(&replacement, SeelenEvent::SystemMemoryChanged));
            hub.disconnect("overlap-screen", &old);
            assert!(hub.has_listeners(SeelenEvent::SystemMemoryChanged));
            hub.disconnect("overlap-screen", &replacement);
        }
        assert!(!hub.has_listeners(SeelenEvent::SystemMemoryChanged));
    }

    #[test]
    fn replacement_cannot_split_subscription_insert_and_global_count() {
        overlap_insert_with_close(true);
    }

    #[test]
    fn disconnect_cannot_split_subscription_insert_and_global_count() {
        overlap_insert_with_close(false);
    }

    #[test]
    fn late_receive_on_replaced_or_disconnected_connection_is_rejected() {
        let hub = EventsHub::default();
        let old = hub.connect("late-recv");
        let current = hub.connect("late-recv");
        assert!(!hub.subscribe(&old, SeelenEvent::SystemGpusChanged));
        assert!(!hub.has_listeners(SeelenEvent::SystemGpusChanged));
        assert!(hub.subscribe(&current, SeelenEvent::SystemGpusChanged));
        hub.disconnect("late-recv", &current);
        assert!(!hub.subscribe(&current, SeelenEvent::SystemGpusChanged));
        assert!(!hub.has_listeners(SeelenEvent::SystemGpusChanged));
    }
}
