param([ValidateSet('Start','Stop','Check')][string]$Action='Check',[switch]$SystemValidation,[switch]$Logon)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1');Assert-MagicReleaseWorkspace -Root $root
. (Join-Path $PSScriptRoot 'system-runtime/native-json.ps1')
function Read-NativeJson([string]$path,[int]$max=65536){Read-MagicNativeJson -Path $path -MaximumBytes $max}
$p=Read-NativeJson (Join-Path $root runtime/system/selection.json) 8192
if($p.enabled -ne $true -or $p.protocol -ne 'Magic.System.Native/1' -or $p.schemaVersion -ne 2 -or $p.mode -ne 'validation' -or [IO.Path]::GetFullPath($p.owner) -ne $root){throw 'Native validation selection differs.'}
$manifest=[IO.Path]::GetFullPath((Join-Path $root $p.manifest));if(-not $manifest.StartsWith((Join-Path $root versions)+'\',[StringComparison]::OrdinalIgnoreCase)-or [IO.Path]::GetFileName($manifest) -ne 'native-system.json'){throw 'Native manifest escaped version directory.'}
if((Get-FileHash -LiteralPath $manifest).Hash -ne $p.manifestSha256){throw 'Native manifest hash differs.'}
$m=Read-NativeJson $manifest
$package=Get-AppxPackage -Name MagicCommunity.Native
if(-not $package -or $package.PackageFamilyName -cne $p.packageFamily -or [string]$package.Version -ne $p.packageVersion -or $package.Status -ne 'Ok'){throw 'Independent installed package identity differs.'}
foreach($entry in $m.files.PSObject.Properties){if($entry.Name -match '[/\\]' -or (Get-FileHash -LiteralPath (Join-Path $package.InstallLocation $entry.Name)).Hash -ne $entry.Value){throw ('Native installed payload differs: '+$entry.Name)}}
$sessionPath=Join-Path $root runtime/desktop/session.json
# Recover only a dead recorded owner before checking a new launch permit.
if($Action -ne 'Check'){
    $pointer=Read-NativeJson (Join-Path $root app.current.json) 1048576
    $recovery=[IO.Path]::GetFullPath((Join-Path $root $pointer.entry))
    if(-not $recovery.StartsWith((Join-Path $root versions)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Recovery entry escaped versions.'}
    if((Get-FileHash -LiteralPath $recovery).Hash -ne $m.files.'MagicCommunity.dll'){throw 'Recovery assembly differs from the verified native package.'}
    & (Join-Path $root tools/dotnet/dotnet.exe) $recovery --root $root --native-taskbar-recover
    if($LASTEXITCODE -ne 0){throw 'Native taskbar recovery requires attention; no new desktop started.'}
}
if($Action -eq 'Check'){[pscustomobject]@{protocol=$p.protocol;package=$package.PackageFullName;installLocation=$package.InstallLocation;verified=$true};return}
if($Action -eq 'Stop'){
 . (Join-Path $PSScriptRoot 'desktop-transition.ps1');$transition=$null;$restored=$false
 try{

 $algorithm=[Security.Cryptography.SHA256]::Create();try{$key=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
 $session=if(Test-Path -LiteralPath $sessionPath){Read-NativeJson $sessionPath 16384}else{$null};$owned=$null
 if($session){$owned=Get-Process -Id $session.pid -ErrorAction SilentlyContinue;if($owned){$actual=[IO.Path]::GetFullPath($owned.Path);if($actual -ne (Join-Path $package.InstallLocation MagicCommunity.exe)){throw 'Running main process identity differs; no process stopped.'}}}
 $transition=if($owned){Start-MagicTransition -Root $root -Mode exit}else{$null}
 try{$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',('MagicCommunity-'+$key),[IO.Pipes.PipeDirection]::Out);try{$pipe.Connect(1200);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true;$writer.WriteLine('quit');$writer.Dispose()}finally{$pipe.Dispose()}}catch [TimeoutException]{if($owned){throw 'Owned main process did not accept a normal stop.'}}
 if($owned -and -not $owned.WaitForExit(8000)){throw 'Main process did not finish normal shutdown; no forced termination attempted.'}
 $restored=$true
 }finally{Complete-MagicTransition $transition $restored}
 return
}
$continuous=$p.acceptanceMode -ceq 'continuous'
if($p.acceptanceMode -and $p.acceptanceMode -cnotin @('timed','continuous')){throw 'Unknown native acceptance mode.'}
$permit=Read-NativeJson (Join-Path $root runtime/system/native-acceptance.json) 8192
if($permit.protocol -ne $p.protocol -or $permit.nonce -ne $p.nonce -or $permit.userSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Native acceptance ownership differs.'}
if($continuous -and ($permit.acceptanceMode -cne 'continuous' -or $null -ne $permit.durationSeconds -or $null -ne $permit.expiresUtc)){throw 'Continuous acceptance authorization differs.'}
if($permit.startedUtc -and ($continuous -or [DateTimeOffset]::Parse($permit.expiresUtc) -gt [DateTimeOffset]::UtcNow)){
    $running=Get-Process -Id $permit.ownerPid -ErrorAction SilentlyContinue
    if($running -and $running.StartTime.ToFileTimeUtc().ToString() -eq $permit.ownerCreated -and $running.Path -eq (Join-Path $package.InstallLocation MagicCommunity.exe)){[pscustomobject]@{protocol=$p.protocol;pid=$running.Id;expiresUtc=$permit.expiresUtc;acceptanceMode=$permit.acceptanceMode;status='existing_owned_session_reused'};return}
}
if(-not $SystemValidation -and -not $continuous){throw 'The independent backend is enabled only for explicitly authorized temporary validation.'}
if(-not $continuous){
 if(($permit.acceptanceMode -and $permit.acceptanceMode -cne 'timed') -or $permit.durationSeconds -ne 3600 -or $permit.startedUtc -or [DateTimeOffset]::Parse($permit.launchBeforeUtc) -le [DateTimeOffset]::UtcNow){throw 'One-hour acceptance is absent, consumed or expired.'}
}
if(-not ('MagicNativeActivation' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'system-runtime/NativeActivation.cs')}
. (Join-Path $PSScriptRoot 'desktop-transition.ps1');$transition=$null;$ready=$false
try{
 $transition=Start-MagicTransition -Root $root -Mode entrance -Logon:$Logon
 $entry=[MagicNativeActivation]::Start(($package.PackageFamilyName+'!App'),'')
 $until=[DateTimeOffset]::UtcNow.AddSeconds(30)
 while([DateTimeOffset]::UtcNow -lt $until){
    $path=Join-Path $root runtime/system/native-ready.json
    $receipt=Read-MagicNativeJson -Path $path -MaximumBytes 8192 -AllowTransient
    if($null -ne $receipt){if($receipt.active -eq $true -and $receipt.protocol -eq $p.protocol -and $receipt.nonce -eq $p.nonce -and (($continuous -and $receipt.acceptanceMode -ceq 'continuous' -and $null -eq $receipt.expiresUtc) -or (-not $continuous -and [DateTimeOffset]::Parse($receipt.expiresUtc) -gt [DateTimeOffset]::UtcNow))){$owner=Get-Process -Id $receipt.adapterPid -ErrorAction SilentlyContinue;if($owner -and $owner.Path -eq (Join-Path $package.InstallLocation MagicCommunity.exe) -and $owner.StartTime.ToFileTimeUtc().ToString() -eq $receipt.adapterCreated){$ready=$true;[pscustomobject]@{entryPid=$entry;protocol=$p.protocol;pid=$owner.Id;expiresUtc=$receipt.expiresUtc;acceptanceMode=$receipt.acceptanceMode;status='independent_native_ready_manual_pending'};break}}}
    Start-Sleep -Milliseconds 200
 }
 if(-not $ready){throw 'Independent native session did not confirm readiness; inspect startup logs.'}
}finally{Complete-MagicTransition $transition $ready}
