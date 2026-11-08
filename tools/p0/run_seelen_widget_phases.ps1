param([Parameter(Mandatory=$true)][int]$GuiPid,[Parameter(Mandatory=$true)][int]$ServicePid)
$ErrorActionPreference='Stop'
$projectRoot=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $projectRoot
$evidence=Join-Path $projectRoot 'evidence/p0/2026-10-14/seelen-runtime'
if (-not (Test-Path -LiteralPath (Join-Path $evidence 'without-widget.json'))) { throw 'Stable baseline must complete first' }
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if ($package.Version -ne '2.8.9.0') { throw 'Pinned package missing' }
$cli=Join-Path $package.InstallLocation 'slu.exe'
$metadata=(Resolve-Path -LiteralPath 'experiments/p0/seelen-widget/metadata.yml').Path
$index=Join-Path $projectRoot 'experiments/p0/seelen-widget/index.js'
$log=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'com.seelen.seelen-ui/logs/Seelen UI.log'
$receipts=@()
foreach ($count in @(0,100)) {
    if ((Get-Process -Id $GuiPid).ProcessName -ne 'seelen-ui' -or (Get-Process -Id $ServicePid).ProcessName -ne 'slu-service') { throw 'Seelen roots changed' }
    $phase=if ($count -eq 0) {'empty-widget'} else {'100-items-widget'}
    $bundle=if ($count -eq 0) {'empty.js'} else {'100-items.js'}
    Copy-Item -LiteralPath (Join-Path $projectRoot ('experiments/p0/seelen-widget/bundles/' + $bundle)) -Destination $index
    $loadedAt=(Get-Date).AddSeconds(-1)
    & $cli resource load widget $metadata
    if ($LASTEXITCODE -ne 0) { throw 'Widget load CLI failed' }
    $ready=@()
    for ($attempt=0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $ready=@()
        foreach ($line in (Get-Content -LiteralPath $log -Tail 200)) {
            if ($line -match '^\[(\d{4}-\d{2}-\d{2})\]\[(\d{2}:\d{2}:\d{2})\].*P0_WIDGET_READY (\{.*\})$') {
                $stamp=[datetime]::ParseExact(($Matches[1] + ' ' + $Matches[2]),'yyyy-MM-dd HH:mm:ss',$null)
                if ($stamp -ge $loadedAt) {
                    $payload=$Matches[3] | ConvertFrom-Json
                    if ($payload.syntheticItems -eq $count) { $ready += $payload }
                }
            }
        }
        if (@($ready.monitorId | Select-Object -Unique).Count -eq 2) { break }
    }
    if (@($ready.monitorId | Select-Object -Unique).Count -ne 2) { throw 'Two current real monitor-ready events not observed' }
    $ready | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence ($phase + '-ready.json')) -Encoding UTF8
    python tools/p0/snapshot_widget_windows.py --pid $GuiPid --output (Join-Path $evidence ($phase + '-windows.json'))
    if ($LASTEXITCODE -ne 0) { throw 'Window snapshot failed' }
    Write-Output "Sampling $phase, roots $GuiPid/$ServicePid; real two-monitor ready recorded"
    python tools/p0/measure_process_group.py --roots $GuiPid $ServicePid --phase $phase --output (Join-Path $evidence ($phase + '.json'))
    if ($LASTEXITCODE -ne 0) { throw 'Group sample failed' }
    $receipts += [ordered]@{phase=$phase;started_at=$loadedAt.ToString('o');ended_at=(Get-Date).ToString('o');
        gui_pid=$GuiPid;service_pid=$ServicePid;synthetic_items_per_monitor=$count;
        bundle_sha256=(Get-FileHash -LiteralPath $index -Algorithm SHA256).Hash;sampler_exit=0}
    $receipts | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'phase-receipts.json') -Encoding UTF8
    & $cli resource unload widget $metadata
    if ($LASTEXITCODE -ne 0) { throw 'Widget unload CLI failed' }
    Start-Sleep -Seconds 2
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'experiments/p0/seelen-widget/bundles/empty.js') -Destination $index
python tools/p0/snapshot_widget_windows.py --pid $GuiPid --output (Join-Path $evidence 'after-unload-windows.json')
Write-Output 'Sampling post-unload resource retention'
python tools/p0/measure_process_group.py --roots $GuiPid $ServicePid --phase after-unload --output (Join-Path $evidence 'after-unload.json')
if ($LASTEXITCODE -ne 0) { throw 'Post-unload sample failed' }
Copy-Item -LiteralPath $log -Destination (Join-Path $evidence 'runtime-final.log')
Write-Output 'Seelen widget phases finished; system/package rollback remains required'
