use std::path::PathBuf;

use slu_ipc::{IPC, ServiceIpc, messages::SvcAction};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
use windows::Win32::{
    System::TaskScheduler::{IExecAction2, ITaskService, TaskScheduler},
    UI::Shell::FOLDERID_LocalAppData,
};
use windows_core::Interface;

use crate::{
    app::get_app_handle,
    error::Result,
    get_tokio_handle,
    utils::was_installed_using_msix,
    windows_api::{Com, WindowsApi},
};

pub struct ServicePipe;

impl ServicePipe {
    /// will ignore any response
    pub fn request(message: SvcAction) -> Result<()> {
        get_tokio_handle().spawn(async move {
            if let Err(err) = ServiceIpc::send(message.clone()).await {
                log::error!("Error sending message to service {err}. Message: {message:?}");
            }
        });
        Ok(())
    }

    pub fn request_sync(message: SvcAction) -> Result<()> {
        ServiceIpc::send_blocking(message.clone())?.ok()?;
        Ok(())
    }

    pub fn is_running() -> bool {
        ServiceIpc::can_stablish_connection()
    }

    pub fn service_path() -> Result<PathBuf> {
        if slu_ipc::community::enabled() { return Ok(slu_ipc::community::shell_executable("service")?); }
        if let Some(path) = crate::resources::community_boot::service_path()? { return Ok(path); }
        let service_path = if was_installed_using_msix() {
            WindowsApi::known_folder(FOLDERID_LocalAppData)?
                .join("Microsoft\\WindowsApps\\slu-service.exe")
        } else {
            std::env::current_exe()?.with_file_name("slu-service.exe")
        };
        Ok(service_path)
    }

    fn try_start_from_task_scheduler() -> Result<()> {
        Com::run_with_context(|| unsafe {
            let task_service: ITaskService = Com::create_instance(&TaskScheduler)?;
            task_service.Connect(
                &Default::default(),
                &Default::default(),
                &Default::default(),
                &Default::default(),
            )?;
            let folder = task_service.GetFolder(&slu_ipc::community::task_folder().into())?;
            let task = folder.GetTask(&slu_ipc::community::task_name().into())?;

            let actions = task.Definition()?.Actions()?;
            // ask to microsoft what that hell this start counting from 1 instead 0
            let action: IExecAction2 = actions.get_Item(1)?.cast()?;
            let mut action_path = windows_core::BSTR::new();
            action.Path(&mut action_path)?;

            let service_path = Self::service_path()?.to_string_lossy().to_lowercase();
            // The unchanged MSIX task uses its App Execution Alias, while the
            // owned community host validates the real signed package image.
            let fixed_alias = std::env::var_os("LOCALAPPDATA").map(|base| {
                PathBuf::from(base).join("Microsoft").join("WindowsApps").join("slu-service.exe").to_string_lossy().to_lowercase()
            });
            let community_alias = !slu_ipc::community::enabled() && crate::resources::community_boot::service_path()?.is_some()
                && fixed_alias.as_deref() == Some(action_path.to_string().to_lowercase().as_str());
            match action_path.to_string().to_lowercase() == service_path || community_alias {
                true => {
                    task.Run(&Default::default())?;
                    Ok(())
                }
                false => {
                    Err("Service task is not pointing to the correct service executable".into())
                }
            }
        })
    }

    // the service should be running since installer will start it or startup task scheduler
    // so if the service is not running, we need to start it (common on msix setup)
    pub async fn start_service() -> Result<()> {
        let Err(err) = Self::try_start_from_task_scheduler() else {
            return Ok(());
        };

        log::debug!("Can not start service via task scheduler: {err}");

        let service_path = Self::service_path()?;
        let result =
            WindowsApi::execute(service_path.to_string_lossy().to_string(), None, None, true);

        if let Err(err) = result {
            log::error!("First attempt to start service failed: {err}");
            let try_again = get_app_handle()
                .dialog()
                .message(t!("service.not_running_description"))
                .title(t!("service.not_running"))
                .kind(MessageDialogKind::Info)
                .buttons(MessageDialogButtons::OkCustom(
                    t!("service.not_running_ok").to_string(),
                ))
                .blocking_show();

            if try_again {
                WindowsApi::execute(service_path.to_string_lossy().to_string(), None, None, true)?;
            }
            return Err(err);
        }

        let mut counter = 0;
        while !Self::is_running() && counter < 10 {
            log::debug!("Waiting for service IPC...");
            counter += 1;
            tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        }

        if counter == 10 {
            get_app_handle()
                .dialog()
                .message(t!("service.not_running_description"))
                .title(t!("service.not_running"))
                .kind(MessageDialogKind::Error)
                .buttons(MessageDialogButtons::Ok)
                .blocking_show();
            return Err("Service not running".into());
        }

        Ok(())
    }
}
