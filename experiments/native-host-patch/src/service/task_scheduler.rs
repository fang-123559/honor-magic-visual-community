use windows::Win32::{
    Foundation::{VARIANT_FALSE, VARIANT_TRUE},
    System::TaskScheduler::{
        IExecAction2, ITaskFolder, ITaskService, TASK_ACTION_EXEC, TASK_CREATE_OR_UPDATE,
        TASK_LOGON_INTERACTIVE_TOKEN, TASK_RUNLEVEL_HIGHEST, TASK_TRIGGER_LOGON, TaskScheduler,
    },
    UI::Shell::FOLDERID_LocalAppData,
};
use windows_core::{BSTR, Interface};

use crate::{
    enviroment::was_installed_using_msix,
    error::Result,
    windows_api::{WindowsApi, com::Com},
};

pub struct TaskSchedulerHelper {}


static OLD_APP_TASK_NAME: &str = "Seelen-UI";


impl TaskSchedulerHelper {
    unsafe fn get_task_service() -> Result<ITaskService> {
        unsafe {
            let task_service: ITaskService = Com::create_instance(&TaskScheduler)?;
            task_service.Connect(
                &Default::default(),
                &Default::default(),
                &Default::default(),
                &Default::default(),
            )?;
            Ok(task_service)
        }
    }

    unsafe fn register_task(folder: &ITaskFolder, task_name: &str, task_xml: &BSTR) -> Result<()> {
        unsafe {
            folder.RegisterTask(
                &task_name.into(),
                task_xml,
                TASK_CREATE_OR_UPDATE.0,
                &Default::default(),
                &Default::default(),
                TASK_LOGON_INTERACTIVE_TOKEN,
                &Default::default(),
            )?;
            log::debug!("Registered task {}", task_name);
            Ok(())
        }
    }

    /// this task handles the startup of the service and the app on login
    pub fn create_service_task() -> Result<()> {
        log::debug!("Creating service task...");
        let service_path = if slu_ipc::community::enabled() {
            slu_ipc::community::shell_executable("service")?
        } else if was_installed_using_msix() {
            WindowsApi::known_folder(FOLDERID_LocalAppData)?
                .join("Microsoft\\WindowsApps\\slu-service.exe")
        } else {
            std::env::current_exe()?
        };
        Com::run_with_context(|| unsafe {
            let task_service = Self::get_task_service()?;
            // remove old task as backwards compatibility
            let mut old_task = None;
            if !slu_ipc::community::enabled() && let Ok(seelen_folder) = task_service.GetFolder(&slu_ipc::community::task_folder().into()) {
                let _ = seelen_folder.DeleteTask(&OLD_APP_TASK_NAME.into(), 0);
                old_task = seelen_folder.GetTask(&OLD_APP_TASK_NAME.into()).ok();
            };
            let root_folder = task_service.GetFolder(&"\\".into())?;
            if slu_ipc::community::enabled() {
                let folder=match task_service.GetFolder(&slu_ipc::community::task_folder().into()) {
                    Ok(folder)=>folder,
                    Err(_)=>root_folder.CreateFolder(&slu_ipc::community::task_folder().into(), &Default::default())?,
                };
                // Recreating a task must preserve the user's current startup choice.
                old_task=folder.GetTask(&slu_ipc::community::task_name().into()).ok();
            }

            let task = task_service.NewTask(0)?;
            task.Principal()?.SetRunLevel(TASK_RUNLEVEL_HIGHEST)?;

            let settings = task.Settings()?;
            settings.SetPriority(2)?;
            settings.SetHidden(VARIANT_TRUE)?;
            settings.SetAllowDemandStart(VARIANT_TRUE)?;
            settings.SetDisallowStartIfOnBatteries(VARIANT_FALSE)?;
            settings.SetStopIfGoingOnBatteries(VARIANT_FALSE)?;

            let triggers = task.Triggers()?;
            if let Some(old) = old_task {
                let old_triggers = old.Definition()?.Triggers()?;
                task.SetTriggers(&old_triggers)?;
            } else if !slu_ipc::community::enabled() {
                triggers.Create(TASK_TRIGGER_LOGON)?;
            }

            let actions = task.Actions()?;
            let exec_action: IExecAction2 = actions.Create(TASK_ACTION_EXEC)?.cast()?;
            exec_action.SetPath(&service_path.to_string_lossy().to_string().into())?;
            exec_action.SetArguments(&"--startup".into())?;

            let mut task_xml = BSTR::new();
            task.XmlText(&mut task_xml)?;
            Self::register_task(
                &root_folder,
                &format!("{}\\{}", slu_ipc::community::task_folder(), slu_ipc::community::task_name()),
                &task_xml,
            )?;
            Ok(())
        })
    }

    pub fn set_run_on_logon(enabled: bool) -> Result<()> {
        if slu_ipc::community::enabled() && enabled {
            let manifest=slu_ipc::community::manifest()?;
            for capability in slu_ipc::community::CORE_CAPABILITIES {
                if !manifest.accepted(capability)? { return Err("Unaccepted system runtime cannot enable automatic startup".into()); }
            }
        }
        Com::run_with_context(|| unsafe {
            let task_service = Self::get_task_service()?;
            let (folder_name,task_name)=if slu_ipc::community::enabled(){("\\","Magic Community Desktop")}else{(slu_ipc::community::task_folder(),slu_ipc::community::task_name())};
            let seelen_folder = task_service.GetFolder(&folder_name.into())?;
            let task = seelen_folder.GetTask(&task_name.into())?;
            let task = task.Definition()?;
            if slu_ipc::community::enabled(){
                let mut run_level=windows::Win32::System::TaskScheduler::TASK_RUNLEVEL_TYPE::default();task.Principal()?.RunLevel(&mut run_level)?;
                if run_level != windows::Win32::System::TaskScheduler::TASK_RUNLEVEL_LUA { return Err("Community desktop startup must remain unelevated".into()); }
                let actions=task.Actions()?;
                let mut count=0;actions.Count(&mut count)?;
                if count!=1 { return Err("Community startup action count differs".into()); }
                let action:IExecAction2=actions.get_Item(1)?.cast()?;
                let mut path=BSTR::new();let mut arguments=BSTR::new();action.Path(&mut path)?;action.Arguments(&mut arguments)?;
                let expected=std::path::PathBuf::from(std::env::var_os("WINDIR").ok_or("Windows directory unavailable")?).join(r"System32\WindowsPowerShell\v1.0\powershell.exe");
                let root=slu_ipc::community::manifest()?.project_root.to_string_lossy();let root=root.strip_prefix(r"\\?\").unwrap_or(&root);
                let expected_args=format!(r#"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{}\tools\launch-magic.ps1" -Mode Desktop -NonInteractive"#,root);
                if !path.to_string().eq_ignore_ascii_case(&expected.to_string_lossy()) || arguments.to_string()!=expected_args { return Err("Community startup entry identity differs".into()); }
            }
            let triggers = task.Triggers()?;
            triggers.Clear()?;
            if enabled {
                triggers.Create(TASK_TRIGGER_LOGON)?;
            }
            let mut task_xml = BSTR::new();
            task.XmlText(&mut task_xml)?;
            Self::register_task(&seelen_folder, task_name, &task_xml)?;
            Ok(())
        })
    }

    pub fn remove_service_task() -> Result<()> {
        Com::run_with_context(|| unsafe {
            let task_service: ITaskService = Com::create_instance(&TaskScheduler)?;
            task_service.Connect(
                &Default::default(),
                &Default::default(),
                &Default::default(),
                &Default::default(),
            )?;
            if let Ok(seelen_folder) = task_service.GetFolder(&slu_ipc::community::task_folder().into()) {
                let _ = seelen_folder.DeleteTask(&slu_ipc::community::task_name().into(), 0);
            }
            Ok(())
        })
    }
}
