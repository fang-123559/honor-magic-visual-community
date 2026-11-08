param(
 [Parameter(Mandatory=$true)][string]$Binaries,
 [Parameter(Mandatory=$true)][string]$BuildReceipt,
 [Parameter(Mandatory=$true)][string]$SignedStaticPayload,
 [Parameter(Mandatory=$true)][string]$BootPlan,
 [Parameter(Mandatory=$true)][string]$VersionId,
 [Parameter(Mandatory=$true)][string]$OwnerRoot,
 [Parameter(Mandatory=$true)][string]$Output
)
# Assembles explicit build outputs. Does not discover an installed AppX package,
# run a build, install a runtime, create acceptance evidence or select a version.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/desktop-native.ps1')
if($VersionId -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$'){throw 'Invalid immutable runtime version.'}
$out=[IO.Path]::GetFullPath($Output)
if(-not $out.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $out)){throw 'Use a new worktree artifacts directory.'}
$owner=[MagicCommunity.SystemRuntimeIdentity]::Plain($OwnerRoot)
$source=[MagicCommunity.SystemRuntimeIdentity]::Plain($Binaries)
$static=[MagicCommunity.SystemRuntimeIdentity]::Plain($SignedStaticPayload)
$build=Read-MagicSystemJson $BuildReceipt
if(@($build.binaries).Count -ne 4 -or -not $build.independentHook){throw 'Complete independent build receipt required.'}
& git -C $root merge-base --is-ancestor $build.sourceCommit HEAD
if($LASTEXITCODE -ne 0){throw 'Independent build is outside this source history.'}
foreach($entry in $build.files){
 $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
 if(-not $path.StartsWith((Join-Path $root 'experiments/native-host-patch')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-MagicSHA256 $path) -ne $entry.sha256){throw ('Frozen independent source differs: '+$entry.path)}
}
$plan=Read-MagicSystemJson $BootPlan
if(@($plan.resources).Count -ne 3){throw 'Explicit three-resource boot plan required.'}
$expected=@(@('theme','@default/theme'),@('plugin','@seelen/tb-keyboard-selector'),@('plugin','@default/power'))
for($i=0;$i -lt 3;$i++){if($plan.resources[$i].kind -ne $expected[$i][0] -or $plan.resources[$i].id -ne $expected[$i][1]){throw 'Boot resource allowlist differs.'}}
Assert-MagicHostStatic -Folder $static
[void][IO.Directory]::CreateDirectory($out)
foreach($name in @('magic-system-host.exe','magic-system-service.exe','magic-system-ctl.exe','sluhk.dll')){
 $file=[MagicCommunity.SystemRuntimeIdentity]::Plain((Join-Path $source $name))
 $record=@($build.binaries|Where-Object {$_.name -ceq $name})
 if($record.Count -ne 1 -or (Get-MagicSHA256 $file) -ne $record[0].sha256){throw ('Binary differs from independent build receipt: '+$name)}
 # Small protocol comparisons may be inlined into machine immediates in a DLL.
 # Freeze source + binary identity first; use retained owned IPC strings as the
 # feature sanity check rather than requiring a contiguous protocol literal.
 $bytes=[IO.File]::ReadAllBytes($file)
 $ascii=[Text.Encoding]::ASCII.GetString($bytes)
 $owned=if($name -eq 'sluhk.dll'){$ascii.Contains('magic-system-runtime.json') -and $ascii.Contains('magic-community-invalid-')}else{$ascii.Contains('Magic.System/1')}
 if(-not $owned){throw ('Not an independent protocol build: '+$name)}
 Copy-Item -LiteralPath $file -Destination (Join-Path $out $name)
}
foreach($name in @('static','SHA256SUMS','SHA256SUMS.sig')){Copy-Item -LiteralPath (Join-Path $static $name) -Destination (Join-Path $out $name) -Recurse}
# Preserve the upstream license and source provenance in every owned distribution.
Copy-Item -LiteralPath (Join-Path $root 'experiments/native-host-patch/LICENSE') -Destination (Join-Path $out 'SEELEN-AGPL-LICENSE')
$resources=@();$bootFiles=@()
for($i=0;$i -lt 3;$i++){
 $entry=$plan.resources[$i];$from=[MagicCommunity.SystemRuntimeIdentity]::Plain($entry.directory)
 $to=Join-Path $out ('boot/'+$i);[void][IO.Directory]::CreateDirectory($to)
 foreach($file in Get-ChildItem -LiteralPath $from -Recurse -Force){
  [void][MagicCommunity.SystemRuntimeIdentity]::Plain($file.FullName)
  $relative=$file.FullName.Substring($from.Length+1);$target=Join-Path $to $relative
  if($file.PSIsContainer){[void][IO.Directory]::CreateDirectory($target)}else{[void][IO.Directory]::CreateDirectory((Split-Path $target));Copy-Item -LiteralPath $file.FullName -Destination $target}
 }
 $metadata=[IO.Path]::GetFullPath((Join-Path $to $entry.metadata))
 if(-not $metadata.StartsWith($to+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Boot metadata escaped its resource.'}
 $resources+=@{kind=$entry.kind;id=$entry.id;path=$metadata.Substring($out.Length+1).Replace('\','/')}
 foreach($file in Get-ChildItem -LiteralPath $to -Recurse -File){$bootFiles+=@{path=$file.FullName.Substring($out.Length+1).Replace('\','/');sha256=(Get-MagicSHA256 $file.FullName)}}
}
$installed=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('MagicCommunity/SystemRuntime/'+$VersionId)
$serviceSha=Get-MagicSHA256 (Join-Path $out 'magic-system-service.exe')
@{owner=$owner;serviceExecutable=(Join-Path $installed 'magic-system-service.exe');serviceSha256=$serviceSha;files=$bootFiles;resources=$resources}|ConvertTo-Json -Depth 8|Write-MagicSystemUtf8 -LiteralPath (Join-Path $out 'community-boot.json')
@{upstream='Seelen UI 2.8.9';license='AGPL-3.0-or-later';sourceCommit=(& git -C $root rev-parse HEAD).Trim();feature='community-system';protocol='Magic.System/1';originalStaticSignatureRetained=$true;capabilityAcceptance='not_performed';compatibilityPluginsRetained=$true}|ConvertTo-Json|Write-MagicSystemUtf8 -LiteralPath (Join-Path $out 'SOURCE.json')
$files=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $out -Recurse -File){$files[$file.FullName.Substring($out.Length+1).Replace('\','/')]=Get-MagicSHA256 $file.FullName}
if($files.Count -gt 4096){throw 'Runtime payload exceeds manifest bound.'}
$manifest=[ordered]@{schemaVersion=1;protocol='Magic.System/1';instanceId=[guid]::NewGuid().ToString('N');projectRoot=$owner;dataDirectory=(Join-Path $owner 'data/system');files=$files;capabilities=@{}}
$manifest|ConvertTo-Json -Depth 8|Write-MagicSystemUtf8 -LiteralPath (Join-Path $out 'magic-system-runtime.json')
@{candidate=$out;installTarget=$installed;manifestSha256=(Get-MagicSHA256 (Join-Path $out 'magic-system-runtime.json'));activationPerformed=$false;acceptancePerformed=$false}|ConvertTo-Json