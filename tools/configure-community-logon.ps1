param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'system-runtime.ps1')
if($null -ne (Get-MagicIndependentRuntime $root)){& (Join-Path $PSScriptRoot 'system-runtime/configure-logon.ps1') -Apply:$Apply;return}
$name='Magic Community Desktop'
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher=Join-Path $root 'tools/launch-magic.ps1'
$legacyArguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$launcher+'" -Mode Desktop -NonInteractive'
$arguments=$legacyArguments+' -Logon'
$oldTask=Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
if($oldTask -and ($oldTask.Description -ne ('Managed Magic Community entry: '+$root) -or @($oldTask.Actions).Count -ne 1 -or $oldTask.Actions[0].Execute -ne $powershell -or $oldTask.Actions[0].Arguments -cnotin @($legacyArguments,$arguments))){throw 'Task name is owned by a different entry; retained.'}
$startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'Magic 视界社区版.lnk'
if(Test-Path -LiteralPath $startup){
    $shell=New-Object -ComObject WScript.Shell;$shortcut=$shell.CreateShortcut($startup)
    if($shortcut.TargetPath -ne $powershell -or $shortcut.Arguments -cnotin @($legacyArguments,$arguments)){throw 'Startup shortcut ownership differs; retained.'}
}
if(-not $Apply){@{taskName=$name;user=$user;runLevel='Limited';logonType='Interactive';target=$powershell;arguments=$arguments;versionIndependent=$true;removeOnlyOwnedShortcut=$startup;originalSignedServiceRetained=$true;mode='plan_only'}|ConvertTo-Json;return}
$selected=Get-Content -LiteralPath (Join-Path $root 'runtime/desktop/host.current.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($selected.startupResources -ne $true){throw 'Community-first host is not active; no startup entry changed.'}
$backup=Join-Path $root ('evidence/startup/2026-10-18/logon-backup-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($backup)
Export-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service'|Set-Content -LiteralPath (Join-Path $backup 'original-service-task.xml') -Encoding UTF8
if($oldTask){Export-ScheduledTask -TaskPath '\' -TaskName $name|Set-Content -LiteralPath (Join-Path $backup 'community-task-before.xml') -Encoding UTF8}
if(Test-Path -LiteralPath $startup){Copy-Item -LiteralPath $startup -Destination (Join-Path $backup 'startup-before.lnk')}
$action=New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $root
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 4)
$task=New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description ('Managed Magic Community entry: '+$root)
try {
    Register-ScheduledTask -TaskName $name -TaskPath '\' -InputObject $task -Force|Out-Null
    $verify=Get-ScheduledTask -TaskName $name -TaskPath '\'
    if($verify.Principal.RunLevel.ToString() -ne 'Limited' -or $verify.Principal.LogonType.ToString() -ne 'Interactive' -or $verify.Actions[0].Arguments -ne $arguments){throw 'Least-privilege logon task did not verify.'}
    & (Join-Path $root 'tools/dotnet/dotnet.exe') (Join-Path $root 'tools/boot-service-control/bin/MagicBootServiceControl.dll') --disable-original-logon
    if($LASTEXITCODE -ne 0){throw 'Original signed service rejected startup handoff.'}
    $service=Get-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service'
    if(@($service.Triggers|Where-Object {$null -ne $_}).Count -ne 0){throw 'Original service still has an independent logon trigger.'}
    # Exact owned shortcut was copied above; remove only after task/API verification.
    if(Test-Path -LiteralPath $startup){Remove-Item -LiteralPath $startup -Force}
} catch {
    $failure=$_
    & (Join-Path $root 'tools/dotnet/dotnet.exe') (Join-Path $root 'tools/boot-service-control/bin/MagicBootServiceControl.dll') --restore-original-logon
    if(-not $oldTask){Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction SilentlyContinue}
    throw $failure
}
$record=@{at=(Get-Date).ToString('o');taskName=$name;owner=$root;user=$user;runLevel='Limited';logonType='Interactive';target=$powershell;arguments=$arguments;originalServiceDemandStart=$true;originalServiceLogonTrigger=$false;oldShortcutRemoved=$true;backup=$backup;rebootAcceptance='pending'}
$record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root 'runtime/desktop/community-logon.current.json') -Encoding UTF8
$record|ConvertTo-Json -Depth 5
