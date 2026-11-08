use std::{
    sync::Arc,
    time::{Duration, Instant},
};

use super::liveness_policy::{should_restart_on_resume, LivenessPolicy, Outcome};
use super::resource_policy::{self, ProbeCadence};

use seelen_core::state::{Widget, WidgetInstanceMode, WidgetStatus};
use tauri::{Emitter, Listener};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
use uuid::Uuid;

use crate::{
    app::get_app_handle,
    error::ResultLogExt,
    get_tokio_handle,
    modules::monitors::MonitorManager,
    resources::RESOURCES,
    state::application::FULL_STATE,
    utils::lock_free::SyncHashMap,
    widgets::{
        WidgetWebviewLabel,
        manager::WIDGET_MANAGER,
        notify_widget_statuses_change,
        webview::{self, WidgetWebview},
    },
    windows_api::event_window::IS_INTERACTIVE_SESSION,
};

pub enum PodSource {
    Static,
    Runtime,
}

const LIVENESS_PROVE_INTERVAL: Duration = Duration::from_secs(5);
const LIVENESS_PROVE_WAIT_TIMEOUT: Duration = Duration::from_secs(3);
const LIVENESS_PROVE_MAX_RETRIES: u8 = 5;
// Grace period after creation, session resume or reload to finish navigation.
const LIVENESS_RELOAD_GRACE_PERIOD: Duration = Duration::from_secs(10);
// Widgets sit occluded/background almost all the time, so the OS never signals real
// memory pressure to their renderer and the JS heap balloons unchecked. Simulating it
// periodically forces WebView2 to actually release freed pages back to the OS.
const MEMORY_PRESSURE_INTERVAL: Duration = Duration::from_secs(30);

pub struct WidgetDeployment {
    pub definition: Arc<Widget>,
    pub pods: SyncHashMap<WidgetWebviewLabel, WidgetPod>,
}

impl WidgetDeployment {
    pub fn new(definition: Arc<Widget>) -> Self {
        log::trace!("Registering widget: {}", definition.id);
        Self {
            definition,
            pods: SyncHashMap::new(),
        }
    }

    /// Will revaluate all widget instances and remove or add them based on current user settings
    pub fn reconcile(&self) {
        match self.definition.instances {
            WidgetInstanceMode::Single => {
                if self.pods.is_empty() {
                    let label = WidgetWebviewLabel::new(&self.definition.id, None, None);
                    let instance = WidgetPod::create(label, None, PodSource::Static);
                    self.pods.upsert(instance.label.clone(), instance);
                }
            }
            WidgetInstanceMode::Multiple => {
                let nil_id = Uuid::nil();
                if !self.definition.lazy && self.pods.is_empty() {
                    let label = WidgetWebviewLabel::new(&self.definition.id, None, Some(&nil_id));
                    let instance = WidgetPod::create(label, None, PodSource::Static);
                    self.pods.upsert(instance.label.clone(), instance);
                }

                let replicas_ids = FULL_STATE
                    .load()
                    .get_widget_instances_ids(&self.definition.id);

                // Remove deleted static instances; runtime pods are never evicted by reconcile.
                self.pods.retain(|(label, pod)| {
                    if matches!(pod.source, PodSource::Runtime) {
                        return true;
                    }
                    let instance_id = label.instance_id.expect("Missing instance id");
                    instance_id == nil_id || replicas_ids.contains(&instance_id)
                });

                // Add new instances
                for replica_id in replicas_ids {
                    if !self
                        .pods
                        .any(|(label, _)| label.instance_id == Some(replica_id))
                    {
                        let label =
                            WidgetWebviewLabel::new(&self.definition.id, None, Some(&replica_id));
                        let instance = WidgetPod::create(label, None, PodSource::Static);
                        self.pods.upsert(instance.label.clone(), instance);
                    }
                }
            }
            WidgetInstanceMode::ReplicaByMonitor => {
                let configs = FULL_STATE.load();
                let connected_ids = MonitorManager::instance().get_cached_ids();

                // Remove disabled or disconnected instances
                self.pods.retain(|(label, _)| {
                    let monitor_id = label.monitor_id.as_ref().expect("Missing monitor id");
                    connected_ids.contains(monitor_id)
                        && configs.is_widget_enable_on_monitor(&self.definition.id, monitor_id)
                });

                // Add new/enabled instances
                for monitor_id in connected_ids {
                    if self
                        .pods
                        .any(|(label, _)| label.monitor_id.as_ref() == Some(&monitor_id))
                    {
                        continue;
                    }

                    if !configs.is_widget_enable_on_monitor(&self.definition.id, &monitor_id) {
                        continue;
                    }

                    let label =
                        WidgetWebviewLabel::new(&self.definition.id, Some(&monitor_id), None);
                    let instance = WidgetPod::create(label, None, PodSource::Static);
                    self.pods.upsert(instance.label.clone(), instance);
                }
            }
        }
    }

    pub fn start_all_webviews(&self) {
        self.pods.for_each(|(_k, pod)| {
            pod.run(&self.definition);
        });
    }

    pub fn start_webview(&self, label: &WidgetWebviewLabel) {
        self.pods.get(label, |pod| {
            pod.run(&self.definition);
        });
    }

    pub fn create_runtime_instance(&self, instance_id: &Uuid, owner_hwnd: Option<isize>) {
        let label = WidgetWebviewLabel::new(&self.definition.id, None, Some(instance_id));
        let instance = WidgetPod::create(label, owner_hwnd, PodSource::Runtime);
        self.pods.upsert(instance.label.clone(), instance);
    }

    pub(super) fn retire_webviews(&self) {
        // Take under the map lock, cancel probes and destroy windows outside it.
        // Destroyed callbacks must not remove a later pod with the same label.
        let retired = self.pods.take();
        let changed = !retired.is_empty();
        if changed {
            log::debug!("Native shell retired {} compatibility pods for {}", retired.len(), self.definition.id);
        }
        drop(retired);
        if changed { notify_widget_statuses_change(); }
    }

    fn remove_pod_if_current(&self, label: &WidgetWebviewLabel, expected: &Arc<parking_lot::Mutex<LivenessPolicy>>) -> bool {
        let removed = self.pods.with_lock(|pods| {
            if pods.get(label).is_some_and(|pod| Arc::ptr_eq(&pod.liveness, expected)) {
                pods.remove(label)
            } else {
                None
            }
        });
        let changed = removed.is_some();
        drop(removed);
        if changed { notify_widget_statuses_change(); }
        changed
    }
}

pub struct WidgetPod {
    pub label: WidgetWebviewLabel,
    pub source: PodSource,

    window: Option<WidgetWebview>,
    _status: WidgetStatus,
    owner_hwnd: Option<isize>,

    live: Arc<tokio::sync::Notify>,
    liveness_prove_handle: Option<tokio::task::JoinHandle<()>>,
    memory_pressure_handle: Option<tokio::task::JoinHandle<()>>,
    liveness: Arc<parking_lot::Mutex<LivenessPolicy>>,
}

impl WidgetPod {
    fn create(label: WidgetWebviewLabel, owner_hwnd: Option<isize>, source: PodSource) -> Self {
        Self {
            label,
            source,
            window: None,
            _status: WidgetStatus::Pending,
            owner_hwnd,
            live: Arc::new(tokio::sync::Notify::new()),
            liveness_prove_handle: None,
            memory_pressure_handle: None,
            liveness: Arc::new(parking_lot::Mutex::new(LivenessPolicy::new(
                Instant::now(),
                LIVENESS_RELOAD_GRACE_PERIOD,
                LIVENESS_PROVE_MAX_RETRIES,
            ))),
        }
    }

    pub fn status(&self) -> &WidgetStatus {
        &self._status
    }

    pub fn hwnd(&self) -> Option<isize> {
        self.window.as_ref().map(WidgetWebview::native_hwnd)
    }

    pub fn set_status(&mut self, status: WidgetStatus) {
        if status == WidgetStatus::Ready {
            self.liveness.lock().healthy(Instant::now());
        }
        log::trace!(target: &self.label.decoded, "status changed to: {status:?}");
        self._status = status;
        notify_widget_statuses_change();
    }

    pub fn is_ready(&self) -> bool {
        self.window.is_some() && self.status() == &WidgetStatus::Ready
    }

    fn prepare_soft_restart(&mut self) -> Option<tauri::WebviewWindow> {
        let window = self.window.as_ref()?.handle();
        self.liveness.lock().begin_reload(Instant::now());
        self.set_status(WidgetStatus::Restarting);
        Some(window)
    }

    pub(super) fn prepare_resume_restart(&mut self) -> Option<tauri::WebviewWindow> {
        if !should_restart_on_resume(*self.status()) {
            return None;
        }
        self.prepare_soft_restart()
    }

    fn prepare_liveness_restart(
        &mut self,
        expected: &Arc<parking_lot::Mutex<LivenessPolicy>>,
        probe_generation: u64,
    ) -> Option<tauri::WebviewWindow> {
        if !Arc::ptr_eq(expected, &self.liveness) {
            return None;
        }
        let window = self.window.as_ref()?.handle();
        // A Pong/Ready can arrive while this task waits for the maps. Validate
        // its failure decision and start grace atomically under the policy.
        if !self
            .liveness
            .lock()
            .commit_retry(Instant::now(), probe_generation)
        {
            return None;
        }
        self.set_status(WidgetStatus::Restarting);
        Some(window)
    }

    fn run(&mut self, definition: &Widget) {
        if self.status() != &WidgetStatus::Pending {
            return;
        }

        self.set_status(WidgetStatus::Creating);
        let window = match WidgetWebview::create(definition, &self.label, self.owner_hwnd) {
            Ok(window) => window,
            Err(err) => {
                log::error!("Failed to create webview: {}", err);
                self.set_status(WidgetStatus::CrashedOnCreation);
                return;
            }
        };
        self.set_status(WidgetStatus::Mounting);

        let label = self.label.clone();
        let identity = self.liveness.clone();
        window.0.on_window_event(move |event| {
            if let tauri::WindowEvent::Destroyed = event {
                // Defer window creation off the UI message-loop thread.
                // Calling start_all_webviews() (→ WidgetWebview::create → builder.build)
                // synchronously here triggers a re-entrant ZwUserDestroyWindow while the
                // message pump is still inside the destruction handler, which causes the
                // APPLICATION_HANG_ENDTASK_HungThreadIsIdle crash.
                let label = label.clone();
                let identity = identity.clone();
                std::thread::spawn(move || {
                    if let Some(deploy) = WIDGET_MANAGER.deployments
                        .get(&label.widget_id, |deployment| deployment.clone())
                    {
                        if deploy.remove_pod_if_current(&label, &identity) {
                            // Ownership and game-mode checks belong to the same
                            // serialized worker as every other reconcile.
                            WIDGET_MANAGER.reconcile().log_error();
                        }
                    }
                });
            }
        });

        if definition.debug {
            window.0.open_devtools();
        }

        self.liveness.lock().begin_reload(Instant::now());
        self.window = Some(window);
        notify_widget_statuses_change();
        self.start_liveness_prove();
        // Resident popups use event-driven, delayed memory targets instead of
        // repeated critical-pressure DevTools calls. Other widgets keep their policy.
        if !resource_policy::is_resident_popup(&self.label.widget_id.to_string()) {
            self.start_memory_pressure_simulation();
        }
    }

    fn start_memory_pressure_simulation(&mut self) {
        let Some(window) = &self.window else {
            return;
        };
        let webview = window.handle();

        let handle = get_tokio_handle().spawn(async move {
            loop {
                tokio::time::sleep(MEMORY_PRESSURE_INTERVAL).await;
                webview::simulate_memory_pressure(&webview);
            }
        });

        self.memory_pressure_handle = Some(handle);
    }

    fn start_liveness_prove(&mut self) {
        if let Some(window) = &self.window {
            let live = self.live.clone();
            let health = self.liveness.clone();
            window.0.listen("internal::liveness-pong", move |_event| {
                health.lock().healthy(Instant::now());
                live.notify_waiters();
            });
        }

        let live = self.live.clone();
        let label = self.label.clone();
        let health = self.liveness.clone();

        let hwnd = self.hwnd().expect("liveness starts after window creation");
        let managed_popup = resource_policy::is_resident_popup(&label.widget_id.to_string());
        let handle = get_tokio_handle().spawn(async move {
            let app = get_app_handle();
            let mut cadence = ProbeCadence::default();
            let mut was_suspended = false;

            loop {
                tokio::time::sleep(LIVENESS_PROVE_INTERVAL).await;
                if !IS_INTERACTIVE_SESSION.load(std::sync::atomic::Ordering::Acquire) {
                    was_suspended = true;
                    continue;
                }

                // After session resume, reset state and wait for webview to finish reloading.
                if was_suspended {
                    was_suspended = false;
                    cadence.reset();
                    health.lock().reset_after_resume(Instant::now());
                    continue;
                }

                let now = Instant::now();
                if !cadence.due(now, managed_popup && !resource_policy::is_visible(hwnd)) {
                    continue;
                }
                let Some(generation) = health.lock().can_probe(now) else {
                    continue;
                };
                // Arm before emit: a quick Pong must not be lost between
                // publishing the event and registering the Notify waiter.
                cadence.attempted(now);
                let pong = live.notified();
                tokio::pin!(pong);
                pong.as_mut().enable();
                let _ = app.emit_to(&label.raw, "internal::liveness-ping", ());

                tokio::select! {
                    _ = &mut pong => {}
                    _ = tokio::time::sleep(LIVENESS_PROVE_WAIT_TIMEOUT) => {
                        // A failed probe keeps the existing fast recovery cadence.
                        cadence.reset();
                        let outcome = health.lock().failed(Instant::now(), generation);
                        if matches!(outcome, Outcome::Ignore) {
                            continue;
                        }
                        if let Outcome::Reload { attempt } = outcome {
                            log::warn!("Liveness prove failed for {label} (reload {attempt}/{LIVENESS_PROVE_MAX_RETRIES}).");
                            let window = WIDGET_MANAGER.deployments
                                .get(&label.widget_id, |deployment| deployment.clone())
                                .and_then(|deployment| {
                                    deployment.pods
                                        .get(&label, |pod| {
                                            pod.prepare_liveness_restart(&health, generation)
                                        })
                                        .flatten()
                                });
                            if let Some(window) = window {
                                window.reload().log_error();
                            }
                        } else {
                            if !health.lock().terminal_is_current(generation) {
                                continue;
                            }
                            log::error!("Liveness prove failed for {label} too many times, giving up.");
                            let lang = rust_i18n::locale();
                            let widget_name = RESOURCES
                                .widgets
                                .read_async(&label.widget_id, |_, w| {
                                    w.metadata.display_name.get(&lang).to_string()
                                })
                                .await
                                .unwrap_or_else(|| label.widget_id.to_string());
                            if !health.lock().terminal_is_current(generation) {
                                continue;
                            }
                            app.dialog()
                                .message(t!("widget_liveness.failed_description", widget_name = widget_name))
                                .title(t!("widget_liveness.failed_title"))
                                .kind(MessageDialogKind::Error)
                                .buttons(MessageDialogButtons::Ok)
                                .show(|_| {});
                            break;
                        }
                    }
                }
            }
        });

        self.liveness_prove_handle = Some(handle);
    }
}

impl Drop for WidgetPod {
    fn drop(&mut self) {
        log::trace!(target: &self.label.decoded, "dropped");
        if let Some(handle) = self.liveness_prove_handle.take() {
            handle.abort();
        }
        if let Some(handle) = self.memory_pressure_handle.take() {
            handle.abort();
        }
    }
}
