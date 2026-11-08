param([switch]$RestoreOriginal)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$record=Join-Path $projectRoot 'runtime/latency/popup-residency.json'
if (-not (Test-Path -LiteralPath $record)) { return }
$state=Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if ($package.PackageFullName -ne $state.package -or $state.owner -ne $projectRoot) { throw 'Popup profile package/ownership mismatch.' }
$cli=Join-Path $package.InstallLocation 'slu.exe'
function Invoke-Resource([string]$verb,[string]$path) {
    $process=Start-Process -FilePath $cli -ArgumentList ('resource '+$verb+' widget "'+$path+'"') -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw "Popup resource $verb did not complete." }
}
foreach($property in $state.widgets.PSObject.Properties) {
    $row=$property.Value
    $folder=[IO.Path]::GetFullPath($row.directory)
    if (-not $folder.StartsWith($projectRoot+'\profiles\seelen-2.8.9-popup-residency-v1\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Popup overlay escapes owned profile directory.' }
    $metadata=Join-Path $folder 'metadata.json'
    if ((Get-FileHash -LiteralPath $metadata -Algorithm SHA256).Hash -ne $row.metadataSha256) { throw 'Popup overlay hash mismatch.' }
    $original=Join-Path $package.InstallLocation ('static/widgets/'+$property.Name+'/metadata.yml')
    if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $row.sourceMetadataSha256) { throw 'Popup upstream definition changed.' }
    # First replace only the definition in the resource map. The manager retains its
    # old deployment until that entry is removed. Unloading OUR path then removes that
    # deployment, while original bundled toolbar plugins retain their real origin.
    Invoke-Resource 'load' $metadata
    Invoke-Resource 'unload' $metadata
    if ($RestoreOriginal) { Invoke-Resource 'load' $original }
    else { Invoke-Resource 'load' $metadata }
}
Write-Output $(if($RestoreOriginal){'Original popup definitions restored.'}else{'Pinned popup residency profile applied; runtime/visual verification still required.'})
