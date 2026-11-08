param([Parameter(Mandatory=$true)][string]$Prepared,[switch]$Restore)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$directory=[IO.Path]::GetFullPath($Prepared)
$prefix=Join-Path $projectRoot 'evidence\toolbar\'
if(-not $directory.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Prepared toolbar directory is outside the project evidence root.'}
$manifest=Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if(-not $package){throw 'Fixed Seelen package is not installed for this Windows user.'}
$target=Join-Path $env:APPDATA 'com.seelen.seelen-ui\data\seelen-fancy-toolbar\state.yml'
if($manifest.owner -ne $projectRoot -or $manifest.package -ne $package.PackageFullName -or [IO.Path]::GetFullPath($manifest.target) -ne $target){throw 'Toolbar package, target or project ownership mismatch.'}
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
$before=Join-Path $directory 'before.yml';$candidate=Join-Path $directory 'candidate.yml'
if((Sha $before) -ne $manifest.beforeSha256 -or (Sha $candidate) -ne $manifest.candidateSha256){throw 'Prepared toolbar content changed.'}
$expected=if($Restore){$manifest.candidateSha256}else{$manifest.beforeSha256}
$source=if($Restore){$before}else{$candidate}
if((Sha $target) -ne $expected){throw 'Toolbar changed since preparation. Prepare again or merge the user changes; no overwrite performed.'}
$layoutHash=Sha (Join-Path $projectRoot 'data/layout.json')
$oldBytes=[IO.File]::ReadAllBytes($target)
$nextBytes=[IO.File]::ReadAllBytes($source)
$applied=$false;$stopped=$false;$completed=$false
$temporary=$target+'.magic-'+[guid]::NewGuid().ToString('N')+'.tmp'
$nativeBackup=$temporary+'.bak'
$receiptName=if($Restore){'restore-receipt.json'}else{'apply-receipt.json'}
try {
    # The toolbar manager keeps state in memory. Stop before atomically replacing
    # its own data file, so it cannot overwrite this change with a pending save.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stop-desktop.ps1')
    if($LASTEXITCODE -ne 0){throw 'Desktop stop failed; toolbar data not written.'}
    $stopped=$true
    if((Sha $target) -ne $expected){throw 'Toolbar changed while stopping; data not overwritten.'}
    [IO.File]::WriteAllBytes($temporary,$nextBytes)
    # Windows PowerShell 5.1 binds a null string argument as an empty path.
    # Use an explicit sibling backup on the destination volume instead.
    [IO.File]::Replace($temporary,$target,$nativeBackup)
    $applied=$true
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'launch-magic.ps1') -Mode App
    if($LASTEXITCODE -ne 0){throw 'Desktop re-entry failed after toolbar preference change.'}
    $popupProfile=$null;$popupReceiptPath=Join-Path $projectRoot 'runtime/latency/apply-receipt.json'
    if(Test-Path -LiteralPath $popupReceiptPath){try{$popupProfile=(Get-Content -LiteralPath $popupReceiptPath -Raw -Encoding UTF8|ConvertFrom-Json).profile}catch{Write-Warning 'Optional popup receipt could not be read; toolbar application is retained.'}}
    $receipt=[ordered]@{at=(Get-Date).ToString('o');owner=$projectRoot;restored=[bool]$Restore;target=$target;targetSha256=(Sha $target);expectedSha256=(Sha $source);layoutUnchanged=($layoutHash -eq (Sha (Join-Path $projectRoot 'data/layout.json')));popupProfile=$popupProfile;manualAcceptance='pending';scope='Configuration persisted and fixed entry ready; actual visual/IME/popup input acceptance separate'}
    if($receipt.targetSha256 -ne $receipt.expectedSha256){throw 'Toolbar state differs from the prepared content after startup; inspect before further changes.'}
    $receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $directory $receiptName) -Encoding UTF8
    $completed=$true
    $receipt|ConvertTo-Json -Depth 6
} catch {
    $failure=$_.Exception.Message
    if($applied){
        # A failed re-entry may leave the GUI running. Stop it before restoring,
        # and refuse to overwrite concurrent edits from the user's settings UI.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stop-desktop.ps1')
        if($LASTEXITCODE -eq 0 -and (Sha $target) -eq (Sha $source)){
            [IO.File]::WriteAllBytes($temporary,$oldBytes)
            [IO.File]::Replace($temporary,$target,$nativeBackup)
        } else {Write-Warning 'Could not verify safe rollback of toolbar data; preserved for review.'}
    }
    if($stopped){
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'launch-magic.ps1') -Mode App
        if($LASTEXITCODE -ne 0){Write-Warning 'Fixed entry recovery also failed; inspect startup logs.'}
    }
    throw $failure
} finally {
    if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}
    if($completed -and (Test-Path -LiteralPath $nativeBackup)){Remove-Item -LiteralPath $nativeBackup -Force}
}
