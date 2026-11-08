param([Parameter(Mandatory=$true)][int]$GuiPid,[Parameter(Mandatory=$true)][int]$ServicePid)
$ErrorActionPreference='Stop'
$projectRoot=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $projectRoot
$evidence=Join-Path $projectRoot 'evidence/p0/2026-10-14/seelen-runtime'
$closedReceipt=Join-Path $evidence 'rollback-verification.json'
if ((Test-Path -LiteralPath $closedReceipt) -and (Get-Content -LiteralPath $closedReceipt -Raw | ConvertFrom-Json).all_pass) {
    throw 'This recorded experiment is already closed. Do not replay its destructive rollback against a later installation; create a fresh baseline and run directory.'
}
$before=Get-Content -LiteralPath (Join-Path $evidence 'before/system-state.json') -Raw | ConvertFrom-Json
$installed=Get-Content -LiteralPath (Join-Path $evidence 'installed-package.json') -Raw | ConvertFrom-Json
$last=Get-Content -LiteralPath (Join-Path $evidence 'restarted-baseline.json') -Raw | ConvertFrom-Json
if (@($before.installed_packages).Count -ne 0 -or @($before.tasks).Count -ne 0) { throw 'Preexisting Seelen state requires a different rollback' }
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if ($package.PackageFullName -ne $installed.PackageFullName -or $package.Version -ne '2.8.9.0') { throw 'Installed package differs from this experiment' }
$steps=@()
if (Get-Process -Id $ServicePid -ErrorAction SilentlyContinue) {
    $stop=Start-Process -FilePath (Join-Path $package.InstallLocation 'slu-service.exe') -ArgumentList 'stop' -WindowStyle Hidden -PassThru
    if (-not $stop.WaitForExit(15000) -or $stop.ExitCode -ne 0) { throw 'Own service stop command failed' }
}
for ($attempt=0;$attempt -lt 15;$attempt++) {
    if (-not (Get-Process -Id $ServicePid -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}
if (Get-Process -Id $ServicePid -ErrorAction SilentlyContinue) { throw 'Own service did not exit' }
$gui=Get-CimInstance Win32_Process -Filter ("ProcessId=" + $GuiPid)
if ($gui) {
    if ($gui.Name -ne 'seelen-ui.exe' -or $gui.ExecutablePath -ne (Join-Path $package.InstallLocation 'seelen-ui.exe')) { throw 'GUI identity mismatch' }
    Stop-Process -Id $GuiPid -Force
}
Start-Sleep -Seconds 2
foreach ($member in $last.samples[-1].processes) {
    $process=Get-Process -Id $member.pid -ErrorAction SilentlyContinue
    if ($process -and $member.status -eq 'observed' -and $member.name -eq 'msedgewebview2.exe') {
        if ($process.ProcessName -eq 'msedgewebview2' -and $process.StartTime.ToFileTimeUtc() -eq $member.creation_filetime) {
            Stop-Process -Id $member.pid -Force
            $steps += "Stopped surviving own WebView2 PID $($member.pid), creation time matched"
        }
    }
}
$steps += 'Experimental service and GUI stopped'
$task=Get-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -ErrorAction SilentlyContinue
if ($task) {
    $expectedAction=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft/WindowsApps/slu-service.exe'
    if ([System.IO.Path]::GetFullPath($task.Actions[0].Execute) -ne [System.IO.Path]::GetFullPath($expectedAction)) { throw 'Task action is not this experiment' }
    Unregister-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -Confirm:$false
}
$steps += 'New login task removed; no unrelated tasks changed'
Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop
$steps += 'Exact experimental MSIX registration removed'
$archiveRoot=[System.IO.Path]::GetFullPath((Join-Path $evidence 'rollback-generated-data'))
if (-not $archiveRoot.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive is outside project' }
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
$allowed=@{
    roaming=[System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'com.seelen.seelen-ui'));
    local=[System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'com.seelen.seelen-ui'));
    temp=[System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) 'com.seelen.seelen-ui'))
}
foreach ($label in $allowed.Keys) {
    $target=$allowed[$label]
    $baseline=@($before.directories | Where-Object { [System.IO.Path]::GetFullPath($_.path) -eq $target })
    if ($baseline.Count -ne 1 -or $baseline[0].existed) { throw "Directory was not proven new: $target" }
    if (-not (Test-Path -LiteralPath $target)) { continue }
    $resolved=(Resolve-Path -LiteralPath $target).Path
    if ([System.IO.Path]::GetFullPath($resolved) -ne $target) { throw 'Resolved source differs from exact allowed target' }
    $item=Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Source root is a reparse point' }
    if (Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Generated directory contains a reparse point; refusing recursive move' }
    $destination=[System.IO.Path]::GetFullPath((Join-Path $archiveRoot $label))
    if (-not $destination.StartsWith($archiveRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $destination)) { throw 'Archive target unsafe or already exists' }
    if ([System.IO.Path]::GetPathRoot($resolved) -eq [System.IO.Path]::GetPathRoot($destination)) {
        Move-Item -LiteralPath $resolved -Destination $destination
    } else {
        Copy-Item -LiteralPath $resolved -Destination $destination -Recurse
        foreach ($sourceFile in (Get-ChildItem -LiteralPath $resolved -File -Recurse -Force)) {
            $relative=$sourceFile.FullName.Substring($resolved.Length).TrimStart('\')
            $archivedFile=Join-Path $destination $relative
            if (-not (Test-Path -LiteralPath $archivedFile) -or
                (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $archivedFile -Algorithm SHA256).Hash) { throw 'Generated-data archive verification failed; source retained' }
        }
        # Exact known target, proven absent before test, reparse points rejected above.
        if ([System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $resolved).Path) -ne $target) { throw 'Source changed before removal' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    $steps += "Archived only newly created $label data to project"
}
$remainingPackage=@(Get-AppxPackage -Name 'Seelen.SeelenUI')
$remainingTask=@(Get-ScheduledTask -TaskPath '\Seelen\' -TaskName 'Seelen UI Service' -ErrorAction SilentlyContinue)
$remainingRoots=@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(seelen-ui|slu-service)\.exe$' } | Select-Object Name,ProcessId)
$receipt=[ordered]@{finished_at=(Get-Date).ToString('o');steps=$steps;
    installed_packages_remaining=$remainingPackage.Count;login_tasks_remaining=$remainingTask.Count;
    roots_remaining=$remainingRoots;user_path_matches_before=([Environment]::GetEnvironmentVariable('Path','User') -eq $before.user_path);
    restored_known_directories=@($allowed.Values | ForEach-Object { [ordered]@{path=$_;exists=(Test-Path -LiteralPath $_)} });
    archive=$archiveRoot;note='Generated test data retained in project; empty scheduler folder not removed because its preexistence was not recorded'}
$receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'rollback.json') -Encoding UTF8
$receipt | ConvertTo-Json -Depth 7
if ($remainingPackage.Count -ne 0 -or $remainingTask.Count -ne 0 -or $remainingRoots.Count -ne 0 -or
    @($receipt.restored_known_directories | Where-Object { $_.exists }).Count -ne 0 -or
    -not $receipt.user_path_matches_before) { throw 'Rollback verification failed: inspect package/task/process/data-directory/PATH postconditions; receipt is not proof of completion' }
