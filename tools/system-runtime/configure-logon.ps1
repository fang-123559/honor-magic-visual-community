param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/system-runtime.ps1')
$runtime=Get-MagicIndependentRuntime $root
if($null -eq $runtime -or $runtime.ValidationOnly){throw 'An accepted production runtime must be selected first; validation sessions cannot register logon.'}
$name='Magic Community Desktop'
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$legacyArguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $root 'tools/launch-magic.ps1')+'" -Mode Desktop -NonInteractive'
$arguments=$legacyArguments+' -Logon'
$old=Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
if($old -and ($old.Description -ne ('Managed Magic Community entry: '+$root) -or @($old.Actions).Count -ne 1 -or $old.Actions[0].Execute -ne $powershell -or $old.Actions[0].Arguments -cnotin @($legacyArguments,$arguments) -or $old.Principal.RunLevel.ToString() -ne 'Limited')){throw 'Existing community startup task differs; retained.'}
if(-not $Apply){@{task=$name;runLevel='Limited';target=$powershell;arguments=$arguments;serviceDemandStart=$true;legacyTasksChanged=$false;mode='plan_only'}|ConvertTo-Json;return}
. (Join-Path $root 'tools/dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $root
$backup=Join-Path $root ('evidence/system-startup/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($backup)
if($old){Export-ScheduledTask -TaskName $name -TaskPath '\'|Set-Content -LiteralPath (Join-Path $backup 'community-task-before.xml') -Encoding UTF8}
$action=New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $root
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 4)
$task=New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description ('Managed Magic Community entry: '+$root)
Register-ScheduledTask -TaskName $name -TaskPath '\' -InputObject $task -Force|Out-Null
$verify=Get-ScheduledTask -TaskName $name -TaskPath '\'
if($verify.Principal.RunLevel.ToString() -ne 'Limited' -or $verify.Actions[0].Arguments -ne $arguments){throw 'Independent startup verification failed; backup retained.'}
@{owner=$root;instanceId=$runtime.InstanceId;task=$name;runLevel='Limited';backup=$backup;legacyTasksChanged=$false;rebootAcceptance='pending'}|ConvertTo-Json
