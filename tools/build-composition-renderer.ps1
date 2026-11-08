param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'copy-honor-fonts.ps1') -OutputDirectory $output
if(-not $output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Renderer output escaped project.'}
[void][IO.Directory]::CreateDirectory($output)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
$msvc=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory|Sort-Object Name -Descending|Select-Object -First 1).FullName
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$version=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
$oldInclude=$env:INCLUDE;$oldLib=$env:LIB;$oldPath=$env:PATH
try{
    $env:INCLUDE=(Join-Path $msvc 'include')+';'+((@('ucrt','shared','um','winrt','cppwinrt')|ForEach-Object {Join-Path $sdk "Include/$version/$_"}) -join ';')
    $env:LIB=(Join-Path $msvc 'lib/x64')+';'+(Join-Path $sdk "Lib/$version/ucrt/x64")+';'+(Join-Path $sdk "Lib/$version/um/x64")
    $env:PATH=(Join-Path $sdk "bin/$version/x64")+';'+$oldPath
    $native=Join-Path $output 'MagicDesktopComposition.exe'
    & (Join-Path $msvc 'bin/Hostx64/x64/cl.exe') /nologo /std:c++20 /EHsc /bigobj /O2 /MD /W4 /utf-8 /permissive- /DUNICODE /D_UNICODE ("/Fo"+(Join-Path $output 'composition.obj')) ("/Fe"+$native) (Join-Path $root 'src/composition-trial/composition-trial.cpp') /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED ("/MANIFESTINPUT:"+(Join-Path $root 'src/MagicCommunity/app.manifest')) windowsapp.lib d3d11.lib d2d1.lib dwrite.lib dxgi.lib user32.lib gdi32.lib ole32.lib oleaut32.lib shell32.lib shlwapi.lib comctl32.lib imm32.lib windowscodecs.lib crypt32.lib uiautomationcore.lib wtsapi32.lib dwmapi.lib
    if($LASTEXITCODE -ne 0){throw 'Composition renderer build failed.'}
    @{schemaVersion=1;sha256=(Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash;sourceCommit=(& git -C $root rev-parse HEAD).Trim()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'composition-renderer.json') -Encoding UTF8
}finally{$env:INCLUDE=$oldInclude;$env:LIB=$oldLib;$env:PATH=$oldPath}

# Copy only the runtime PNG manifest; the source library remains independently auditable.
$honorRoot=Join-Path $root 'assets/honor-icons'
$honorFiles=Get-Content -LiteralPath (Join-Path $honorRoot runtime-files.json) -Raw|ConvertFrom-Json
$honorManifest=Get-Content -LiteralPath (Join-Path $honorRoot manifest.json) -Raw|ConvertFrom-Json
foreach($relative in @($honorFiles.files)+@('manifest.json','catalog.json','NOTICE.md')){
    if($relative -match '\.\.' -or [IO.Path]::IsPathRooted($relative)){throw 'Honor icon path escaped catalog.'}
    $source=Join-Path $honorRoot $relative
    $entry=$honorManifest.files|Where-Object path -eq $relative
    if($entry -and (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256){throw ('Honor icon hash differs: '+$relative)}
    $target=Join-Path (Join-Path $output 'honor-icons') $relative
    [void][IO.Directory]::CreateDirectory((Split-Path $target))
    Copy-Item -LiteralPath $source -Destination $target
}
