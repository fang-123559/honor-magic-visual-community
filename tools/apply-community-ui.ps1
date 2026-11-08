param([switch]$ValidateOnly,[switch]$RestoreOriginal,[switch]$InputMethodOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$pointer=Join-Path $root 'runtime/desktop/community-ui.current.json'
if(-not(Test-Path -LiteralPath $pointer)){return}
$state=Get-Content -LiteralPath $pointer -Raw -Encoding UTF8|ConvertFrom-Json
if(-not $state.enabled -and -not $RestoreOriginal){return}
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if(-not $package -or $state.owner -ne $root -or $state.package -ne $package.PackageFullName -or $state.version -notmatch '^\d+\.\d+\.\d+$'){throw 'Community UI ownership/package/version differs.'}
$directory=[IO.Path]::GetFullPath((Join-Path $root ('profiles/community-ui/'+$state.version)))
if([IO.Path]::GetFullPath($state.directory) -ne $directory){throw 'Community UI path differs.'}
foreach($file in $state.files){
  $path=[IO.Path]::GetFullPath((Join-Path $directory $file.path))
  if(-not $path.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-MagicSHA256 $path) -ne $file.sha256){throw 'Community UI payload changed.'}
}
if(@($state.resources).Count -ne 2 -or $state.resources[0].kind -ne 'theme' -or $state.resources[0].path -ne 'theme/metadata.yml' -or $state.resources[0].id -ne '@default/theme' -or $state.resources[1].kind -ne 'plugin' -or $state.resources[1].path -ne 'ime/metadata.yml' -or $state.resources[1].id -ne '@seelen/tb-keyboard-selector'){throw 'Community UI resource allowlist differs.'}
if($ValidateOnly){'Community UI resources validated.';return}
if($InputMethodOnly -and $RestoreOriginal){throw 'InputMethodOnly cannot restore upstream resources.'}
if($InputMethodOnly){
  # CLI plugin load also emits an enable intent. Respect an intentionally absent
  # toolbar item; normal community-first boot already registers its definition.
  $layout=Join-Path $env:APPDATA 'com.seelen.seelen-ui/data/seelen-fancy-toolbar/state.yml'
  if(-not(Test-Path -LiteralPath $layout) -or -not(Select-String -LiteralPath $layout -SimpleMatch '@seelen/tb-keyboard-selector' -Quiet)){
    Write-Output 'Input method is not placed in the toolbar; current layout retained.';return
  }
}
$hostPath=Get-MagicDesktopHost $package $root
foreach($resource in $state.resources){
  if($InputMethodOnly -and $resource.kind -ne 'plugin'){continue}
  $target=if(-not $RestoreOriginal){Join-Path $directory $resource.path}elseif($resource.kind -eq 'theme'){Join-Path $package.InstallLocation 'static/themes/default/metadata.yml'}else{Join-Path $package.InstallLocation 'static/widgets/keyboard-selector/toolbar-plugin.yml'}
  $info=New-Object Diagnostics.ProcessStartInfo
  $info.FileName=Join-Path $package.InstallLocation 'slu.exe';$info.Arguments='resource load '+$resource.kind+' "'+$target+'"';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
  $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
  $child=New-Object Diagnostics.Process;$child.StartInfo=$info
  try{
    [void]$child.Start();$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
    if(-not $child.WaitForExit(15000)){$child.Kill();throw 'Community UI resource CLI timed out.'}
    $child.WaitForExit();if($child.ExitCode -ne 0){throw ('Community UI resource load failed: '+$stderr.Result)}
  }finally{$child.Dispose()}
}
@{at=(Get-Date).ToString('o');version=$state.version;host=$hostPath;restoredOriginal=[bool]$RestoreOriginal;inputMethodOnly=[bool]$InputMethodOnly;scope='Selected resource registry hot-load; actual rendered/input acceptance separate'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'runtime/desktop/community-ui-receipt.json') -Encoding UTF8
