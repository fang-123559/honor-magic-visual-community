param([switch]$BuildOnly,[switch]$NoBuild,[switch]$AllDisplays)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')
$branch=(& git -C $root branch --show-current).Trim()
if($branch -ne 'codex/features'){throw 'Run this prototype only from the features worktree.'}
$common=(& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($common)) -eq $root){throw 'The live primary checkout is not a prototype workspace.'}
$stampPath=Join-Path $root 'artifacts/native-desktop-trial-build.json'
function Source-Fingerprint {
    $paths=@(& git -C $root ls-files --cached --others --exclude-standard -- 'src/MagicCommunity' 'widgets/desktop' 'tools/dev/check-worktree.ps1' 'tools/dev/start-native-desktop-trial.ps1') | Sort-Object -Unique
    $rows=@($paths | ForEach-Object {$path=Join-Path $root $_;if(Test-Path -LiteralPath $path -PathType Leaf){$_+':'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}})
    $sha=[Security.Cryptography.SHA256]::Create()
    try{([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
$fingerprint=Source-Fingerprint
if($NoBuild){
    if(-not(Test-Path -LiteralPath $stampPath)){throw 'No prototype build receipt. Build first.'}
    $build=Get-Content -LiteralPath $stampPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($build.sourceFingerprint -ne $fingerprint){throw 'Source changed since this build. Rebuild before starting.'}
    $appRoot=[IO.Path]::GetFullPath([string]$build.appRoot)
    if(-not $appRoot.StartsWith($root+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build path escaped the worktree artifacts directory.'}
    foreach($file in $build.files){if((Get-FileHash -LiteralPath (Join-Path $appRoot $file.name) -Algorithm SHA256).Hash -ne $file.sha256){throw ('Build hash changed: '+$file.name)}}
}else{
    $receipt=(& (Join-Path $root 'tools/dev/check-worktree.ps1') -BuildOnly | Out-String) | ConvertFrom-Json
    if($receipt.allPass -ne $true -or $receipt.buildOnly -ne $true){throw 'Build did not complete.'}
    if((Source-Fingerprint) -ne $fingerprint){throw 'Source changed during the build. Rebuild.'}
    $appRoot=Join-Path $receipt.output 'app'
    $files=@(Get-ChildItem -LiteralPath $appRoot -File | ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    $build=@{at=(Get-Date -Format o);appRoot=$appRoot;sourceFingerprint=$fingerprint;files=$files;sourceCommit=(& git -C $root rev-parse HEAD).Trim();scope='Isolated source build; no tests or desktop processes executed.'}
    $build|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $stampPath -Encoding UTF8
}
$env:DOTNET_ROOT=Join-Path $root 'tools/dotnet'
$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
$exe=Join-Path $appRoot 'MagicCommunity.exe'
if($BuildOnly){$build|ConvertTo-Json -Depth 5;return}
# Interactive trial requested by the person running this launcher. Normal
# windows only; this entry never invokes formal desktop start/apply/load tools.
$arguments=@('--root',('"'+$root+'"'),'--native-desktop-trial')
if($AllDisplays){$arguments+='--all-displays'}
$process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Normal
@{pid=$process.Id;executable=$exe;sourceFingerprint=$fingerprint;scope='Interactive isolated native trial. Close all trial windows to exit.'}|ConvertTo-Json
