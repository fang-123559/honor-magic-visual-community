param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'file-hash.ps1')
$app=Get-Content -LiteralPath (Join-Path $root 'app.current.json') -Raw -Encoding UTF8|ConvertFrom-Json
$hostSelection=Get-Content -LiteralPath (Join-Path $root 'runtime/desktop/host.current.json') -Raw -Encoding UTF8|ConvertFrom-Json
$activeApp=[IO.Path]::GetFullPath((Join-Path $root ('versions/'+$app.version)))
$activeHost=[IO.Path]::GetFullPath((Split-Path $hostSelection.executable))
$targets=@()
foreach($base in @((Join-Path $root 'versions'),(Join-Path $root 'profiles/native-host'))){
    $base=[IO.Path]::GetFullPath($base)
    foreach($folder in @(Get-ChildItem -LiteralPath $base -Directory)){
        $resolved=(Resolve-Path -LiteralPath $folder.FullName).ProviderPath
        if($resolved -in @($activeApp,$activeHost)){continue}
        if([IO.Path]::GetDirectoryName($resolved) -ne $base -or -not $resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $folder.Name -notmatch '^0\.[12]\.0-(preview\.[0-9]+|rc\.[0-9]+)$'){throw 'Unexpected retirement target; nothing deleted.'}
        if(($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $resolved -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Reparse point in retirement target.'}
        $files=@(Get-ChildItem -LiteralPath $resolved -File -Recurse)
        $targets+=@{path=$resolved;bytes=($files|Measure-Object Length -Sum).Sum;files=$files.Count}
    }
}
$record=@{at=(Get-Date).ToString('o');owner=$root;activeApp=$activeApp;activeHost=$activeHost;targets=$targets;destination='Windows Recycle Bin';dataTouched=$false;applied=$false}
$evidence=Join-Path $root 'evidence/startup/2026-10-17'
[void][IO.Directory]::CreateDirectory($evidence)
$runId=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$inventoryPath=Join-Path $evidence ('retirement-'+$runId+'-inventory.json');$resultPath=Join-Path $evidence ('retirement-'+$runId+'-result.json')
$record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $inventoryPath -Encoding UTF8
if($Apply){
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='seelen-ui.exe' OR Name='MagicCommunity.exe' OR Name='MagicDesktopGuard.exe'")){
        foreach($target in $targets){if($process.ExecutablePath -and $process.ExecutablePath.StartsWith($target.path+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'A retired build is still running; retained all targets.'}}
    }
    # The launcher no longer falls back to this pointer. Keep it consistent for
    # maintenance scripts while the original is retained in the evidence folder.
    Copy-Item -LiteralPath (Join-Path $root 'app.current.json') -Destination (Join-Path $root 'app.previous.json') -Force
    Add-Type -AssemblyName Microsoft.VisualBasic
    $completed=@()
    foreach($target in $targets){
        # Targets are direct children of the two checked version directories.
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($target.path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
        $completed+=$target.path
        $record.completed=$completed
        $record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    $record.applied=$true
    $record|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $resultPath -Encoding UTF8
}
$totalBytes=0L;foreach($target in $targets){$totalBytes+=[long]$target.bytes}
[pscustomobject]@{applied=$record.applied;count=$targets.Count;MiB=[math]::Round(($totalBytes/1MB),1);activeApp=$app.version;activeHost=$hostSelection.version;destination=$record.destination;dataTouched=$false;inventory=$inventoryPath;result=$resultPath}|ConvertTo-Json
