param([switch]$RestoreOriginal,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$pointer=Join-Path $root 'runtime/shell/toolbar-profile.current.json'
if(-not (Test-Path -LiteralPath $pointer)){return}
$profile=Get-Content -LiteralPath $pointer -Raw -Encoding UTF8|ConvertFrom-Json
if($profile.enabled -eq $false -and -not $RestoreOriginal){if(Test-Path -LiteralPath (Join-Path $root 'runtime/shell/toolbar-last-failure.json')){Write-Warning 'Transparent toolbar remains disabled after a prior failure. Inspect runtime/shell/toolbar-last-failure.json; no automatic retry performed.'};return}
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if(-not $package -or $package.PackageFullName -ne $profile.package -or $profile.owner -ne $root){throw 'Toolbar package or owner changed.'}
if($profile.version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid toolbar profile version.'}
$directory=Join-Path $root ('profiles/seelen-toolbar/'+$profile.version)
if([IO.Path]::GetFullPath($profile.directory) -ne $directory){throw 'Toolbar profile directory mismatch.'}
function Hash([string]$path){Get-MagicSHA256 $path}
foreach($file in $profile.files){$path=[IO.Path]::GetFullPath((Join-Path $directory $file.path));if(-not $path.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $path) -ne $file.sha256){throw 'Toolbar profile payload changed.'}}
if($ValidateOnly){Write-Output ('Toolbar surface validated: '+$profile.version);return}
$algorithm=[Security.Cryptography.SHA256]::Create()
try{$mutexKey=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
$mutex=New-Object Threading.Mutex($false,('Local\MagicToolbarSurface-'+$mutexKey));$locked=$false
try{
try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
if(-not $locked){throw 'Another toolbar transition is in progress; no second transition started.'}
$selectedGui=Get-MagicDesktopHost $package $root
$gui=@(Get-CimInstance Win32_Process -Filter "Name='seelen-ui.exe'"|Where-Object {$_.ExecutablePath -eq $selectedGui})
if($gui.Count -ne 1){throw 'Exactly one fixed Seelen host is required.'}
$hostPid=$gui[0].ProcessId;$birth=(Get-Process -Id $hostPid).StartTime.ToFileTimeUtc()
if(-not ('MagicToolbarWindow' -as [type])){Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class MagicToolbarWindow{[DllImport("user32.dll")]static extern bool IsWindow(IntPtr h);[DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);public static bool Owned(long h,uint expected){uint p;return IsWindow(new IntPtr(h))&&GetWindowThreadProcessId(new IntPtr(h),out p)!=0&&p==expected;}}'}
Add-Type -AssemblyName System.Windows.Forms
$expectedInstances=[Windows.Forms.Screen]::AllScreens.Count
$receiptPath=Join-Path $root 'runtime/shell/toolbar-surface-receipt.json'
if(-not $RestoreOriginal -and (Test-Path -LiteralPath $receiptPath)){
    $prior=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($prior.profile -eq $profile.version -and $prior.hostPid -eq $hostPid -and $prior.hostCreationFileTime -eq $birth -and @($prior.windows).Count -eq $expectedInstances -and @($prior.windows|Where-Object {-not [MagicToolbarWindow]::Owned([long]$_.windowId,[uint32]$hostPid)}).Count -eq 0){Write-Output 'Toolbar surface already active; existing window retained.';return}
}
$cli=Join-Path $package.InstallLocation 'slu.exe'
function Invoke-ToolbarCli([string]$arguments){
    $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$cli;$info.Arguments=$arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$info
    try{if(-not $process.Start()){throw 'Toolbar CLI did not start'};$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Toolbar CLI timed out'};$process.WaitForExit();if($process.ExitCode -ne 0){throw ('Toolbar CLI failed: '+$stderr.Result)}}finally{$process.Dispose()}
}
$log=Join-Path $env:LOCALAPPDATA 'com.seelen.seelen-ui/logs/Seelen UI.log'
function Tail([long]$offset){$stream=New-Object IO.FileStream($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite);$reader=$null;try{[void]$stream.Seek([Math]::Min($offset,$stream.Length),[IO.SeekOrigin]::Begin);$reader=New-Object IO.StreamReader($stream);return $reader.ReadToEnd()}finally{if($reader){$reader.Dispose()}else{$stream.Dispose()}}}
$nativeSettings=Join-Path $env:APPDATA 'com.seelen.seelen-ui/settings.json'
$disableState=[pscustomobject]@{attempted=$false;observed=$false}
function Disable-Toolbar {
    $offset=(Get-Item -LiteralPath $log).Length
    $disableState.attempted=$true
    Invoke-ToolbarCli ('resource load widget "'+(Join-Path $directory 'toolbar-transition/metadata.yml')+'"')
    Invoke-ToolbarCli 'widget trigger "@magic-community/toolbar-transition"'
    $deadline=(Get-Date).AddSeconds(240);$done=$null
    while((Get-Date) -lt $deadline){
        $tail=Tail $offset
        foreach($line in ($tail -split "`n")){if($line.Contains('MAGIC_TOOLBAR_DISABLED')){try{$done=$line.Substring($line.IndexOf('{'))|ConvertFrom-Json}catch{}}}
        if($done){$disableState.observed=$true}
        if($tail.Contains('MAGIC_TOOLBAR_HELPER_RELEASE_FAILED')){throw 'Toolbar was disabled but helper release failed; restore required.'}
        if($tail.Contains('MAGIC_TOOLBAR_TRANSITION_FAILED')){throw 'Toolbar transition failed or native permission was declined; applied state may be uncertain.'}
        if($done){break};Start-Sleep -Milliseconds 250
    }
    if(-not $done){throw 'Toolbar disable not observed; no replacement loaded.'}
    $stale=@($done.windows)+@($done.controllerWindowId);$deadline=(Get-Date).AddSeconds(15)
    do{$settings=Get-Content -LiteralPath $nativeSettings -Raw -Encoding UTF8|ConvertFrom-Json;$alive=@($stale|Where-Object {[MagicToolbarWindow]::Owned([long]$_,[uint32]$hostPid)});if($alive.Count -eq 0 -and $settings.byWidget.'@seelen/fancy-toolbar'.enabled -eq $false -and $settings.byWidget.'@magic-community/toolbar-transition'.enabled -eq $false){return};Start-Sleep -Milliseconds 100}while((Get-Date) -lt $deadline)
    throw 'Toolbar/helper native windows or enabled state still active; replacement refused.'
}
$layoutHash=Hash (Join-Path $root 'data/layout.json')
$toolbarState=Join-Path $env:APPDATA 'com.seelen.seelen-ui/data/seelen-fancy-toolbar/state.yml';$toolbarStateHash=Hash $toolbarState
$original=Join-Path $package.InstallLocation 'static/widgets/toolbar/metadata.yml'
$disabled=$false
try{
    Disable-Toolbar;$disabled=$true
    if($RestoreOriginal){Invoke-ToolbarCli ('resource load widget "'+$original+'"');$profile.enabled=$false;$profile|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $pointer -Encoding UTF8;Write-Output 'Original toolbar load requested; custom surface disabled. Human input acceptance separate.';return}
    $offset=(Get-Item -LiteralPath $log).Length
    Invoke-ToolbarCli ('resource load widget "'+(Join-Path $directory 'fancy-toolbar/metadata.yml')+'"')
    $deadline=(Get-Date).AddSeconds(60);$windows=@()
    while((Get-Date) -lt $deadline){
        $windows=@();foreach($line in ((Tail $offset) -split "`n")){if($line.Contains('MAGIC_TOOLBAR_SURFACE_READY')){try{$data=$line.Substring($line.IndexOf('{'))|ConvertFrom-Json;if($data.id -eq '@seelen/fancy-toolbar' -and $data.sdkSource -eq '2.8.9' -and [MagicToolbarWindow]::Owned([long]$data.windowId,[uint32]$hostPid)){$windows+=$data}}catch{}}}
        $windows=@($windows|Group-Object monitorId|ForEach-Object {$_.Group|Select-Object -Last 1})
        if($windows.Count -eq $expectedInstances){break};Start-Sleep -Milliseconds 250
    }
    if($windows.Count -ne $expectedInstances -or [Windows.Forms.Screen]::AllScreens.Count -ne $expectedInstances){throw 'Toolbar readiness or display count mismatch.'}
    if((Hash (Join-Path $root 'data/layout.json')) -ne $layoutHash -or (Hash $toolbarState) -ne $toolbarStateHash){throw 'User layout or toolbar items changed during transition; inspect before continuing.'}
    [ordered]@{at=(Get-Date).ToString('o');profile=$profile.version;hostPid=$hostPid;hostCreationFileTime=$birth;windows=$windows;layoutUnchanged=$true;toolbarItemsUnchanged=$true;helperReleased=$true;manualAcceptance='pending';scope='Actual frontend readiness and HWND identity; transparency, fullscreen and click behavior need human acceptance'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $receiptPath -Encoding UTF8
    Write-Output ('Toolbar surface frontend ready: '+$profile.version)
}catch{
    $failure=$_.Exception.Message
    $recoveryErrors=@()
    if($disabled -or $disableState.attempted){
        # A save may have applied before its reply failed. Recover even without a returned success.
        # Do not repeat the failing disable transition before attempting the original toolbar.
        try{Invoke-ToolbarCli ('resource unload widget "'+(Join-Path $directory 'toolbar-transition/metadata.yml')+'"')}catch{$recoveryErrors+=('Helper release: '+$_.Exception.Message)}
        try{Invoke-ToolbarCli ('resource load widget "'+$original+'"')}catch{$recoveryErrors+=('Original toolbar load: '+$_.Exception.Message)}
        foreach($recoveryError in $recoveryErrors){Write-Warning $recoveryError}
    }
    $profile.enabled=$false;$profile|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $pointer -Encoding UTF8
    [ordered]@{at=(Get-Date).ToString('o');failure=$failure;disableAttempted=$disableState.attempted;disableObserved=$disableState.observed;recoveryErrors=$recoveryErrors;recoveryNeedsReview=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root 'runtime/shell/toolbar-last-failure.json') -Encoding UTF8
    throw ('Toolbar surface disabled after failure: '+$failure)
}
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
