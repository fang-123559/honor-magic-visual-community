param([Parameter(Mandatory=$true)][string]$FrontendReceipt)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if((& git -C $root branch --show-current).Trim() -notin @('codex/features') -or -not(Test-Path -LiteralPath (Join-Path $root '.git') -PathType Leaf)){throw 'Use an isolated features worktree.'}
$common=(& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$primary=Split-Path -Parent $common
$toolchain=Join-Path $primary 'tools/rustup-native/toolchains/nightly-2026-05-22-x86_64-pc-windows-msvc'
if(-not(Test-Path -LiteralPath (Join-Path $toolchain 'bin/cargo.exe'))){throw 'Pinned Rust toolchain is unavailable.'}
$out=Join-Path $root ('artifacts/community-system-build/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
[void][IO.Directory]::CreateDirectory($out)
$frontend=Get-Content -LiteralPath $FrontendReceipt -Raw|ConvertFrom-Json
foreach($asset in $frontend.assets){if((Get-FileHash -LiteralPath (Join-Path $root ('experiments/native-host-patch/dist/'+$asset.path)) -Algorithm SHA256).Hash -ne $asset.sha256){throw ('Frontend asset mismatch: '+$asset.path)}}
$sourcePaths=@(& git -C $root ls-files --cached --others --exclude-standard -- experiments/native-host-patch)|Sort-Object -Unique
if($LASTEXITCODE -ne 0){throw 'Cannot freeze build source.'}
$sourceHashes=@{};foreach($path in $sourcePaths){$file=Join-Path $root $path;if(Test-Path -LiteralPath $file -PathType Leaf){$sourceHashes[$path]=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}}
$keys=@('CARGO_HOME','RUSTUP_HOME','CARGO_TARGET_DIR','CARGO_BUILD_JOBS','CARGO_PROFILE_DEV_OPT_LEVEL','CARGO_PROFILE_DEV_DEBUG','RUSTC','INCLUDE','LIB','PATH');$saved=@{};foreach($key in $keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
try{
    $env:CARGO_HOME=Join-Path $root 'tools/cargo-home';$env:RUSTUP_HOME=Join-Path $primary 'tools/rustup-native';$env:CARGO_TARGET_DIR=Join-Path $root 'experiments/native-host-target';$env:CARGO_BUILD_JOBS='2';$env:CARGO_PROFILE_DEV_OPT_LEVEL='1';$env:CARGO_PROFILE_DEV_DEBUG='0';$env:RUSTC=Join-Path $toolchain 'bin/rustc.exe'
    if((Get-Item -LiteralPath $env:CARGO_HOME).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cargo cache must be private to the worktree.'}
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe';$vs=(& $vswhere -latest -products '*' -property installationPath).Trim();$msvc=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10';$sdkVersion=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
    $env:INCLUDE=(Join-Path $msvc 'include')+';'+((@('ucrt','shared','um')|ForEach-Object {Join-Path $sdk "Include/$sdkVersion/$_"}) -join ';');$env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")+';'+(Join-Path $sdk "Lib/$sdkVersion/um/x64")
    $env:PATH=(Join-Path $toolchain 'bin')+';'+(Join-Path $toolchain 'lib/rustlib/x86_64-pc-windows-msvc/bin')+';'+(Join-Path $msvc 'bin/Hostx64/x64')+';'+$saved.PATH
    Push-Location -LiteralPath (Join-Path $root 'experiments/native-host-patch')
    try{
        $cargo=Join-Path $toolchain 'bin/cargo.exe'
        & $cargo build --manifest-path src/Cargo.toml --locked --offline --features custom-protocol,community-system --bin magic-system-host --bin magic-system-service --bin magic-system-ctl *> (Join-Path $out 'cargo-check.log')
        $code=$LASTEXITCODE
        if($code -eq 0){& $cargo build -p sluhk --locked --offline --features community-system *> (Join-Path $out 'hook-check.log');$code=$LASTEXITCODE}
    }finally{Pop-Location}
    if($code -ne 0){Get-Content -LiteralPath (Join-Path $out 'cargo-check.log') -Tail 60|Out-Host;throw ('Community system binary build failed: '+$out)}
    foreach($name in @('magic-system-host.exe','magic-system-service.exe','magic-system-ctl.exe','sluhk.dll')){Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR ('debug/'+$name)) -Destination (Join-Path $out $name)}
    foreach($path in $sourceHashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash -ne $sourceHashes[$path]){throw ('Source changed during build: '+$path)}}
    foreach($asset in $frontend.assets){if((Get-FileHash -LiteralPath (Join-Path $root ('experiments/native-host-patch/dist/'+$asset.path)) -Algorithm SHA256).Hash -ne $asset.sha256){throw ('Frontend changed during build: '+$asset.path)}}
    $files=@($sourceHashes.Keys|Sort-Object|ForEach-Object {@{path=$_;sha256=$sourceHashes[$_]}})
    $receipt=@{frontendReceiptSha256=(Get-FileHash -LiteralPath $FrontendReceipt -Algorithm SHA256).Hash;sourceCommit=(& git -C $root rev-parse HEAD).Trim();files=@($files);output=$out;binaries=@(Get-ChildItem -LiteralPath $out -File|Where-Object {$_.Extension -in @('.exe','.dll')}|ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}});independentHook=$true;scope='Optimized dev binaries built for candidate packaging; no tests, install or live load'}
    $receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'build.json') -Encoding utf8
    $receipt|ConvertTo-Json -Depth 4
}finally{foreach($key in $keys){[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}}
