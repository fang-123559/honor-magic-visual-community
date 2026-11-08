param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $root
$backup=Get-Content -LiteralPath (Join-Path $root 'local-maintenance/backup-location.json') -Raw -Encoding UTF8|ConvertFrom-Json
$state=Get-Content -LiteralPath (Join-Path $backup.directory 'backup-state.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($state.status -ne 'verified'){throw 'Verified baseline backup is required.'}
$targets=New-Object Collections.Generic.List[string]
foreach($relative in @('experiments/native-host-target','experiments/hicon-conversion/target','experiments/hicon-conversion/target-nightly','experiments/latency/popup-build/upstream/target','experiments/wmi-subscription/target','tools/maintenance/backup-preview65.py','app.current.tmp-a3f4c8dfb3c842ff8911658ef7bb90df')){
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Target escaped workspace.'}
    if(Test-Path -LiteralPath $path){$targets.Add($path)}
}
foreach($pair in @(@('seelen-popups','0.1.0-rc.19'),@('community-ui','0.1.2'),@('power-panel','0.1.2'))){
    $base=Join-Path $root ('profiles/'+$pair[0])
    foreach($dir in @(Get-ChildItem -LiteralPath $base -Directory)){
        if($dir.Name -eq $pair[1] -or $dir.Name -notmatch '^0\.1\.[012](?:-rc\.[0-9]+)?$'){continue}
        if([IO.Path]::GetDirectoryName($dir.FullName) -ne $base){throw 'Profile path differs.'}
        $targets.Add($dir.FullName)
    }
}
$backupParent=[IO.Path]::GetFullPath('F:\magic视界社区版备份')
foreach($name in @('preview65-precleanup-20261019-170622','preview65-baseline-20261019-174215')){
    $path=[IO.Path]::GetFullPath((Join-Path $backupParent $name))
    if([IO.Path]::GetDirectoryName($path) -ne $backupParent -or $path -eq $backup.directory){throw 'Invalid incomplete backup path.'}
    if(Test-Path -LiteralPath $path){$targets.Add($path)}
}
$processes=if($Apply){@(Get-CimInstance Win32_Process)}else{@()}
foreach($path in $targets){
    $driveRoot=[IO.Path]::GetPathRoot($path)
    $drive=New-Object IO.DriveInfo($driveRoot)
    if($Apply -and ($drive.DriveType -ne [IO.DriveType]::Fixed -or -not(Test-Path -LiteralPath (Join-Path $driveRoot '$RECYCLE.BIN') -PathType Container))){throw ('Recycle Bin availability is not established for '+$driveRoot+'; no deletion performed.')}
    $item=Get-Item -LiteralPath $path -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Reparse target rejected: '+$path)}
    if($item.PSIsContainer){
        foreach($entry in Get-ChildItem -LiteralPath $path -Recurse -Force){
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Nested reparse point rejected: '+$entry.FullName)}
        }
    }
    foreach($process in $processes){if($process.ExecutablePath -and ($process.ExecutablePath -eq $path -or $process.ExecutablePath.StartsWith($path+'\',[StringComparison]::OrdinalIgnoreCase))){throw ('Running executable in target: '+$path)}}
}
$out=Join-Path $root 'evidence/maintenance/2026-10-19';[void][IO.Directory]::CreateDirectory($out)
$record=@{at=(Get-Date).ToString('o');backup=$backup.directory;targets=@($targets.ToArray());completed=@();destination='Windows Recycle Bin';userDataTouched=$false;applied=$false}
$report=Join-Path $out 'retired-artifacts.json'
$record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $report -Encoding UTF8
if($Apply){
    Add-Type -AssemblyName Microsoft.VisualBasic
    foreach($path in $targets){
        if(Test-Path -LiteralPath $path -PathType Container){[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}
        else{[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)}
        $record.completed+=@($path)
        $record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $report -Encoding UTF8
    }
    $record.applied=$true
    $record|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $report -Encoding UTF8
}
[pscustomobject]@{applied=$record.applied;count=$record.completed.Count;report=$report;destination=$record.destination}|ConvertTo-Json
