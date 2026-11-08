param([switch]$RestoreOriginal,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'file-hash.ps1')
$pointer=Join-Path $root 'runtime/desktop/power-panel.current.json'
if(-not(Test-Path -LiteralPath $pointer)){return}
$state=Get-Content -LiteralPath $pointer -Raw -Encoding UTF8|ConvertFrom-Json
if($state.enabled -ne $true -and -not $RestoreOriginal){return}
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if(-not $package -or $state.owner -ne $root -or $state.package -ne $package.PackageFullName){throw 'Power panel baseline/ownership differs.'}
$directory=[IO.Path]::GetFullPath($state.directory)
$version=Split-Path $directory -Leaf
if($version -notmatch '^\d+\.\d+\.\d+$' -or $directory -ne (Join-Path $root ('profiles/power-panel/'+$version))){throw 'Power panel directory differs.'}
$manifest=Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.owner -ne $root -or $manifest.version -ne $version){throw 'Power plugin manifest ownership/version differs.'}
foreach($file in $manifest.files){
  $path=[IO.Path]::GetFullPath((Join-Path $directory $file.path))
  if((Split-Path $path) -ne $directory -or (Get-MagicSHA256 $path) -ne $file.sha256){throw 'Power plugin payload changed.'}
}
if($ValidateOnly){Write-Output 'Power plugin candidate verified.';return}
. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$selected=Get-MagicDesktopHost $package $root
if(-not $RestoreOriginal -and $selected -eq (Join-Path $package.InstallLocation 'seelen-ui.exe')){throw 'Power panel requires the verified community toolbar host.'}
$target=if($RestoreOriginal){Join-Path $package.InstallLocation 'static/plugins/tb_default_power/metadata.yml'}else{Join-Path $directory 'metadata.yml'}
$process=Start-Process -FilePath (Join-Path $package.InstallLocation 'slu.exe') -ArgumentList ('resource load plugin "'+$target+'"') -WindowStyle Hidden -PassThru
if(-not $process.WaitForExit(10000)){$process.Kill();throw 'Power plugin load timed out.'}
if($process.ExitCode -ne 0){throw 'Power plugin resource load failed.'}
@{at=(Get-Date).ToString('o');profile=$manifest.version;host=$selected;restoredOriginal=[bool]$RestoreOriginal;scope='Resource CLI completion only; real click acceptance separate'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/desktop/power-panel-receipt.json') -Encoding UTF8
