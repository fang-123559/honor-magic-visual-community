param([switch]$NativeSystem,[switch]$Activate,[switch]$BuildOnly,[ValidateSet('web','composition')][string]$DesktopRenderer='web',[string[]]$NativePopups)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'file-hash.ps1')
Set-Location -LiteralPath $projectRoot
$popupLedger=Get-Content -LiteralPath (Join-Path $projectRoot 'resources/native-popup-identities.json') -Raw -Encoding UTF8|ConvertFrom-Json
$popupAliases=@{};$popupSupported=@();foreach($identity in $popupLedger.entries){if($identity.scope -eq 'public'){$popupAliases[$identity.legacy]=$identity.canonical;$popupAliases[$identity.canonical]=$identity.canonical;$popupSupported+=$identity.canonical}}
if(-not $PSBoundParameters.ContainsKey('NativePopups')){$NativePopups=$popupSupported}
$NativePopups=@($NativePopups|ForEach-Object {if(-not $popupAliases.ContainsKey($_)){throw ('Unknown native popup identity: '+$_)};$popupAliases[$_]}|Select-Object -Unique)
$sourceVersion=Select-String -LiteralPath 'src/MagicCommunity/AppController.cs' -Pattern 'const string Version = "([^"]+)"'
$version=$sourceVersion.Matches[0].Groups[1].Value
if($version -notmatch '^\d+\.\d+\.\d+(?:-preview\.\d+)?$'){throw 'Invalid source version.'}
if($BuildOnly -and $Activate){throw 'BuildOnly cannot activate a version.'}
$destination=[IO.Path]::GetFullPath((Join-Path $projectRoot "versions/$version"))
if (-not $destination.StartsWith($projectRoot+'\versions\',[StringComparison]::OrdinalIgnoreCase)) { throw '版本输出目录超出项目。' }
if (Test-Path -LiteralPath $destination) { throw '版本目录已存在；保留已体验的构建，请修改版本号后再构建。' }
$env:DOTNET_CLI_HOME=Join-Path $projectRoot 'tools/dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
$runtime=Join-Path $projectRoot 'tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $runtime)) { throw '缺少项目工具链，先运行 tools/bootstrap_dotnet.py。' }
node 'widgets/desktop/build.mjs'
if ($LASTEXITCODE -ne 0) { throw '桌面 Widget 构建失败，未更新入口。' }
& $runtime publish 'src/MagicCommunity/MagicCommunity.csproj' -c Release --no-restore -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败，未更新入口。' }
& (Join-Path $PSScriptRoot 'build-desktop-guard.ps1') -OutputDirectory $destination
if ($LASTEXITCODE -ne 0) { throw '桌面恢复守护构建失败，未更新入口。' }
if($DesktopRenderer -eq 'composition'){
    & (Join-Path $PSScriptRoot 'build-composition-renderer.ps1') -OutputDirectory $destination
    if($LASTEXITCODE -ne 0){throw 'Native composition build failed; version pointer was not updated.'}
}
if($NativeSystem){
    if($DesktopRenderer -ne 'composition'){throw 'Independent native system requires Composition UI.'}
    $native=& (Join-Path $PSScriptRoot 'dev/build-native-system.ps1') -Integrated | ConvertFrom-Json
    foreach($name in @('MagicSystemNative.exe','MagicTrayCapture.dll','MagicTaskbarGuard.exe','MagicNotificationPermission.exe','MagicNativeEntry.exe','MagicWindowAction.exe')){Copy-Item -LiteralPath (Join-Path $native.output $name) -Destination (Join-Path $destination $name)}
    Copy-Item -LiteralPath (Join-Path $native.output build.json) -Destination (Join-Path $destination native-backend-build.json)
}
$evidence=Join-Path $projectRoot "evidence/preview/$version"
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
if(-not $BuildOnly){
& $runtime (Join-Path $destination 'MagicCommunity.dll') --self-test --root $projectRoot --report (Join-Path $evidence 'self-test.json')
if ($LASTEXITCODE -ne 0) { throw '状态与存储测试失败，未更新入口。' }
# Isolated fixture root: automated smoke must not replace real user layout.
& $runtime (Join-Path $destination 'MagicCommunity.dll') --smoke --root (Join-Path $evidence 'fixture') --report (Join-Path $evidence 'window-smoke.json') --render (Join-Path $evidence 'app-render.png')
if ($LASTEXITCODE -ne 0) { throw '窗口测试失败，未更新入口。' }
$test=Get-Content -LiteralPath (Join-Path $evidence 'self-test.json') -Raw | ConvertFrom-Json
$smoke=Get-Content -LiteralPath (Join-Path $evidence 'window-smoke.json') -Raw | ConvertFrom-Json
if (-not $test.allPass -or $smoke.error -or -not $smoke.mainWindowVisible -or @($smoke.screenWindows).Count -eq 0 -or @($smoke.screenWindows | Where-Object {-not $_.visible}).Count -gt 0) { throw '测试回执不满足启用条件。' }
}
$files=@(Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object {$_.Extension -ne '.pdb'} | ForEach-Object {
    [ordered]@{path="versions/$version/"+$_.FullName.Substring($destination.Length+1).Replace('\','/');sha256=(Get-MagicSHA256 $_.FullName);bytes=$_.Length}
})
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/MagicCommunity') -File | ForEach-Object {
    [ordered]@{path=$_.Name;sha256=(Get-MagicSHA256 $_.FullName)}
})
$pointer=[ordered]@{schemaVersion=1;version=$version;entry="versions/$version/MagicCommunity.dll";builtAt=(Get-Date).ToString('o');runtime='10.0.13';runtimeHostSha256=(Get-MagicSHA256 $runtime);files=$files;sourceFiles=$sourceFiles;desktopBridge=$true;unifiedDesktop=$true;guardSourceSha256=(Get-MagicSHA256 (Join-Path $projectRoot 'src/desktop-guard/desktop-guard.c'));acceptance='automated_only_manual_pending'}
if($NativeSystem){$pointer.nativeSystemProtocol='Magic.System.Native/1';$pointer.systemImplementation='independent-win32'}
$pointer.sourceCommit=(& git -C $projectRoot rev-parse HEAD).Trim()
$pointer.buildId='v'+$version.Replace('.','')+'-native-'+(Get-Date -Format 'yyyyMMdd-HHmmss')
$pointer.acceptance=if($BuildOnly){'build_only_manual_pending'}else{'automated_only_manual_pending'}
$pointer.testsRun=(-not $BuildOnly)
$pointer.desktopRenderer=$DesktopRenderer
$pointer.nativePopups=@(if($DesktopRenderer -eq 'composition'){$NativePopups})
$pointer.nativePopupProtocol=1
$pointer.nativeShell=if($DesktopRenderer -eq 'composition'){'composition'}else{'web'}
$json=$pointer | ConvertTo-Json -Depth 8
$utf8=New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $evidence 'build-receipt.json'),$json,$utf8)
if ($Activate) {
    & (Join-Path $PSScriptRoot 'activate-version.ps1') -Version $version
} else { Write-Output ('构建完成；固定入口尚未切换。仅构建：'+[bool]$BuildOnly) }
