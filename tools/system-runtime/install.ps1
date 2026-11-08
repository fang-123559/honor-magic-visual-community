param([Parameter(Mandatory=$true)][string]$Prepared,[Parameter(Mandatory=$true)][string]$ManifestSha256,[Parameter(Mandatory=$true)][string]$VersionId,[switch]$Apply,[switch]$AuthorizeValidation,[DateTimeOffset]$ValidationExpiresUtc=[DateTimeOffset]::MinValue)
$ErrorActionPreference='Stop'
$permitExpiry=[DateTimeOffset]::UtcNow.AddHours(1)
if($PSBoundParameters.ContainsKey('ValidationExpiresUtc')){
 if(-not $AuthorizeValidation -or $ValidationExpiresUtc -le [DateTimeOffset]::UtcNow -or $ValidationExpiresUtc -gt $permitExpiry){throw 'Explicit validation deadline must be within the authorized next hour.'}
 $permitExpiry=$ValidationExpiresUtc
}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/system-runtime.ps1')
if($VersionId -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$'){throw 'Invalid immutable runtime version.'}
$source=[MagicCommunity.SystemRuntimeIdentity]::Plain($Prepared)
$manifestPath=Join-Path $source 'magic-system-runtime.json'
if((Get-MagicSHA256 $manifestPath) -ne $ManifestSha256){throw 'Reviewed candidate manifest hash differs.'}
$manifest=Read-MagicSystemJson $manifestPath
if($manifest.schemaVersion -ne 1 -or $manifest.protocol -ne 'Magic.System/1' -or $manifest.instanceId -notmatch '^[0-9a-f]{32}$' -or [IO.Path]::GetFullPath($manifest.projectRoot) -ne $root -or [IO.Path]::GetFullPath($manifest.dataDirectory) -ne (Join-Path $root 'data/system')){throw 'Prepared runtime ownership/protocol differs.'}
$base=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MagicCommunity/SystemRuntime'
$target=Join-Path $base $VersionId
if(Test-Path -LiteralPath $target){throw 'Runtime version already exists; no overwrite.'}
$entries=@($manifest.files.PSObject.Properties)
if($entries.Count -gt 4096){throw 'Runtime file count exceeds bound.'}
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $entries){
 if(-not $names.Add($entry.Name) -or [IO.Path]::IsPathRooted($entry.Name) -or @($entry.Name.Replace('\','/').Split('/')|Where-Object {$_ -in @('','.','..') -or $_.Contains(':')}).Count){throw 'Invalid runtime payload path.'}
 $file=[MagicCommunity.SystemRuntimeIdentity]::Plain((Join-Path $source $entry.Name))
 if(-not $file.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-MagicSHA256 $file) -ne $entry.Value){throw 'Prepared payload differs.'}
}
foreach($name in @('magic-system-host.exe','magic-system-service.exe','magic-system-ctl.exe','sluhk.dll','community-boot.json','SHA256SUMS','SHA256SUMS.sig','SEELEN-AGPL-LICENSE')){if(-not $names.Contains($name)){throw ('Incomplete runtime: '+$name)}}
if(-not $Apply){@{source=$source;target=$target;manifestSha256=$ManifestSha256;files=$entries.Count;startupChanged=$false;selectionChanged=$false;mode='plan_only'}|ConvertTo-Json;return}
. (Join-Path $root 'tools/dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $root
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this explicit install step with normal Windows administrator authorization.'}
function Protect-Directory([string]$Path){
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$acl.SetAccessRuleProtection($true,$false)
 foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($entry[0]),[Security.AccessControl.FileSystemRights]$entry[1],([Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'),[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))}
 Set-Acl -LiteralPath $Path -AclObject $acl
}
foreach($dir in @((Split-Path $base),$base)){
 if(Test-Path -LiteralPath $dir){[void][MagicCommunity.SystemRuntimeIdentity]::Plain($dir);[MagicCommunity.SystemRuntimeIdentity]::Protected($dir)}else{[void][IO.Directory]::CreateDirectory($dir);Protect-Directory $dir}
}
[void][IO.Directory]::CreateDirectory($target);Protect-Directory $target
foreach($entry in $entries){
 $destination=Join-Path $target $entry.Name
 [void][IO.Directory]::CreateDirectory((Split-Path $destination))
 Copy-Item -LiteralPath (Join-Path $source $entry.Name) -Destination $destination
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $target 'magic-system-runtime.json')
[void][IO.Directory]::CreateDirectory((Join-Path $target 'logs'))
# New files inherit read/execute ACLs; make their owner the administrator group too.
foreach($file in Get-ChildItem -LiteralPath $target -Recurse -Force){
 if($file.PSIsContainer){Protect-Directory $file.FullName}else{$acl=Get-Acl -LiteralPath $file.FullName;$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $file.FullName -AclObject $acl}
}
[void][MagicCommunity.SystemRuntimeIdentity]::Installation($target)
foreach($entry in $entries){[void][MagicCommunity.SystemRuntimeIdentity]::Payload($target,$entry.Name,[string]$entry.Value)}
[void][MagicCommunity.SystemRuntimeIdentity]::Payload($target,'magic-system-runtime.json',$ManifestSha256)
if($AuthorizeValidation){
 $permit=Join-Path $target 'validation-permit.json'
 @{protocol='Magic.System/1';instanceId=$manifest.instanceId;purpose='interactive-validation';manifestSha256=$ManifestSha256;userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;expiresUtc=$permitExpiry.ToString('o');capabilityAcceptance='not_asserted'}|ConvertTo-Json|Write-MagicSystemUtf8 -LiteralPath $permit
 $acl=Get-Acl -LiteralPath $permit;$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $permit -AclObject $acl
 [MagicCommunity.SystemRuntimeIdentity]::Protected($permit)
}
$legacyRecord=Join-Path $root 'runtime/desktop/install-state.json'
$original=if(Test-Path -LiteralPath $legacyRecord){Read-MagicSystemJson $legacyRecord}else{[pscustomobject]@{nativeTaskbarState=[MagicCommunity.SystemRuntimeIdentity]::NativeTaskbarState()}}
if($original.nativeTaskbarState -notin @(0,1,2,3)){throw 'Original taskbar restoration state unavailable; installation retained but selection remains unchanged.'}
$journal=Join-Path $root ('runtime/system/installed-'+$manifest.instanceId+'.json')
[void][IO.Directory]::CreateDirectory((Split-Path $journal))
@{owner=$root;instanceId=$manifest.instanceId;manifest=(Join-Path $target 'magic-system-runtime.json');manifestSha256=$ManifestSha256;nativeTaskbarState=$original.nativeTaskbarState;selectionChanged=$false}|ConvertTo-Json|Write-MagicSystemUtf8 -LiteralPath $journal
@{target=$target;manifestSha256=$ManifestSha256;installed=$true;validationAuthorized=[bool]$AuthorizeValidation;startupChanged=$false;selectionChanged=$false;configurationImported=$false;acceptancePerformed=$false;note='Protected installation only. Import, reviewed capability evidence and explicit desktop activation remain separate.'}|ConvertTo-Json