/// Compile the owned runtime with its own application identity and data namespace.
pub fn get_context() -> tauri::Context {
    #[cfg(feature = "community-system")]
    { tauri::generate_context!("tauri.community.conf.json") }
    #[cfg(not(feature = "community-system"))]
    { tauri::generate_context!() }
}
