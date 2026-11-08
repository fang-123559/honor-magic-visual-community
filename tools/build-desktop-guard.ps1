param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'experiments/desktop-guard-build'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Guard output must remain in this project.'}
[void][IO.Directory]::CreateDirectory($output)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -property installationPath
$msvc=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
$env:INCLUDE=(Join-Path $msvc 'include')+';'+(Join-Path $sdk "Include/$sdkVersion/ucrt")+';'+(Join-Path $sdk "Include/$sdkVersion/shared")+';'+(Join-Path $sdk "Include/$sdkVersion/um")
$env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$sdkVersion/ucrt/x64")+';'+(Join-Path $sdk "Lib/$sdkVersion/um/x64")
& (Join-Path $msvc 'bin/Hostx64/x64/cl.exe') /nologo /O1 /MT /W4 /WX (Join-Path $root 'src/desktop-guard/desktop-guard.c') ("/Fo"+(Join-Path $output 'desktop-guard.obj')) ("/Fe"+(Join-Path $output 'MagicDesktopGuard.exe')) /link /SUBSYSTEM:WINDOWS user32.lib shell32.lib ole32.lib dwmapi.lib
if($LASTEXITCODE -ne 0){throw 'Native desktop restore guard build failed.'}
