$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if((& git -C $root branch --show-current).Trim() -notin @('codex/features','codex/ui') -or -not(Test-Path -LiteralPath (Join-Path $root '.git') -PathType Leaf)){throw 'Use an isolated features or UI worktree.'}
$common=(& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$primary=Split-Path -Parent $common
$toolchain=Join-Path $primary 'tools/rustup-native/toolchains/nightly-2026-05-22-x86_64-pc-windows-msvc'
if(-not(Test-Path -LiteralPath (Join-Path $toolchain 'bin/cargo.exe'))){throw 'Pinned Rust toolchain is unavailable.'}
$out=Join-Path $root ('artifacts/native-popup-host-check/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
[void][IO.Directory]::CreateDirectory($out)
$keys=@('CARGO_HOME','RUSTUP_HOME','CARGO_TARGET_DIR','CARGO_BUILD_JOBS','CARGO_PROFILE_DEV_OPT_LEVEL','CARGO_PROFILE_DEV_DEBUG','RUSTC','INCLUDE','LIB','PATH');$saved=@{};foreach($key in $keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
try{
    $env:CARGO_HOME=Join-Path $root 'tools/cargo-home';$env:RUSTUP_HOME=Join-Path $primary 'tools/rustup-native';$env:CARGO_TARGET_DIR=Join-Path $root 'experiments/native-host-target';$env:CARGO_BUILD_JOBS='2';$env:CARGO_PROFILE_DEV_OPT_LEVEL='1';$env:CARGO_PROFILE_DEV_DEBUG='0';$env:RUSTC=Join-Path $toolchain 'bin/rustc.exe'
    if((Get-Item -LiteralPath $env:CARGO_HOME).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cargo cache must be private to the worktree.'}
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe';$vs=(& $vswhere -latest -products '*' -property installationPath).Trim();$msvc=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10';$sdkVersion=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
    $env:INCLUDE=(Join-Path $msvc 'include')+';'+((@('ucrt','shared','um')|ForEach-Object {Join-Path $sdk "Include/$sdkVersion/$_"}) -join ';');$env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")+';'+(Join-Path $sdk "Lib/$sdkVersion/um/x64")
    $env:PATH=(Join-Path $toolchain 'bin')+';'+(Join-Path $toolchain 'lib/rustlib/x86_64-pc-windows-msvc/bin')+';'+(Join-Path $msvc 'bin/Hostx64/x64')+';'+$saved.PATH
    Push-Location -LiteralPath (Join-Path $root 'experiments/native-host-patch')
    try{& (Join-Path $toolchain 'bin/cargo.exe') check --manifest-path src/Cargo.toml --locked --offline --features custom-protocol --bin seelen-ui *> (Join-Path $out 'cargo-check.log');$code=$LASTEXITCODE}finally{Pop-Location}
    if($code -ne 0){Get-Content -LiteralPath (Join-Path $out 'cargo-check.log') -Tail 60|Out-Host;throw ('Native popup host check failed: '+$out)}
    $files=@('desktop_drag.rs','loader.rs','manager.rs','mod.rs','native_popups.rs','native_panels.rs','native_shell.rs','native_shell_config.rs','native_apps.rs')|ForEach-Object {@{path=('src/background/widgets/'+$_);sha256=(Get-FileHash -LiteralPath (Join-Path $root ('experiments/native-host-patch/src/background/widgets/'+$_)) -Algorithm SHA256).Hash}}
    $receipt=@{sourceCommit=(& git -C $root rev-parse HEAD).Trim();files=@($files);output=$out;cargoCheck=$true;scope='Source check only; no host build, tests, install or live load'}
    $receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'check.json') -Encoding utf8
    $receipt|ConvertTo-Json -Depth 4
}finally{foreach($key in $keys){[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}}
