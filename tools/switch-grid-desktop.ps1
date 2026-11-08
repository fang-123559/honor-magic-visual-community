param([switch]$Rollback)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$newVersion='0.2.0-preview.16';$oldVersion='0.2.0-preview.15';$oldSupportsCollections=$true
$manifestPath=Join-Path $root 'runtime/desktop/grid-upgrade.json'
$utf8=New-Object Text.UTF8Encoding($false)
function Save-Json($path,$value){$temporary=$path+'.tmp-'+[Guid]::NewGuid().ToString('N');try{[IO.File]::WriteAllText($temporary,($value|ConvertTo-Json -Depth 12),$utf8);if(Test-Path -LiteralPath $path){[IO.File]::Replace($temporary,$path,[NullString]::Value)}else{[IO.File]::Move($temporary,$path)}}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}}
function Stop-Adapter {
    $sessionPath=Join-Path $root 'runtime/desktop/session.json'
    if(-not(Test-Path -LiteralPath $sessionPath)){return}
    $session=Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8|ConvertFrom-Json
    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$session.pid)
    if(-not $process){return}
    $expected=Join-Path $root ('versions/'+$session.version+'/MagicCommunity.exe')
    if($process.ExecutablePath -ne $expected){throw 'Adapter process identity changed; no process stopped.'}
    $owned=Get-Process -Id $session.pid;$birth=$owned.StartTime.ToFileTimeUtc()
    $package=Get-AppxPackage -Name 'Seelen.SeelenUI'
    if(-not $package -or $package.Version -ne '2.8.9.0'){throw 'Pinned Seelen package missing.'}
    $metadata=Join-Path $root 'runtime/desktop/widget/metadata.yml'
    if($session.metadata -ne $metadata){throw 'Unexpected widget metadata path.'}
    $unload=Start-Process -FilePath (Join-Path $package.InstallLocation 'slu.exe') -ArgumentList ('resource unload widget "'+$metadata+'"') -WindowStyle Hidden -PassThru
    if(-not $unload.WaitForExit(10000) -or $unload.ExitCode -ne 0){throw 'Desktop widget unload failed; host and popup windows retained.'}
    if((Get-Process -Id $session.pid).StartTime.ToFileTimeUtc() -ne $birth){throw 'Adapter PID changed.'}
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{$key=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
    $pipe=New-Object IO.Pipes.NamedPipeClientStream('.',('MagicCommunity-'+$key),[IO.Pipes.PipeDirection]::Out)
    try{$pipe.Connect(3000);$writer=New-Object IO.StreamWriter($pipe);$writer.AutoFlush=$true;$writer.WriteLine('quit');$writer.Dispose()}finally{$pipe.Dispose()}
    if(-not $owned.WaitForExit(10000)){throw 'Adapter did not exit gracefully; no forced termination performed.'}
}
function Restore-GridBaseline($manifest) {
    $audit=Join-Path $manifest.directory ('collection-audit-'+(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')+'.json')
    & (Join-Path $root 'tools/dotnet/dotnet.exe') (Join-Path $root "versions/$newVersion/MagicCommunity.dll") --root $root --collection-audit --report $audit
    if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath $audit)){throw 'Collection audit unavailable; current application and files retained.'}
    $collection=Get-Content -LiteralPath $audit -Raw -Encoding UTF8|ConvertFrom-Json
    if(($collection.hasStoredFiles -and -not $oldSupportsCollections) -or $collection.problem -or $collection.layoutReadOnly){throw ('File recovery requires review before rollback to '+$oldVersion+'; current files and pointers retained.')}
    Stop-Adapter
    $layout=Join-Path $root 'data/layout.json'
    if(Test-Path -LiteralPath $layout){
        $state=Get-Content -LiteralPath $layout -Raw -Encoding UTF8|ConvertFrom-Json
        if($state.SchemaVersion -ne 3){throw 'Unexpected layout schema; no automatic downgrade.'}
        # Collection-aware versions share layout and operation schemas. Keep current references and journals.
    }
    Copy-Item -LiteralPath (Join-Path $manifest.directory 'app.before.json') -Destination (Join-Path $root 'app.current.json')
    Copy-Item -LiteralPath (Join-Path $manifest.directory 'desktop.before.json') -Destination (Join-Path $root 'desktop.current.json')
    & (Join-Path $PSScriptRoot 'start-desktop.ps1')
    if($LASTEXITCODE -ne 0){throw 'Old desktop start failed; inspect retained snapshots.'}
    $manifest.status='rolled_back_with_latest_references';$manifest.updatedAt=(Get-Date).ToString('o');Save-Json $manifestPath $manifest
}
$current=Get-Content -LiteralPath (Join-Path $root 'app.current.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($Rollback){
    if($current.version -ne $newVersion){throw 'Rollback only applies to the grid preview version.'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
    $directory=[IO.Path]::GetFullPath($manifest.directory)
    if(-not $directory.StartsWith($root+'\evidence\preview\'+$newVersion+'\upgrade-',[StringComparison]::OrdinalIgnoreCase) -or $manifest.from -ne $oldVersion -or $manifest.to -ne $newVersion){throw 'Unexpected upgrade snapshot.'}
    Restore-GridBaseline $manifest;return
}
if($current.version -ne $oldVersion){throw "Expected $oldVersion baseline; no implicit overwrite or repeat upgrade."}
$snapshot=Join-Path $root ('evidence/preview/'+$newVersion+'/upgrade-'+(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $snapshot|Out-Null
foreach($name in @('app.current.json','desktop.current.json')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $snapshot ($name.Replace('.current','.before')))}
if($current.version -ne $oldVersion){
    Copy-Item -LiteralPath (Join-Path $snapshot 'app.before.json') -Destination (Join-Path $snapshot 'app.source.json')
    Copy-Item -LiteralPath (Join-Path $root "evidence/preview/$oldVersion/build-receipt.json") -Destination (Join-Path $snapshot 'app.before.json')
    $baselineDesktop=Get-Content -LiteralPath (Join-Path $snapshot 'desktop.before.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $baselineDesktop.version=$oldVersion;Save-Json (Join-Path $snapshot 'desktop.before.json') $baselineDesktop
}
if(Test-Path -LiteralPath (Join-Path $root 'data/layout.json')){Copy-Item -LiteralPath (Join-Path $root 'data/layout.json') -Destination (Join-Path $snapshot 'layout.before.json')}
$manifest=[pscustomobject]@{from=$oldVersion;to=$newVersion;directory=$snapshot;status='prepared';updatedAt=(Get-Date).ToString('o');manualAcceptance='pending'}
Save-Json $manifestPath $manifest
try{
    Stop-Adapter
    & (Join-Path $PSScriptRoot 'activate-version.ps1') -Version $newVersion
    if($LASTEXITCODE -ne 0){throw 'Version activation failed.'}
    $desktop=Get-Content -LiteralPath (Join-Path $root 'desktop.current.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $desktop.version=$newVersion;$desktop.activationAt=(Get-Date).ToString('o');Save-Json (Join-Path $root 'desktop.current.json') $desktop
    & (Join-Path $PSScriptRoot 'start-desktop.ps1')
    if($LASTEXITCODE -ne 0){throw 'Desktop start failed.'}
    $manifest.status='runtime_ready_manual_pending';$manifest.updatedAt=(Get-Date).ToString('o');Save-Json $manifestPath $manifest
    Write-Output ("Folder accessory ready; fixed shortcut opens "+$newVersion+". Native desktop and icons retained; human input acceptance pending.")
}catch{
    $failure=$_.Exception.Message;$manifest.status='failed';Save-Json $manifestPath $manifest
    try{Restore-GridBaseline $manifest}catch{throw ('Grid upgrade failed: '+$failure+'; rollback requires review: '+$_.Exception.Message)}
    throw ('Grid upgrade failed and old desktop restored with current references: '+$failure)
}
