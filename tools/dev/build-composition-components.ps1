param([switch]$Launch,[int]$BenchmarkCycles=0,[switch]$PopupBenchmark)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if((& git -C $root branch --show-current).Trim() -ne 'codex/features' -or -not(Test-Path -LiteralPath (Join-Path $root '.git') -PathType Leaf)){throw 'Use the features worktree.'}
$build=& (Join-Path $PSScriptRoot 'start-composition-trial.ps1') -BuildOnly|ConvertFrom-Json
$output=Join-Path $root ('artifacts/composition-components/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($output)
Copy-Item -LiteralPath (Join-Path $build.output 'backend') -Destination (Join-Path $output 'app') -Recurse
$app=Join-Path $output 'app'
Copy-Item -LiteralPath (Join-Path $build.output 'MagicCompositionTrial.exe') -Destination (Join-Path $app 'MagicDesktopComposition.exe')
@{schemaVersion=1;sha256=(Get-FileHash -LiteralPath (Join-Path $app 'MagicDesktopComposition.exe') -Algorithm SHA256).Hash;sourceFingerprint=$build.sourceFingerprint}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $app 'composition-renderer.json') -Encoding UTF8
$oldInclude=$env:INCLUDE;$oldLib=$env:LIB
try{& (Join-Path $root 'tools/build-desktop-guard.ps1') -OutputDirectory $app|Out-File -LiteralPath (Join-Path $output 'guard-build.log') -Encoding UTF8}finally{$env:INCLUDE=$oldInclude;$env:LIB=$oldLib}
if($LASTEXITCODE -ne 0){throw 'Guard build failed.'}
$receipt=@{output=$output;app=$app;sourceFingerprint=$build.sourceFingerprint;files=@(Get-ChildItem -LiteralPath $app -Recurse -File|ForEach-Object {@{name=$_.FullName.Substring($app.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}});scope='Preview70 desktop components with native renderer; ordinary isolated windows; no live takeover'}
$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'build.json') -Encoding UTF8
$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'artifacts/composition-components.current.json') -Encoding UTF8
if($Launch){
    $fixture=Join-Path $root ('artifacts/native-desktop-trial/components-'+[guid]::NewGuid().ToString('N'))
    $env:DOTNET_ROOT=Join-Path $root 'tools/dotnet';$env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $arguments=@('--root',('"'+$root+'"'),'--composition-components-preview','--trial-root',('"'+$fixture+'"'))
    if($PopupBenchmark){$arguments+='--popup-benchmark'}
    if($BenchmarkCycles){$arguments+=@('--benchmark-cycles',[string]$BenchmarkCycles)}
    $process=Start-Process -FilePath (Join-Path $app 'MagicCommunity.exe') -ArgumentList $arguments -WindowStyle Normal -PassThru
    $receipt.pid=$process.Id;$receipt.fixture=$fixture
}
$receipt|ConvertTo-Json -Depth 6
