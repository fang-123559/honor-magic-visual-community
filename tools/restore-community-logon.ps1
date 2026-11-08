param([Parameter(Mandatory=$true)][string]$BackupPath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$recordPath=Join-Path $root 'runtime/desktop/community-logon.current.json'
$record=Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8|ConvertFrom-Json
$backup=[IO.Path]::GetFullPath($BackupPath)
$prefix=[IO.Path]::GetFullPath((Join-Path $root 'evidence/startup/2026-10-18'))+'\'
if($record.owner -ne $root -or $record.backup -ne $backup -or -not $backup.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Rollback backup ownership differs.'}
$task=Get-ScheduledTask -TaskName $record.taskName -TaskPath '\' -ErrorAction SilentlyContinue
if($task -and ($task.Description -ne ('Managed Magic Community entry: '+$root) -or $task.Actions[0].Arguments -ne $record.arguments)){throw 'Task has changed since installation; retained.'}
$startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'Magic 视界社区版.lnk'
if(Test-Path -LiteralPath $startup){
    $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($startup)
    if($shortcut.TargetPath -ne $record.target -or $shortcut.Arguments -ne $record.arguments){throw 'Startup shortcut has another owner; retained.'}
}
& (Join-Path $root 'tools/dotnet/dotnet.exe') (Join-Path $root 'tools/boot-service-control/bin/MagicBootServiceControl.dll') --restore-original-logon
if($LASTEXITCODE -ne 0){throw 'Original signed service must be running and verified for startup rollback.'}
if(Test-Path -LiteralPath (Join-Path $backup 'community-task-before.xml')){
    Register-ScheduledTask -TaskName $record.taskName -TaskPath '\' -Xml ([IO.File]::ReadAllText((Join-Path $backup 'community-task-before.xml'))) -Force|Out-Null
}elseif($task){Unregister-ScheduledTask -TaskName $record.taskName -TaskPath '\' -Confirm:$false}
if(Test-Path -LiteralPath (Join-Path $backup 'startup-before.lnk')){Copy-Item -LiteralPath (Join-Path $backup 'startup-before.lnk') -Destination $startup -Force}
@{at=(Get-Date).ToString('o');owner=$root;restoredStartupEntries=$true;backup=$backup;originalSignedServiceRetained=$true;hostAndAppPointersChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $backup 'rollback-result.json') -Encoding UTF8
'Original startup entries restored; app/host pointers and user data retained.'
