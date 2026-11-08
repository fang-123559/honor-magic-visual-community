param([string]$OutputDirectory,[switch]$Integrated,[string]$PackagePublisher='CN=MagicCommunity.Native.Local')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$branch=(& git -C $root branch --show-current).Trim();$main=($branch -eq 'main' -and (Test-Path (Join-Path $root '.git') -PathType Container));if(($branch -notin @('codex/features','codex/bugfixes','codex/ui') -and -not($branch -like 'codex/bugfixes-*' -and (Test-Path (Join-Path $root '.git') -PathType Leaf))) -and -not($Integrated -and $main)){throw 'Use a development worktree or explicit main integration.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/native-system/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $out.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $out)){throw 'Use a new worktree artifacts directory.'}
[void][IO.Directory]::CreateDirectory($out)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
$msvc=(Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10';$sdkVersion=(Get-ChildItem (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
$saved=@{};foreach($name in @('INCLUDE','LIB','PATH')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try{
 $env:INCLUDE=(Join-Path $msvc 'include')+';'+((@('ucrt','shared','um','winrt','cppwinrt')|ForEach-Object {Join-Path $sdk "Include/$sdkVersion/$_"}) -join ';')
 $env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")+';'+(Join-Path $sdk "Lib/$sdkVersion/um/x64")
 $env:PATH=(Join-Path $sdk "bin/$sdkVersion/x64")+';'+$saved.PATH
 $exe=Join-Path $out 'MagicSystemNative.exe'
 Push-Location $out
 try{
 & (Join-Path $msvc 'bin/Hostx64/x64/cl.exe') /nologo /std:c++20 /EHsc /O2 /MT /W4 /utf-8 /permissive- /DUNICODE /D_UNICODE @((Get-ChildItem (Join-Path $root 'src/system-native') -Filter '*.cpp' -File|Sort-Object Name).FullName) ("/Fe"+$exe) /link /SUBSYSTEM:CONSOLE /MANIFEST:EMBED ("/MANIFESTINPUT:"+(Join-Path $root 'src/MagicCommunity/app.manifest')) windowsapp.lib user32.lib advapi32.lib dwmapi.lib shcore.lib iphlpapi.lib powrprof.lib setupapi.lib ole32.lib propsys.lib wlanapi.lib oleaut32.lib uuid.lib uiautomationcore.lib windowscodecs.lib crypt32.lib shell32.lib imm32.lib pdh.lib *> (Join-Path $out 'build.log')
 if($LASTEXITCODE -ne 0){Get-Content (Join-Path $out 'build.log') -Tail 50|Out-Host;throw 'Native system build failed.'}
 & (Join-Path $msvc 'bin/Hostx64/x64/dumpbin.exe') /dependents $exe | Set-Content (Join-Path $out 'dependencies.txt')
 }finally{Pop-Location}
 $support=& (Join-Path $PSScriptRoot 'build-native-shell-support.ps1') -OutputDirectory $out -PackagePublisher $PackagePublisher | ConvertFrom-Json
 $sources=@(@('src/system-native','src/tray-capture','src/taskbar-guard','src/notification-permission','src/native-package-entry','src/window-action-helper')|ForEach-Object {Get-ChildItem (Join-Path $root $_) -File}|ForEach-Object {@{path=$_.FullName.Substring($root.Length+1);sha256=(Get-FileHash $_.FullName).Hash}})
 $receipt=@{protocol='Magic.System.Native/1';executable=$exe;bytes=(Get-Item $exe).Length;sha256=(Get-FileHash $exe).Hash;support=$support;sourceCommit=(& git -C $root rev-parse HEAD).Trim();sources=$sources;output=$out;desktopActivated=$false;scope='Independent Win32 milestone; no Seelen, Tauri, WebView2 or third-party runtime libraries'}
 $receipt|ConvertTo-Json -Depth 6|Set-Content (Join-Path $out 'build.json');$receipt|ConvertTo-Json -Depth 6
}finally{foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}}
