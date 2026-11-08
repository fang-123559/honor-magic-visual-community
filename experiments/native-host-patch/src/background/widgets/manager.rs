use std::sync::{
    Arc, LazyLock,
    atomic::{AtomicBool, Ordering},
};

use seelen_core::{
    resource::WidgetId,
    state::{WidgetLoader, WidgetStatus},
};

use crate::{
    error::{Result, ResultLogExt},
    modules::monitors::MonitorManager,
    resources::RESOURCES,
    state::application::FULL_STATE,
    utils::lock_free::SyncHashMap,
    widgets::{WidgetWebviewLabel, loader::WidgetDeployment},
};

pub static WIDGET_MANAGER: LazyLock<WidgetManager> = LazyLock::new(WidgetManager::create);
pub static GAME_MODE_ACTIVE: AtomicBool = AtomicBool::new(false);

pub struct WidgetManager {
    /// group of widgets instances by widget resource id
    pub deployments: SyncHashMap<WidgetId, Arc<WidgetDeployment>>,
    reconcile_tx: crossbeam_channel::Sender<()>,
}

impl WidgetManager {
    fn create() -> Self {
        let (reconcile_tx, reconcile_rx) = crossbeam_channel::bounded(1);
        // Serialize webview creation and never run it on the monitor dispatcher.
        // No idle timer: this worker sleeps until a reconcile is requested.
        std::thread::spawn(move || {
            while reconcile_rx.recv().is_ok() {
                while reconcile_rx.try_recv().is_ok() {}
                let mut deployments = WIDGET_MANAGER.deployments.values();
                deployments.sort_by_key(|deployment| {
                    [
                        WidgetId::known_wall(),
                        WidgetId::known_toolbar(),
                        WidgetId::known_weg(),
                    ]
                    .iter()
                    .position(|id| *id == deployment.definition.id)
                    .unwrap_or(3)
                });
                super::native_shell::start();
                let native_shell = super::native_shell::selection();
                let native_popups = super::native_popups::selection();
                for deployment in deployments {
                    // Selection can change after editing toolbar/Dock modules.
                    // Retire only the compatibility windows now owned by the
                    // native shell; lazy popup fallbacks remain independent.
                    if native_shell.contains(&deployment.definition.id.to_string()) || (deployment.definition.id.to_string()=="@seelen/apps-menu"&&native_popups.contains("@seelen/apps-menu")) {
                        deployment.retire_webviews();
                        continue;
                    }
                    deployment.reconcile();
                    if !deployment.definition.lazy
                        && !native_popups.contains(&deployment.definition.id.to_string())
                        && !GAME_MODE_ACTIVE.load(Ordering::Acquire)
                    {
                        deployment.start_all_webviews();
                    }
                }
            }
        });
        let sub_id = MonitorManager::subscribe(|_event| {
            WIDGET_MANAGER.reconcile().log_error();
        });
        MonitorManager::set_event_handler_priority(&sub_id, 1);
        Self {
            deployments: SyncHashMap::new(),
            reconcile_tx,
        }
    }

    pub fn is_ready(&self, label: &WidgetWebviewLabel) -> bool {
        self.deployments
            .get(&label.widget_id, |deployment| deployment.clone())
            .map(|deploy| deploy.pods.any(|(key, pod)| key == label && pod.is_ready()))
            .unwrap_or(false)
    }

    pub fn set_status(&self, label: &WidgetWebviewLabel, status: WidgetStatus) {
        if let Some(deploy) = self
            .deployments
            .get(&label.widget_id, |deployment| deployment.clone())
        {
            deploy.pods.get(label, |instance| {
                instance.set_status(status);
            });
        }
    }

    pub fn restart_ready_after_resume(&self) {
        let mut windows = Vec::new();
        for deployment in self.deployments.values() {
            deployment.pods.for_each(|(_, pod)| {
                if let Some(window) = pod.prepare_resume_restart() {
                    windows.push(window);
                }
            });
        }
        // Handles were cloned and statuses marked under the maps; dispatch
        // actual reloads only after both global locks have been released.
        for window in windows {
            window.reload().log_error();
        }
    }

    pub fn suspend_all(&self) {
        GAME_MODE_ACTIVE.store(true, Ordering::Release);
        for deploy in self.deployments.values() {
            deploy.pods.clear();
        }
    }

    pub fn resume_all(&self) -> Result<()> {
        GAME_MODE_ACTIVE.store(false, Ordering::Release);
        self.reconcile()
    }

    pub fn reconcile(&self) -> Result<()> {
        // remove deleted resources
        self.deployments
            .retain(|(key, _)| RESOURCES.widgets.contains_sync(key));

        let mut filtered = Vec::new();
        RESOURCES.widgets.iter_sync(|k, w| {
            if w.loader != WidgetLoader::Legacy {
                filtered.push((k.clone(), w.clone()));
            }
            true
        });

        let state = FULL_STATE.load();
        for (id, widget) in filtered {
            if !state.is_widget_enabled(&id) {
                self.deployments.remove(&id);
                continue;
            }

            if !self.deployments.contains_key(&id) {
                self.deployments
                    .upsert(id.clone(), Arc::new(WidgetDeployment::new(widget)));
            }
        }

        // Only short Arc snapshots hold the global map. Building a WebView can
        // block on the UI thread for seconds; doing that inside map.get used to
        // time out retain/for_each and terminate the monitor event dispatcher.
        let _ = self.reconcile_tx.try_send(());

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn deployment_snapshot_does_not_lock_monitor_dispatch_during_window_work() {
        let registry = Arc::new(SyncHashMap::new());
        registry.upsert(1, Arc::new(()));
        let (started_tx, started_rx) = std::sync::mpsc::channel();
        let (finish_tx, finish_rx) = std::sync::mpsc::channel();
        let worker_registry = registry.clone();
        let worker = std::thread::spawn(move || {
            let snapshot = worker_registry.values();
            started_tx.send(()).unwrap();
            finish_rx.recv().unwrap(); // stand-in for a blocked WebView build
            drop(snapshot);
        });
        started_rx.recv_timeout(Duration::from_secs(1)).unwrap();
        let (dispatch_tx, dispatch_rx) = std::sync::mpsc::channel();
        let dispatcher_registry = registry.clone();
        let dispatcher = std::thread::spawn(move || {
            dispatcher_registry.retain(|_| true);
            dispatch_tx.send(()).unwrap();
        });
        let unblocked = dispatch_rx.recv_timeout(Duration::from_millis(500)).is_ok();
        finish_tx.send(()).unwrap();
        worker.join().unwrap();
        dispatcher.join().unwrap();
        assert!(
            unblocked,
            "monitor dispatch must not wait for WebView creation"
        );
    }
}
