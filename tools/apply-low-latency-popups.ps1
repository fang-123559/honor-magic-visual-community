param([switch]$RestoreOriginal,[switch]$Force,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$record=Join-Path $projectRoot 'runtime/latency/popup-profile.current.json'
if (-not (Test-Path -LiteralPath $record)) { return }
$state=Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json
$failurePath=Join-Path $projectRoot 'runtime/latency/last-failure.json'
if ($state.enabled -eq $false -and -not $RestoreOriginal) {
    if(Test-Path -LiteralPath $failurePath){Write-Warning ('Popup profile remains disabled after a recorded failure; inspect '+$failurePath+' before recovery. No automatic retry was started.')}
    return
}
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if (-not $package) { throw 'Fixed Seelen package is not installed for this Windows user.' }
if ($package.PackageFullName -ne $state.package -or $state.owner -ne $projectRoot) { throw 'Low-latency popup package/ownership mismatch.' }
if ($state.version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Invalid popup profile version.' }
function Hash([string]$path) {
    $stream=[IO.File]::OpenRead($path);$algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose();$algorithm.Dispose() }
}
$cli=Join-Path $package.InstallLocation 'slu.exe'
function Invoke-Slu([string]$arguments) {
    $attemptPath=Join-Path $projectRoot ('runtime/latency/cli-'+[guid]::NewGuid().ToString('N'))
    # Start-Process -PassThru can expose a null ExitCode for a fast child in
    # Windows PowerShell 5.1. Retain the native handle with Process.Start instead.
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$cli;$info.Arguments=$arguments
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$info
    try {
        if(-not $process.Start()){throw 'Resource CLI did not start.'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { $process.Kill();throw 'Low-latency CLI timed out; own CLI child stopped.' }
        $process.WaitForExit()
        [IO.File]::WriteAllText(($attemptPath+'.out.txt'),$stdout.Result)
        [IO.File]::WriteAllText(($attemptPath+'.err.txt'),$stderr.Result)
        if ($process.ExitCode -ne 0) { throw ('Low-latency CLI failed (exit '+$process.ExitCode+'); inspect '+$attemptPath+'.err.txt') }
    } finally { $process.Dispose() }
}
function Invoke-Resource([string]$verb,[string]$path) { Invoke-Slu ('resource '+$verb+' widget "'+$path+'"') }
$log=Join-Path $env:LOCALAPPDATA 'com.seelen.seelen-ui/logs/Seelen UI.log'
$logOffset=if(Test-Path -LiteralPath $log){(Get-Item -LiteralPath $log).Length}else{0}
$validated=@()
foreach($row in $state.widgets) {
    if ($row.sourceFolder -notin @('quick-settings','calendar-popup','network-popup','bluetooth-popup','media-popup','notifications','keyboard-selector','system-tray','user')) { throw 'Unexpected source widget.' }
    $folder=[IO.Path]::GetFullPath($row.directory)
    $prefix=$projectRoot+'\profiles\seelen-popups\'+$state.version+'\'
    if (-not $folder.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $folder).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Popup path outside the pinned profile.' }
    if ([IO.Path]::GetFullPath($row.metadata) -ne (Join-Path $folder 'metadata.yml')) { throw 'Unexpected metadata path.' }
    if (-not $RestoreOriginal) { foreach($file in $row.files) {
        $path=[IO.Path]::GetFullPath((Join-Path $folder $file.path))
        if ((Split-Path $path) -ne $folder -or (Hash $path) -ne $file.sha256) { throw 'Low-latency popup payload changed.' }
    } }
    $original=Join-Path $package.InstallLocation ('static/widgets/'+$row.sourceFolder+'/metadata.yml')
    if ((Hash $original) -ne $row.sourceMetadataSha256) { throw 'Original popup baseline changed.' }
    $validated+=@{row=$row;original=$original}
}
if ($state.deploymentMode -ne 'settings-disable-reload') { throw 'Retired popup deployment mode; use a reviewed profile.' }
$controller=Get-Content -LiteralPath (Join-Path $projectRoot 'runtime/latency/controller-install.json') -Raw -Encoding UTF8|ConvertFrom-Json
$controllerDirectory=Join-Path $env:APPDATA 'com.seelen.seelen-ui/widgets/magic-community-popup-lifecycle'
if($controller.owner -ne $projectRoot -or $controller.package -ne $package.PackageFullName -or $controller.widgetId -ne '@magic-community/popup-lifecycle' -or [IO.Path]::GetFullPath($controller.directory) -ne $controllerDirectory -or [IO.Path]::GetFullPath($controller.metadata) -ne (Join-Path $controllerDirectory 'metadata.yml')){throw 'Popup controller ownership/path mismatch.'}
if((Get-Item -LiteralPath $controllerDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Popup controller directory is a reparse point.'}
foreach($file in $controller.files){
    $path=[IO.Path]::GetFullPath((Join-Path $controllerDirectory $file.path))
    if((Split-Path $path) -ne $controllerDirectory -or (Hash $path) -ne $file.sha256){throw 'Popup controller payload changed.'}
}
if ($ValidateOnly) { Write-Output ('Popup profile validated: '+$state.version);return }
$algorithm=[Security.Cryptography.SHA256]::Create()
try{$mutexKey=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
$mutex=New-Object Threading.Mutex($false,('Local\MagicPopupTransition-'+$mutexKey));$locked=$false
try {
    try{$locked=$mutex.WaitOne(180000)}catch [Threading.AbandonedMutexException]{$locked=$true}
    if(-not $locked){throw 'Another popup transition is still in progress; no second transition started.'}
$selectedGui=Get-MagicDesktopHost $package $projectRoot
$gui=@(Get-CimInstance Win32_Process -Filter "Name='seelen-ui.exe'" | Where-Object {$_.ExecutablePath -eq $selectedGui})
if ($gui.Count -ne 1) { throw 'Exactly one fixed Seelen host is required.' }
$hostPid=$gui[0].ProcessId
$birth=(Get-Process -Id $hostPid).StartTime.ToFileTimeUtc()
if (-not ('MagicPopupIdentity' -as [type])) { Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MagicPopupIdentity {
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr hwnd);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 public static bool Owned(long hwnd,uint expected){uint pid;return IsWindow(new IntPtr(hwnd))&&GetWindowThreadProcessId(new IntPtr(hwnd),out pid)!=0&&pid==expected;}
}
'@ }
$receiptPath=Join-Path $projectRoot 'runtime/latency/apply-receipt.json'
if (-not $Force -and -not $RestoreOriginal -and (Test-Path -LiteralPath $receiptPath)) {
    $prior=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
    if ($prior.profile -eq $state.version -and $prior.hostPid -eq $hostPid -and $prior.hostCreationFileTime -eq $birth -and @($prior.windows).Count -eq @($state.widgets).Count -and @($prior.windows|Where-Object {-not [MagicPopupIdentity]::Owned([long]$_.windowId,[uint32]$hostPid)}).Count -eq 0) {
        Write-Output 'Low-latency popup profile already active; existing windows retained.';return
    }
}
function Read-ResidentWindows {
    $reader=$null;$stream=New-Object IO.FileStream($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try { [void]$stream.Seek([Math]::Min($logOffset,$stream.Length),[IO.SeekOrigin]::Begin);$reader=New-Object IO.StreamReader($stream);$tail=$reader.ReadToEnd() } finally { if($reader){$reader.Dispose()}else{$stream.Dispose()} }
    if ($tail -match 'MAGIC_POPUP_FAILED') { throw 'Popup candidate failed to initialize; restore originals.' }
    $items=@()
    foreach($line in ($tail -split "`n")) {
        if ($line.Contains('MAGIC_POPUP_RESIDENT_READY')) {
            try {
                $data=$line.Substring($line.IndexOf('{'))|ConvertFrom-Json
                if($data.windowId -and $data.id -in @($state.widgets|ForEach-Object widgetId) -and $data.sdkSource -eq '2.8.9' -and $data.closeOnHide -eq $false -and $data.showOnReady -eq $false) {
                    # Windows PowerShell 5.1 Group-Object ignores Hashtable keys.
                    $items+=[pscustomobject]@{id=$data.id;windowId=[long]$data.windowId}
                }
            } catch { }
        }
    }
    # An incomplete final JSON log line is retried on the next read.
    $items|Group-Object id|ForEach-Object {$_.Group|Select-Object -Last 1}
}
function Disable-PopupDeployments {
    $offset=(Get-Item -LiteralPath $log).Length
    Invoke-Resource 'load' $controller.metadata
    Invoke-Slu 'widget trigger "@magic-community/popup-lifecycle"'
    $deadline=(Get-Date).AddSeconds(240);$done=$null
    while((Get-Date) -lt $deadline){
        $stream=New-Object IO.FileStream($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $reader=$null
        try{[void]$stream.Seek([Math]::Min($offset,$stream.Length),[IO.SeekOrigin]::Begin);$reader=New-Object IO.StreamReader($stream);$tail=$reader.ReadToEnd()}finally{if($reader){$reader.Dispose()}else{$stream.Dispose()}}
        if($tail.Contains('MAGIC_POPUP_LIFECYCLE_SAVED')){$script:transitionStarted=$true}
        if($tail.Contains('MAGIC_POPUP_LIFECYCLE_FAILED')){throw 'Popup controller failed or permission was declined; no authorization inferred.'}
        foreach($line in ($tail -split "`n")){
            if($line.Contains('MAGIC_POPUP_LIFECYCLE_DISABLED')){try{$done=$line.Substring($line.IndexOf('{'))|ConvertFrom-Json}catch{}}
        }
        if($done){break}
        Start-Sleep -Milliseconds 250
    }
    if(-not $done -or @($done.ids|Select-Object -Unique).Count -ne @($state.widgets).Count -or @($done.ids|Where-Object {$_ -notin @($state.widgets|ForEach-Object widgetId)}).Count -ne 0 -or -not [MagicPopupIdentity]::Owned([long]$done.controllerWindowId,[uint32]$hostPid)){throw 'Popup deployment disable was not verified.'}
    $destroyDeadline=(Get-Date).AddSeconds(10)
    while(@($done.windows|Where-Object {[MagicPopupIdentity]::Owned([long]$_.windowId,[uint32]$hostPid)}).Count -gt 0 -and (Get-Date) -lt $destroyDeadline){Start-Sleep -Milliseconds 100}
    if(@($done.windows|Where-Object {[MagicPopupIdentity]::Owned([long]$_.windowId,[uint32]$hostPid)}).Count -gt 0){throw 'Prior popup native windows still exist; replacement refused.'}
    $done|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $projectRoot 'runtime/latency/last-disable-receipt.json') -Encoding UTF8
}
$transitionStarted=$false
try {
Disable-PopupDeployments
$transitionStarted=$true
foreach($item in $validated) {
    $row=$item.row;$original=$item.original
    # Keep every resource ID installed. Removing an ID also deletes its native
    # user permission decisions. Disabled deployments have already been evicted.
    if ($RestoreOriginal) { Invoke-Resource 'load' $original } else {
        Invoke-Resource 'load' $row.metadata
        # Limit cold-start work to one popup at a time. Native resource load only
        # enqueues webview creation; its exit code is not frontend readiness.
        $widgetDeadline=(Get-Date).AddSeconds(45);$widgetReady=$false
        while((Get-Date) -lt $widgetDeadline) {
            $observed=@(Read-ResidentWindows|Where-Object {$_.id -eq $row.widgetId -and [MagicPopupIdentity]::Owned([long]$_.windowId,[uint32]$hostPid)})
            if($observed.Count -eq 1){$widgetReady=$true;break}
            Start-Sleep -Milliseconds 250
        }
        if(-not $widgetReady){throw ('Popup initialization timed out: '+$row.widgetId)}
    }
}
if ($RestoreOriginal) {
    $state | Add-Member -MemberType NoteProperty -Name enabled -Value $false -Force
    [IO.File]::WriteAllText($record,($state|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    if(Test-Path -LiteralPath $failurePath){Remove-Item -LiteralPath $failurePath -Force}
    Write-Output 'Original popups restored; low-latency profile disabled.'
    return
}
$ready=@();$windows=@();$deadline=(Get-Date).AddSeconds(45)
while((Get-Date) -lt $deadline) {
    $windows=@(Read-ResidentWindows)
    $ready=@($windows|Where-Object {[MagicPopupIdentity]::Owned([long]$_.windowId,[uint32]$hostPid)})
    if ($ready.Count -eq @($state.widgets).Count) { break }
    Start-Sleep -Milliseconds 250
}
if ($ready.Count -ne @($state.widgets).Count) { throw ('Popup readiness/native identity not observed for every widget: '+$ready.Count+'/'+@($state.widgets).Count+'; CLI success is not runtime acceptance.') }
$receipt=[ordered]@{at=(Get-Date).ToString('o');profile=$state.version;deploymentMode=$state.deploymentMode;hostPid=$hostPid;hostCreationFileTime=$birth;windows=$windows;readyIds=@($ready|ForEach-Object id);scope='Actual frontend readiness and native window identity only; no physical click latency or human acceptance inferred'}
$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receiptPath -Encoding UTF8
if(Test-Path -LiteralPath $failurePath){Remove-Item -LiteralPath $failurePath -Force}
Write-Output ('Low-latency popup frontend ready: '+$state.version)
} catch {
    $failure=$_.Exception.Message
    $canRestore=$false;$restoreFailures=@()
    if($transitionStarted){
        try{Disable-PopupDeployments;$canRestore=$true}catch{Write-Warning 'Could not stop candidate deployments; original trust metadata will not be overlaid onto running candidates.'}
        if($canRestore){foreach($item in $validated) { try { Invoke-Resource 'load' $item.original } catch { $restoreFailures+=$item.row.widgetId;Write-Warning ('Original popup restore failed: '+$item.row.widgetId) } }}
    }
    $failureRecord=[ordered]@{at=(Get-Date).ToString('o');profile=$state.version;owner=$projectRoot;hostPid=$hostPid;failure=$failure;transitionStarted=$transitionStarted;candidateStopVerified=$canRestore;originalLoadFailures=$restoreFailures;recoveryNeedsReview=[bool]$transitionStarted;scope='Persistent diagnostic only. Original CLI load is not frontend or input acceptance; no automatic retry authorized by this marker.'}
    try{[IO.File]::WriteAllText($failurePath,($failureRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))}catch{Write-Warning ('Could not persist popup failure diagnostic: '+$_.Exception.Message)}
    $state|Add-Member -MemberType NoteProperty -Name enabled -Value $false -Force
    [IO.File]::WriteAllText($record,($state|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    throw ('Low-latency profile disabled after failure: '+$failure)
}
} finally {if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
