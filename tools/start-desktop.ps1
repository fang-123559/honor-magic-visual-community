param([switch]$SystemValidation,[switch]$Logon)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $projectRoot
$nativeSelection=Join-Path $projectRoot 'runtime/system/selection.json'
if(Test-Path -LiteralPath $nativeSelection){
    if((Get-Item -LiteralPath $nativeSelection).Length -gt 8192){throw 'System selection exceeds bound.'}
    $selectedSystem=Get-Content -LiteralPath $nativeSelection -Raw -Encoding UTF8|ConvertFrom-Json
    if($selectedSystem.enabled -eq $true -and $selectedSystem.protocol -eq 'Magic.System.Native/1'){
        & (Join-Path $PSScriptRoot 'native-system-lifecycle.ps1') -Action Start -SystemValidation:$SystemValidation -Logon:$Logon
        return
    }
}

. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$logPath=Join-Path $projectRoot 'logs/desktop-launch.jsonl'
New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null
function Log-Launch($value) { $value | ConvertTo-Json -Compress -Depth 5 | Add-Content -LiteralPath $logPath -Encoding UTF8 }
function Assert-NativeTaskbarSafety([switch]$CheckWindow) {
    $safetyPath=Join-Path $projectRoot 'runtime/desktop/native-taskbar-safety.json'
    if(-not(Test-Path -LiteralPath $safetyPath)){return}
    $safety=Get-Content -LiteralPath $safetyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($safety.active -ne $true){return}
    try {
        if($safety.schemaVersion -ne 1 -or $safety.issue -ne 'P1-031'){throw 'Unknown native taskbar safety profile.'}
        $provider=Get-MagicSystemProvider $projectRoot
        $settings=Get-Content -LiteralPath (Join-Path $provider.DataDirectory 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if($settings.byWidget.'@seelen/weg'.enabled -ne $false){throw 'Replacement Dock was re-enabled while native taskbar mitigation is active.'}
        if($CheckWindow -and -not [MagicNativeTaskbarSafety]::PrimaryVisible()){throw 'Windows native taskbar is not visible.'}
    } catch {
        $reason=$_.Exception.Message
        # A failed safety check must not leave a replacement desktop with no UAC entry.
        # Re-enter the lifecycle mutex on this thread; a child would wait for the parent's lease.
        try { & (Join-Path $PSScriptRoot 'stop-desktop.ps1') }
        catch { throw ('Native taskbar safety check failed; Windows restoration also needs inspection: '+$reason+'; '+$_.Exception.Message) }
        if(-not [MagicNativeTaskbarSafety]::PrimaryVisible()){throw ('Native taskbar safety check failed; stop completed but Windows taskbar visibility is still unconfirmed: '+$reason)}
        throw ('Native taskbar safety check failed; returned to Windows: '+$reason)
    }
}
. (Join-Path $PSScriptRoot 'desktop-transition.ps1')
$lifecycle=Enter-MagicLifecycle -Root $projectRoot
if($null -eq $lifecycle){Log-Launch @{at=(Get-Date).ToString('o');status='startup_coalesced'};return}
try {
$transition=$null;$transitionReady=$false
try {
    $package=Get-MagicSystemProvider $projectRoot
    if($package.ValidationOnly -and -not $SystemValidation){throw "Temporary system acceptance requires an explicit -SystemValidation launch; automatic startup is disabled."}
    $servicePath=$package.ServiceExecutable;$cli=$package.CliExecutable
    $hostProcessName=$package.HostProcessName;$serviceProcessName=$package.ServiceProcessName
    $guiPath=Get-MagicDesktopHost $package $projectRoot
    Assert-MagicHostStatic -Folder (Split-Path $guiPath)
    if(-not $package.Independent){
        $owned=Get-Content -LiteralPath (Join-Path $projectRoot 'runtime/desktop/install-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $owned.installed -or $owned.packageFullName -ne $package.PackageFullName) { throw 'Installation differs from the recorded desktop baseline.' }
    }else{
        $owned=Read-MagicSystemJson (Join-Path $projectRoot 'runtime/system/install-state.json')
        if($owned.instanceId -ne $package.InstanceId -or $owned.nativeTaskbarState -notin @(0,1,2,3)){throw 'Independent install/rollback state differs.'}
    }
    Assert-NativeTaskbarSafety
    $pointer=Get-Content -LiteralPath (Join-Path $projectRoot 'app.current.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    # A healthy desktop reuses its existing process and animation. Never claim
    # success from a PID alone: authenticate the session and takeover receipt.
    $existingPath=Join-Path $projectRoot 'runtime/desktop/session.json'
    $existingReadyPath=Join-Path $projectRoot 'runtime/desktop/ready.json'
    if((Test-Path -LiteralPath $existingPath) -and (Test-Path -LiteralPath $existingReadyPath)){
        try{
            $previous=Get-Content -LiteralPath $existingPath -Raw|ConvertFrom-Json
            $priorReady=Get-Content -LiteralPath $existingReadyPath -Raw|ConvertFrom-Json
            $expectedApp=[IO.Path]::GetFullPath((Join-Path $projectRoot ([IO.Path]::ChangeExtension($pointer.entry,'.exe'))))
            if($previous.version -eq $pointer.version -and (Get-MagicLiveProcessPath $previous.pid) -eq $expectedApp -and $priorReady.sessionId -eq $previous.sessionId -and $priorReady.nativeIconTakeover -eq $true){
                $health=Invoke-RestMethod -Uri ($previous.endpoint+'/health') -Headers @{Authorization=('Bearer '+$previous.token)} -TimeoutSec 2
                if($health.sessionId -eq $previous.sessionId -and ($pointer.desktopRenderer -ne 'composition' -or $health.nativeDesktopReady -eq $true)){Log-Launch @{at=(Get-Date).ToString('o');status='startup_reused';pid=$previous.pid};return}
            }
        }catch{Log-Launch @{at=(Get-Date).ToString('o');status='reuse_unavailable';reason=$_.Exception.Message}}
    }
    $transition=Start-MagicTransition -Root $projectRoot -Mode entrance -Logon:$Logon
    $expected=[IO.Path]::GetFullPath((Join-Path $projectRoot ([IO.Path]::ChangeExtension($pointer.entry,'.exe'))))
    foreach ($old in @(Get-CimInstance Win32_Process -Filter "Name='MagicCommunity.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($projectRoot+'\versions\',[StringComparison]::OrdinalIgnoreCase) -and $_.ExecutablePath -ne $expected})) {
        $process=Get-Process -Id $old.ProcessId
        if (-not $process.CloseMainWindow()) { throw 'Close the older workbench or its dialog before entering the desktop.' }
        if (-not $process.WaitForExit(5000)) { throw 'Older workbench did not exit; no forced termination performed.' }
    }
    $originalGui=$package.HostExecutable
    $hostSelection=if($package.Independent){[pscustomobject]@{startupResources=$true}}else{Get-Content -LiteralPath (Join-Path $projectRoot 'runtime/desktop/host.current.json') -Raw -Encoding UTF8|ConvertFrom-Json}
    $bootFirst=$hostSelection.startupResources -eq $true
    Log-Launch @{at=(Get-Date).ToString('o');status='startup_begin';version=$pointer.version;host=$guiPath;communityFirst=$bootFirst}
    if(-not $package.Independent -and $guiPath -ne $originalGui -and -not @(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName'"|Where-Object {$_.ExecutablePath -eq $guiPath}).Count){
        # Bootstrap only the original service using its unchanged signed GUI.
        # Never register a new privileged task or start a community service.
        $servicePath=$package.ServiceExecutable
        if(-not $bootFirst -and -not @(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'"|Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count){
            Start-Process -FilePath $originalGui -ArgumentList '--silent' -WindowStyle Hidden|Out-Null
            $deadline=(Get-Date).AddSeconds(20)
            do{
                $serviceReady=@(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'"|Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count -eq 1
                if($serviceReady){break};Start-Sleep -Milliseconds 200
            }while((Get-Date) -lt $deadline)
            if(-not $serviceReady){throw 'Original service did not start; community host was not launched.'}
        }
        foreach($oldGui in @(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName'"|Where-Object {$_.ExecutablePath -eq $originalGui})){
            Stop-MagicGuiForHandoff -ProcessId $oldGui.ProcessId -ExpectedPath $originalGui
        }
    }
    if (-not @(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName'" | Where-Object {$_.ExecutablePath -eq $guiPath}).Count) {
        for($hostAttempt=0;$hostAttempt -lt 3;$hostAttempt++) {
            $launchedHost=Start-Process -FilePath $guiPath -ArgumentList '--silent' -WindowStyle Hidden -PassThru
            # At login the original service may still be launching its GUI.
            # Finish this bounded handoff before starting the adapter, which
            # intentionally snapshots the exact selected host for its lifetime.
            if(-not $launchedHost.WaitForExit(3000)){break}
            $exitCode=$launchedHost.ExitCode
            $competing=@(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName'"|Where-Object {$_.ExecutablePath -eq $originalGui})
            Log-Launch @{at=(Get-Date).ToString('o');status='host_early_exit';attempt=$hostAttempt;pid=$launchedHost.Id;exitCode=$exitCode;host=$guiPath;competingOriginalCount=$competing.Count}
            if($guiPath -eq $originalGui -or $hostAttempt -eq 2 -or $competing.Count -gt 1 -or ($competing.Count -eq 0 -and $exitCode -ne 0)){throw ('Selected desktop host exited during startup (code '+$exitCode+'); no older version was selected.')}
            foreach($other in $competing){Stop-MagicGuiForHandoff -ProcessId $other.ProcessId -ExpectedPath $originalGui}
            # Single-instance IPC can close before Explorer's original GUI is
            # visible to process enumeration. Retry only this bounded startup.
            if($competing.Count -eq 0){Start-Sleep -Milliseconds 250}
        }
    }
    if($bootFirst){
        # The selected GUI has already opened its App IPC before starting the
        # unchanged signed service on demand. Do not launch the stock GUI here.
        $servicePath=$package.ServiceExecutable
        $deadline=(Get-Date).AddSeconds(20);$serviceReady=$false
        do {
            $serviceReady=@(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'"|Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count -eq 1
            if($serviceReady){break};Start-Sleep -Milliseconds 200
        }while((Get-Date) -lt $deadline)
        if(-not $serviceReady){throw 'Selected community host did not connect the original signed service.'}
        if(-not $package.Independent){
        & (Join-Path $projectRoot 'tools/dotnet/dotnet.exe') (Join-Path $projectRoot 'tools/boot-service-control/bin/MagicBootServiceControl.dll') --disable-original-logon
        if($LASTEXITCODE -ne 0){throw 'Original service logon trigger could not be handed over.'}
        }
        Log-Launch @{at=(Get-Date).ToString('o');status='community_first_service_ready';host=$guiPath;originalGuiBootstrapped=$false}
    }
    if($pointer.desktopRenderer -eq 'composition'){
        $legacyMetadata=Join-Path $projectRoot 'runtime/desktop/widget/metadata.yml'
        if(Test-Path -LiteralPath $legacyMetadata){
            $release=Start-Process -FilePath ($package.CliExecutable) -ArgumentList ('resource unload widget "'+$legacyMetadata+'"') -WindowStyle Hidden -PassThru
            if(-not $release.WaitForExit(10000) -or $release.ExitCode -ne 0){throw 'Previous desktop surface did not unload; native desktop startup stopped.'}
        }
    }
    $sessionPath=Join-Path $projectRoot 'runtime/desktop/session.json';$session=$null
    $healthDeadline=(Get-Date).AddSeconds(24)
    for($brokerAttempt=0;$brokerAttempt -lt 2 -and $null -eq $session;$brokerAttempt++){
        $broker=& (Join-Path $PSScriptRoot 'launch-magic.ps1') -Mode Broker -NonInteractive -PassThru
        if($LASTEXITCODE -ne 0 -or $null -eq $broker){throw 'Local desktop adapter did not start.'}
        $attemptDeadline=(Get-Date).AddSeconds(9)
        do {
            if(Test-Path -LiteralPath $sessionPath){
                try{
                    $candidate=Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8|ConvertFrom-Json
                    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$candidate.pid)
                    if($process.ExecutablePath -eq $expected -and $candidate.version -eq $pointer.version){
                        $health=Invoke-RestMethod -Uri ($candidate.endpoint+'/health') -Headers @{Authorization=('Bearer '+$candidate.token)} -TimeoutSec 2
                        if($health.sessionId -eq $candidate.sessionId -and ($pointer.desktopRenderer -ne 'composition' -or $health.nativeDesktopReady -eq $true)){$session=$candidate;break}
                    }
                }catch{}
            }
            if($broker.HasExited){break}
            Start-Sleep -Milliseconds 500
        }while((Get-Date) -lt $attemptDeadline -and (Get-Date) -lt $healthDeadline)
        if($null -ne $session){break}
        Log-Launch @{at=(Get-Date).ToString('o');status='adapter_unready';attempt=$brokerAttempt;pid=$broker.Id;exited=$broker.HasExited;exitCode=$(if($broker.HasExited){$broker.ExitCode}else{$null})}
        # Retry only a confirmed exited child. A live unresponsive adapter must
        # remain a visible failure, never race with another complete instance.
        if(-not $broker.HasExited){throw 'Local adapter is alive but did not become healthy; duplicate launch refused.'}
    }
    if($null -eq $session){throw 'Local adapter startup failed after the bounded retry; inspect renderer and desktop-guard diagnostics.'}
    $cli=$package.CliExecutable
    Add-Type -AssemblyName System.Windows.Forms
    $expectedInstances=@([System.Windows.Forms.Screen]::AllScreens).Count
    if ($expectedInstances -lt 1) { throw 'No active display detected; retain the session for diagnosis.' }
    $loaded=$session.renderer -eq 'composition';$loadDeadline=(Get-Date).AddSeconds(45)
    while(-not $loaded -and (Get-Date) -lt $loadDeadline) {
        $load=Start-Process -FilePath $cli -ArgumentList ('resource load widget "'+$session.metadata+'"') -WindowStyle Hidden -PassThru
        if (-not $load.WaitForExit(5000)) { $load.Kill();throw 'Resource loading did not return; inspect the Seelen startup or permission dialog.' }
        if ($load.ExitCode -eq 0) {$loaded=$true;break}
        Start-Sleep -Seconds 2
    }
    if (-not $loaded) { throw 'Seelen IPC was not ready in the startup window; CLI exit code is not readiness proof.' }
    $readyPath=Join-Path $projectRoot 'runtime/desktop/ready.json';$ready=$null
    for ($attempt=0;$attempt -lt 25;$attempt++) {
        if (Test-Path -LiteralPath $readyPath) {
            $candidate=Get-Content -LiteralPath $readyPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($candidate.sessionId -eq $session.sessionId -and @($candidate.instances).Count -ge $expectedInstances -and ($pointer.unifiedDesktop -ne $true -or $candidate.nativeIconTakeover -eq $true)) { $ready=$candidate;break }
        }
        Start-Sleep -Seconds 1
    }
    if ($null -eq $ready) { throw "Desktop instances have not reported ready for $expectedInstances active display(s); inspect Seelen and adapter logs." }
    # Performance profile is separate from the WPF application version. It validates
    # the pinned Seelen host/assets and keeps existing resident windows on re-entry.
    if($package.Independent -and ($pointer.desktopRenderer -ne 'composition' -or @($pointer.nativePopups).Count -eq 0)){throw 'Independent system requires the native component selection.'}
    if($pointer.desktopRenderer -ne 'composition' -or @($pointer.nativePopups).Count -eq 0){& (Join-Path $PSScriptRoot 'apply-low-latency-popups.ps1')}else{Log-Launch @{at=(Get-Date).ToString('o');status='native_popups_selected';count=@($pointer.nativePopups).Count;legacyWarmup=$false}}
    if($package.Independent){
        Log-Launch @{at=(Get-Date).ToString('o');status='independent_boot_resources';manifest=$package.ManifestSha256}
    }elseif(-not $bootFirst){
        & (Join-Path $PSScriptRoot 'apply-power-panel.ps1')
        & (Join-Path $PSScriptRoot 'apply-community-ui.ps1')
    }else{
        # Original-popup fallback can replace the keyboard plugin after the
        # community boot registry loaded. Reapply only this existing item after
        # popup transitions; enabling the whole theme would reset external themes.
        & (Join-Path $PSScriptRoot 'apply-community-ui.ps1') -InputMethodOnly
    }
    Assert-NativeTaskbarSafety -CheckWindow
    $roots=@(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName' OR Name='$serviceProcessName'" | ForEach-Object {
        $path=(Get-MagicLiveProcessPath $_.ProcessId)
        if ($path -eq $guiPath -or $path -eq ($package.ServiceExecutable)) {
            [ordered]@{pid=$_.ProcessId;name=$_.Name;path=$path;creationFiletime=(Get-Process -Id $_.ProcessId).StartTime.ToFileTimeUtc()}
        }
    })
    $receipt=[ordered]@{at=(Get-Date).ToString('o');sessionId=$session.sessionId;version=$session.version;brokerPid=$session.pid;seelenRoots=$roots;systemRoots=$roots;systemProvider=$(if($package.Independent){'Magic.System/1'}else{'seelen-compatibility'});hostExecutable=$guiPath;hostSha256=(Get-MagicSHA256 $guiPath);ready=$ready;expectedActiveDisplays=$expectedInstances;manualAcceptance='pending';nativeIconTakeover=($ready.nativeIconTakeover -eq $true)}
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $projectRoot 'runtime/desktop/launch-receipt.json') -Encoding UTF8
    Log-Launch @{at=(Get-Date).ToString('o');version=$session.version;sessionId=$session.sessionId;instances=@($ready.instances).Count;status='runtime_ready_manual_pending'}
    $transitionReady=$true
    Write-Output 'Seelen desktop instances ready; real user visual/input acceptance pending.'
} catch { Log-Launch @{at=(Get-Date).ToString('o');status='failed';reason=$_.Exception.Message}; throw } finally { Complete-MagicTransition $transition $transitionReady }

} finally {Exit-MagicLifecycle $lifecycle}
