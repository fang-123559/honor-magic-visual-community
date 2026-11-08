# Read-only runtime discovery shared by lifecycle/resource tools. Never falls back
# to the upstream package when an enabled independent selection is invalid.
. (Join-Path $PSScriptRoot 'file-hash.ps1')
if(-not ('MagicCommunity.SystemRuntimeIdentity' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'system-runtime/RuntimeIdentity.cs')}
function Read-MagicSystemJson([string]$Path,[int]$Maximum=524288){
    if((Get-Item -LiteralPath $Path).Length -gt $Maximum){throw 'System runtime document exceeds bound.'}
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json)
}
function Resolve-MagicIndependentRuntime([string]$Root,$pointer,[switch]$ForShutdown){
    if($pointer.enabled -ne $true){return $null}
    if($pointer.schemaVersion -ne 1 -or $pointer.protocol -ne 'Magic.System/1' -or [IO.Path]::GetFullPath($pointer.owner) -ne $Root){throw 'Independent runtime selection ownership differs.'}
    $folder=[MagicCommunity.SystemRuntimeIdentity]::Installation((Split-Path ([IO.Path]::GetFullPath($pointer.manifest))))
    if([IO.Path]::GetFileName($pointer.manifest) -cne 'magic-system-runtime.json'){throw 'Invalid runtime manifest name.'}
    $manifestPath=[MagicCommunity.SystemRuntimeIdentity]::Payload($folder,'magic-system-runtime.json',$pointer.manifestSha256)
    $manifest=Read-MagicSystemJson $manifestPath
    if($manifest.schemaVersion -ne 1 -or $manifest.protocol -ne 'Magic.System/1' -or $manifest.instanceId -notmatch '^[0-9a-f]{32}$' -or [MagicCommunity.SystemRuntimeIdentity]::Plain($manifest.projectRoot) -ne $Root){throw 'Independent runtime identity differs.'}
    $data=[MagicCommunity.SystemRuntimeIdentity]::Plain($manifest.dataDirectory)
    if($data -ne (Join-Path $Root 'data/system')){throw 'Independent data namespace differs.'}
    $files=@($manifest.files.PSObject.Properties)
    if($files.Count -gt 4096){throw 'Too many runtime files.'}
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $files){if(-not $names.Add($file.Name)){throw 'Duplicate runtime file.'};[void][MagicCommunity.SystemRuntimeIdentity]::Payload($folder,$file.Name,[string]$file.Value)}
    foreach($file in @('magic-system-host.exe','magic-system-service.exe','magic-system-ctl.exe','sluhk.dll','community-boot.json','SHA256SUMS','SHA256SUMS.sig')){if(-not $names.Contains($file)){throw ('Incomplete independent runtime: '+$file)}}
    $checks=@{
        'system-data'=@('snapshot-actions','idle-resources');'window-tracking'=@('open-close-focus','mixed-dpi','elevated-window');
        'tray'=@('icons-actions','explorer-restart');'notifications'=@('authorize-revoke','arrive-dismiss','exit-restore');
        'taskbar'=@('dock-actions','uac-entry','crash-restore');'configuration'=@('import-checksums','plugin-order','startup-rollback')
    }
    $validation=$pointer.mode -eq 'validation'
    if(-not $validation){
    foreach($capability in $checks.Keys){
        $entry=$manifest.capabilities.$capability
        if($entry.status -ne 'accepted' -or $entry.providerSha256 -ne $manifest.files.'magic-system-host.exe'){throw ('Independent capability has no matching acceptance: '+$capability)}
        $receipt=Read-MagicSystemJson ([MagicCommunity.SystemRuntimeIdentity]::Payload($folder,$entry.evidencePath,$entry.evidenceSha256)) 65536
        if($receipt.protocol -ne $manifest.protocol -or $receipt.instanceId -ne $manifest.instanceId -or $receipt.capability -ne $capability -or [string]::IsNullOrWhiteSpace($receipt.reviewedBy)){throw 'Capability receipt identity differs.'}
        foreach($file in @('magic-system-host.exe','magic-system-service.exe','magic-system-ctl.exe','sluhk.dll')){if($receipt.runtimeFiles.$file -ne $manifest.files.$file){throw ('Stale capability receipt: '+$capability)}}
        foreach($check in $checks[$capability]){if($receipt.checks.$check.passed -ne $true -or [string]::IsNullOrWhiteSpace($receipt.checks.$check.evidence)){throw ('Incomplete capability receipt: '+$capability+'/'+$check)}}
    }
    }
    if($validation){
        $permit=Read-MagicSystemJson ([MagicCommunity.SystemRuntimeIdentity]::Payload($folder,'validation-permit.json',$pointer.validationPermitSha256)) 8192
        $expiry=[DateTimeOffset]::Parse($permit.expiresUtc)
        if($permit.protocol -ne 'Magic.System/1' -or $permit.instanceId -ne $manifest.instanceId -or $permit.purpose -ne 'interactive-validation' -or $permit.manifestSha256 -ne $pointer.manifestSha256 -or $permit.userSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or (-not $ForShutdown -and ($expiry -le [DateTimeOffset]::UtcNow -or $expiry -gt [DateTimeOffset]::UtcNow.AddHours(2)))){throw 'Temporary validation authorization is expired or differs.'}
    }
    return [pscustomobject]@{Independent=$true;ValidationOnly=$validation;InstallLocation=$folder;PackageFullName=('Magic.System/1/'+$manifest.instanceId);Version='1';HostExecutable=(Join-Path $folder 'magic-system-host.exe');ServiceExecutable=(Join-Path $folder 'magic-system-service.exe');CliExecutable=(Join-Path $folder 'magic-system-ctl.exe');HostProcessName='magic-system-host.exe';ServiceProcessName='magic-system-service.exe';DataDirectory=$data;LogPath=(Join-Path $env:LOCALAPPDATA ('MagicCommunity/System/'+$manifest.instanceId+'/logs/Seelen UI.log'));Manifest=$manifestPath;ManifestSha256=$pointer.manifestSha256;InstanceId=$manifest.instanceId}
}
function Get-MagicIndependentRuntime([string]$Root,[switch]$ForShutdown){
    $selection=Join-Path $Root 'runtime/system/selection.json'
    if(-not(Test-Path -LiteralPath $selection)){return $null}
    return Resolve-MagicIndependentRuntime $Root (Read-MagicSystemJson $selection 8192) -ForShutdown:$ForShutdown
}
function Get-MagicSystemProvider([string]$Root,[switch]$ForShutdown){
    $runtime=Get-MagicIndependentRuntime $Root -ForShutdown:$ForShutdown
    if($null -ne $runtime){return $runtime}
    $package=Get-AppxPackage -Name 'Seelen.SeelenUI'
    if($null -eq $package -and $ForShutdown){return $null}
    if($null -eq $package -or ($package.Version -ne '2.8.9.0' -and -not $ForShutdown)){
        $actual=if($package){[string]$package.Version}else{'not installed'}
        throw ('Seelen dependency changed: expected 2.8.9.0, actual '+$actual+'. Restore the pinned dependency before entering; exit recovery remains available.')
    }
    # A version check protects startup, but must never disable shutdown after
    # a Store update. Recovery accepts only the same recorded package family.
    $recovery=$package.Version -ne '2.8.9.0'
    if($ForShutdown){
        $owned=Read-MagicSystemJson (Join-Path $Root 'runtime/desktop/install-state.json')
        if($owned.packageFullName -notmatch '^Seelen\.SeelenUI_[0-9.]+_x64__p6yyn03m1894e$' -or $package.PackageFamilyName -ne 'Seelen.SeelenUI_p6yyn03m1894e'){throw 'Shutdown package family differs; no processes stopped.'}
        $recovery=$recovery -or $package.PackageFullName -ne $owned.packageFullName
        if($recovery -and (Get-AuthenticodeSignature -LiteralPath (Join-Path $package.InstallLocation 'slu-service.exe')).Status -ne 'Valid'){throw 'Recovery service signature is invalid.'}
    }
    return [pscustomobject]@{Independent=$false;RecoveryOnly=$recovery;InstallLocation=$package.InstallLocation;PackageFullName=$package.PackageFullName;Version=$package.Version;HostExecutable=(Join-Path $package.InstallLocation 'seelen-ui.exe');ServiceExecutable=(Join-Path $package.InstallLocation 'slu-service.exe');CliExecutable=(Join-Path $package.InstallLocation 'slu.exe');HostProcessName='seelen-ui.exe';ServiceProcessName='slu-service.exe';DataDirectory=(Join-Path $env:APPDATA 'com.seelen.seelen-ui');LogPath=(Join-Path $env:LOCALAPPDATA 'com.seelen.seelen-ui/logs/Seelen UI.log')}
}
function Write-MagicSystemUtf8 {
    param([Parameter(ValueFromPipeline=$true)][string]$Text,[Parameter(Mandatory=$true)][string]$LiteralPath)
    process {[IO.File]::WriteAllText($LiteralPath,$Text+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))}
}
