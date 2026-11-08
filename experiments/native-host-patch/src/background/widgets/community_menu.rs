use std::time::Duration;

use super::{desktop_drag::BridgeSession, webview::WidgetWebviewLabel};
use crate::error::Result;

impl crate::tauri_handlers::Handlers {
    /// Fixed, authenticated local adapter actions; never accept URLs or credentials from JavaScript.
    pub async fn magic_community_menu_request(
        webview: tauri::WebviewWindow,
        kind: String,
    ) -> Result<String> {
        let owner = WidgetWebviewLabel::try_from_raw(webview.label())?;
        if owner.widget_id.to_string() != "@seelen/user-menu" {
            return Err("Only the user menu can call the community menu adapter".into());
        }
        if !matches!(kind.as_str(), "state" | "account" | "feedback" | "exit") {
            return Err("Unsupported community menu action".into());
        }
        let session = BridgeSession::current()?;
        let client = reqwest::Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(Duration::from_secs(3))
            .build()?;
        let response = client
            .post(format!("{}/community-menu", session.endpoint.trim_end_matches('/')))
            .bearer_auth(&session.token)
            .json(&serde_json::json!({ "kind": kind }))
            .send()
            .await?
            .error_for_status()?;
        let text = response.text().await?;
        if text.len() > 16_384 {
            return Err("Community menu response exceeded its limit".into());
        }
        Ok(text)
    }
}
