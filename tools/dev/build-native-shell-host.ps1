param([ValidateSet('check','build','test')][string]$Phase='build')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if((& git -C $root branch --show-current).Trim() -notin @('codex/features','codex/bugfixes') -or -not(Test-Path -LiteralPath (Join-Path $root '.git') -PathType Leaf)){throw 'Use an isolated features or bugfixes worktree.'}
$common=(& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$primary=Split-Path -Parent $common
$toolchain=Join-Path $primary 'tools/rustup-native/toolchains/nightly-2026-05-22-x86_64-pc-windows-msvc'
if(-not(Test-Path -LiteralPath (Join-Path $toolchain 'bin/cargo.exe'))){throw 'Pinned Rust toolchain is unavailable.'}
$out=Join-Path $root ('artifacts/native-shell-host-build/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
[void][IO.Directory]::CreateDirectory($out)
# A native-first host still needs working compatibility pages for selected
# fallbacks and third-party modules. Build them from the same SDK source before
# embedding, then freeze the exact assets alongside the host source hashes.
$frontendReceipt=$null
if($Phase -eq 'build'){
    $version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'src/MagicCommunity/AppController.cs')),'const string Version = "([^"]+)"').Groups[1].Value
    $frontendOut=Join-Path $out 'frontends'
    & node (Join-Path $root 'experiments/native-host-patch/node_modules/tsx/dist/cli.mjs') (Join-Path $root 'tools/review/build_integrated_frontends.mts') $version $frontendOut *> (Join-Path $out 'frontend-build.log')
    if($LASTEXITCODE -ne 0){throw ('Complete frontend build failed; refusing to embed partial assets: '+$out)}
    $frontendReceipt=Get-Content -LiteralPath (Join-Path $frontendOut 'build-receipt.json') -Raw|ConvertFrom-Json
}
$keys=@('CARGO_HOME','RUSTUP_HOME','CARGO_TARGET_DIR','CARGO_BUILD_JOBS','CARGO_PROFILE_DEV_OPT_LEVEL','CARGO_PROFILE_DEV_DEBUG','RUSTC','INCLUDE','LIB','PATH');$saved=@{};foreach($key in $keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
try{
    $env:CARGO_HOME=Join-Path $root 'tools/cargo-home';$env:RUSTUP_HOME=Join-Path $primary 'tools/rustup-native';$env:CARGO_TARGET_DIR=Join-Path $root 'experiments/native-host-target';$env:CARGO_BUILD_JOBS='2';$env:CARGO_PROFILE_DEV_OPT_LEVEL='1';$env:CARGO_PROFILE_DEV_DEBUG='0';$env:RUSTC=Join-Path $toolchain 'bin/rustc.exe'
    if((Get-Item -LiteralPath $env:CARGO_HOME).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cargo cache must be private to the worktree.'}
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe';$vs=(& $vswhere -latest -products '*' -property installationPath).Trim();$msvc=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10';$sdkVersion=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
    $env:INCLUDE=(Join-Path $msvc 'include')+';'+((@('ucrt','shared','um')|ForEach-Object {Join-Path $sdk "Include/$sdkVersion/$_"}) -join ';');$env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")+';'+(Join-Path $sdk "Lib/$sdkVersion/um/x64")
    $env:PATH=(Join-Path $toolchain 'bin')+';'+(Join-Path $toolchain 'lib/rustlib/x86_64-pc-windows-msvc/bin')+';'+(Join-Path $msvc 'bin/Hostx64/x64')+';'+$saved.PATH
    $sourcePaths=@(& git -C $root ls-files --cached --others --exclude-standard -- 'experiments/native-host-patch/src/background' 'experiments/native-host-patch/libs/core/src' 'experiments/native-host-patch/src/Cargo.toml' 'experiments/native-host-patch/src/Cargo.lock')|Sort-Object -Unique
    $sourceHashes=@{};foreach($path in $sourcePaths){$sourceHashes[$path]=(Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash}
    Push-Location -LiteralPath (Join-Path $root 'experiments/native-host-patch')
    # Pure configuration tests do not initialize Tauri or resolve packaged assets.
    [string[]]$featureArgs=if($Phase -eq 'test'){@('--no-default-features')}else{@('--features','custom-protocol')}
    $testArgs=if($Phase -eq 'test'){@('widgets::native_','--','--nocapture')}else{@()}
    try{& (Join-Path $toolchain 'bin/cargo.exe') $Phase --manifest-path src/Cargo.toml --locked --offline @featureArgs --bin seelen-ui @testArgs *> (Join-Path $out 'cargo-check.log');$code=$LASTEXITCODE}finally{Pop-Location}
    if($code -ne 0){Get-Content -LiteralPath (Join-Path $out 'cargo-check.log') -Tail 60|Out-Host;throw ('Native popup host check failed: '+$out)}
    if($Phase -eq 'test' -and (Get-Content -LiteralPath (Join-Path $out 'cargo-check.log') -Raw) -notmatch 'test result: ok\. [1-9][0-9]* passed'){throw 'The selected native tests did not execute any cases.'}
    foreach($path in $sourcePaths){if((Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash -ne $sourceHashes[$path]){throw ('Source changed during host build; retry: '+$path)}}
    if($null -ne $frontendReceipt){foreach($asset in $frontendReceipt.assets){if((Get-FileHash -LiteralPath (Join-Path $root ('experiments/native-host-patch/dist/'+$asset.path)) -Algorithm SHA256).Hash -ne $asset.sha256){throw ('Frontend asset changed during host build; retry: '+$asset.path)}}}
    $files=@('desktop_drag.rs','loader.rs','manager.rs','mod.rs','native_popups.rs','native_panels.rs','native_shell.rs','native_shell_config.rs','native_toolbar.rs','native-toolbar-bluetooth.json','native-shell-strings.json','native_apps.rs','webview.rs')|ForEach-Object {@{path=('src/background/widgets/'+$_);sha256=(Get-FileHash -LiteralPath (Join-Path $root ('experiments/native-host-patch/src/background/widgets/'+$_)) -Algorithm SHA256).Hash;normalizedSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText((Join-Path $root ('experiments/native-host-patch/src/background/widgets/'+$_))).Replace("`r`n","`n")))))}}
    $hook=Join-Path $root 'experiments/native-host-patch/src/background/hook.rs'
    $files+=@{path='src/background/hook.rs';sha256=(Get-FileHash -LiteralPath $hook -Algorithm SHA256).Hash;normalizedSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($hook).Replace("`r`n","`n")))))}
    foreach($module in @('application.rs','infrastructure.rs')){
        $relative='src/background/modules/notifications/'+$module
        $modulePath=Join-Path $root ('experiments/native-host-patch/'+$relative)
        $files+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash;normalizedSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($modulePath).Replace("`r`n","`n")))))}
    }
    foreach($relative in @('src/background/modules/power/application.rs','src/background/modules/system_tray/application/mod.rs')){
        $modulePath=Join-Path $root ('experiments/native-host-patch/'+$relative)
        $files+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash;normalizedSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($modulePath).Replace("`r`n","`n")))))}
    }
    $frontendIdentity=if($null -ne $frontendReceipt){@{path=(Join-Path $frontendOut 'build-receipt.json');sha256=(Get-FileHash -LiteralPath (Join-Path $frontendOut 'build-receipt.json') -Algorithm SHA256).Hash;assetCount=$frontendReceipt.assets.Count}}else{$null}
    $receipt=@{sourceCommit=(& git -C $root rev-parse HEAD).Trim();files=@($files);output=$out;cargoPhase=$Phase;frontend=$frontendIdentity;scope=$(if($Phase -eq 'test'){'Owned pure native configuration tests; no runtime or user-state access'}else{'Owned host source build; no tests, install or live load'})}
    if($Phase -eq 'build'){$binary=Join-Path $env:CARGO_TARGET_DIR 'debug/seelen-ui.exe';Copy-Item -LiteralPath $binary -Destination (Join-Path $out 'seelen-ui.exe');$receipt.binary=Join-Path $out 'seelen-ui.exe';$receipt.sha256=(Get-FileHash -LiteralPath $receipt.binary -Algorithm SHA256).Hash}
    $receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'check.json') -Encoding utf8
    $receipt|ConvertTo-Json -Depth 4
}finally{foreach($key in $keys){[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}}
