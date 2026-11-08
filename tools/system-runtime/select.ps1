param([Parameter(Mandatory=$true)][string]$Manifest,[Parameter(Mandatory=$true)][string]$ManifestSha256,[ValidateSet('production','validation')][string]$Mode='production',[switch]$Apply)
# Selection is explicit and offline. It neither stops the old desktop nor starts
# the new one, and it never manufactures capability acceptance receipts.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/desktop-native.ps1')
$folder=[MagicCommunity.SystemRuntimeIdentity]::Installation((Split-Path ([IO.Path]::GetFullPath($Manifest))))
$path=[MagicCommunity.SystemRuntimeIdentity]::Payload($folder,'magic-system-runtime.json',$ManifestSha256)
$value=Read-MagicSystemJson $path
$pointer=[ordered]@{schemaVersion=1;protocol='Magic.System/1';enabled=$true;owner=$root;manifest=$path;manifestSha256=$ManifestSha256;mode=$Mode}
if($Mode -eq 'validation'){$pointer.validationPermitSha256=Get-MagicSHA256 (Join-Path $folder 'validation-permit.json')}
$runtime=Resolve-MagicIndependentRuntime $root ([pscustomobject]$pointer)
if(-not $Apply){@{runtime=$runtime.HostExecutable;mode=$Mode;startupChanged=$false;selectionChanged=$false;temporaryExplicitLaunchRequired=($Mode -eq 'validation')}|ConvertTo-Json;return}
. (Join-Path $root 'tools/dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $root
$legacyHost=$null;$legacyService=$null
$legacyRecord=Join-Path $root 'runtime/desktop/install-state.json'
if(Test-Path -LiteralPath $legacyRecord){$legacy=Read-MagicSystemJson $legacyRecord;$legacyHost=Join-Path $legacy.installLocation 'seelen-ui.exe';$legacyService=Join-Path $legacy.installLocation 'slu-service.exe'}
foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='MagicCommunity.exe' OR Name='magic-system-host.exe' OR Name='magic-system-service.exe' OR Name='seelen-ui.exe' OR Name='slu-service.exe'")){
 $image=Get-MagicLiveProcessPath $process.ProcessId
 if($image -and ($image.StartsWith($root+'\versions\',[StringComparison]::OrdinalIgnoreCase) -or $image.StartsWith($root+'\profiles\native-host\',[StringComparison]::OrdinalIgnoreCase) -or $image -in @($runtime.HostExecutable,$runtime.ServiceExecutable,$legacyHost,$legacyService))){throw 'Stop the current owned desktop before selecting a different backend.'}
}
$installed=Read-MagicSystemJson (Join-Path $root ('runtime/system/installed-'+$runtime.InstanceId+'.json'))
if($installed.owner -ne $root -or $installed.manifestSha256 -ne $ManifestSha256 -or $installed.nativeTaskbarState -notin @(0,1,2,3)){throw 'Protected installation/restoration journal differs.'}
$backup=Join-Path $root ('evidence/system-selection/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($backup)
function Write-Pointer([string]$Name,[string]$Text){
 $target=Join-Path $root ('runtime/system/'+$Name)
 if(Test-Path -LiteralPath $target){Copy-Item -LiteralPath $target -Destination (Join-Path $backup $Name)}
 $temp=$target+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
 [IO.File]::WriteAllText($temp,$Text,[Text.UTF8Encoding]::new($false))
 if(Test-Path -LiteralPath $target){[IO.File]::Replace($temp,$target,(Join-Path $backup ($Name+'.replaced')))}else{[IO.File]::Move($temp,$target)}
}
# Publish selection last. A failure before it leaves the existing backend owner.
Write-Pointer 'install-state.json' ($installed|ConvertTo-Json -Depth 6)
Write-Pointer 'selection.json' ($pointer|ConvertTo-Json -Depth 6)
@{selected=$runtime.HostExecutable;mode=$Mode;backup=$backup;started=$false;startupChanged=$false;capabilityAcceptance=$($Mode -eq 'production');launch=$(if($Mode -eq 'validation'){'tools/launch-magic.ps1 -Mode Desktop -SystemValidation'}else{'tools/launch-magic.ps1 -Mode Desktop'})}|ConvertTo-Json