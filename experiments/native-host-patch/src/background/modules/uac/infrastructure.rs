use std::sync::Once;

use seelen_core::{
    handlers::SeelenEvent,
    system_state::{PendingUacRequest, UacActivationOutcome},
};

use crate::{
    app::emit_to_webviews,
    error::Result,
    widgets::permissions::{WidgetPerm, request_widget_permission},
};

use super::application::{PendingUacEvent, PendingUacManager};

fn get_manager() -> &'static PendingUacManager {
    static REGISTER: Once = Once::new();
    REGISTER.call_once(|| {
        PendingUacManager::subscribe(|event| {
            if matches!(event, PendingUacEvent::Changed) {
                emit_to_webviews(
                    SeelenEvent::PendingUacRequestsChanged,
                    PendingUacManager::instance().requests(),
                );
            }
        });
    });
    PendingUacManager::instance()
}

impl crate::tauri_handlers::Handlers {
    pub fn get_pending_uac_requests() -> Vec<PendingUacRequest> {
        get_manager().requests()
    }

    pub async fn open_pending_uac_request(
        webview: tauri::WebviewWindow,
        id: String,
    ) -> Result<UacActivationOutcome> {
        request_widget_permission(&webview, WidgetPerm::ManageAppWindows)?;
        let manager = get_manager();
        // The bounded window message must not block the host's event dispatcher or UI thread.
        tokio::task::spawn_blocking(move || manager.open(id)).await?
    }
}
