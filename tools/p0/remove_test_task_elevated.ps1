$ErrorActionPreference='Stop'
$projectRoot=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence=Join-Path $projectRoot 'evidence/p0/2026-10-14/seelen-runtime'
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrative token required' }
$before=Get-Content -LiteralPath (Join-Path $evidence 'before/system-state.json') -Raw | ConvertFrom-Json
if (@($before.tasks).Count -ne 0) { throw 'Preexisting task; not this rollback' }
$task=Get-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -ErrorAction SilentlyContinue
if ($task) {
    $expected=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft/WindowsApps/slu-service.exe'
    if ([System.IO.Path]::GetFullPath($task.Actions[0].Execute) -ne [System.IO.Path]::GetFullPath($expected)) { throw 'Unexpected task action' }
    Unregister-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -Confirm:$false
}
$remaining=@(Get-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -ErrorAction SilentlyContinue)
[ordered]@{finished_at=(Get-Date).ToString('o');administrative_token_verified=$true;
    task_path='\Seelen\';task_name='Seelen UI Service';remaining=$remaining.Count;other_tasks_changed=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'task-removal-result.json') -Encoding UTF8
if ($remaining.Count -ne 0) { throw 'Task still present' }
