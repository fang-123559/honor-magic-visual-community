param(
    [Parameter(Mandatory=$true)][string]$InputWebm,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Ffmpeg='ffmpeg',
    [ValidateRange(1,2048)][int]$Width=1152,
    [ValidateRange(1,2048)][int]$Height=722
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must stay inside this worktree.'}
$inputFile=(Get-Item -LiteralPath $InputWebm).FullName
if([uint64]$Width*$Height*4 -gt 4*1024*1024){throw 'Decoded frame exceeds the renderer bound.'}
[void][IO.Directory]::CreateDirectory($output)
$sourceHash=(Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash.ToLowerInvariant()
$target=Join-Path $output 'entrance-alpha.frames';$manifestPath=Join-Path $output 'entrance-alpha.manifest.json'
if((Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $manifestPath)){
    try{
        $cached=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
        if($cached.format -eq 'MAGICAF1' -and $cached.sourceSha256 -eq $sourceHash -and $cached.width -eq $Width -and $cached.height -eq $Height -and $cached.frames -eq 150 -and $cached.fps -eq 30 -and $cached.bytes -eq (Get-Item -LiteralPath $target).Length -and $cached.sha256 -eq (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()){$cached|ConvertTo-Json;return}
    }catch{Write-Verbose 'Cached alpha resources are incomplete; rebuilding.'}
}
$frames=Join-Path $root ('artifacts/transition-frames-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($frames)
$filter="scale=${Width}:${Height}:flags=lanczos,fps=30,format=rgba"
& $Ffmpeg -hide_banner -loglevel error -c:v libvpx-vp9 -i $inputFile -vf $filter -compression_level 9 -frames:v 150 (Join-Path $frames 'frame-%03d.png')
if($LASTEXITCODE -ne 0){throw 'Offline alpha decode failed.'}
$files=@(Get-ChildItem -LiteralPath $frames -Filter 'frame-*.png' | Sort-Object Name)
if($files.Count -ne 150){throw 'Expected exactly 150 alpha frames.'}
foreach($file in $files){
    if($file.Length -lt 33 -or $file.Length -gt 4*1024*1024){throw 'PNG frame exceeds renderer bound.'}
    $stream=[IO.File]::OpenRead($file.FullName)
    try{$header=[byte[]]::new(33);if($stream.Read($header,0,33) -ne 33){throw 'Incomplete PNG header.'}}finally{$stream.Dispose()}
    $w=([uint32]$header[16] -shl 24) -bor ([uint32]$header[17] -shl 16) -bor ([uint32]$header[18] -shl 8) -bor $header[19]
    $h=([uint32]$header[20] -shl 24) -bor ([uint32]$header[21] -shl 16) -bor ([uint32]$header[22] -shl 8) -bor $header[23]
    if($header[0] -ne 137 -or $header[1] -ne 80 -or $w -ne $Width -or $h -ne $Height -or $header[24] -ne 8 -or $header[25] -ne 6){throw 'Expected full-size 8-bit RGBA PNG frames.'}
}
$temporary=$target+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
$writer=[IO.BinaryWriter]::new([IO.File]::Create($temporary))
try{
    $writer.Write([Text.Encoding]::ASCII.GetBytes('MAGICAF1'))
    foreach($value in @(150,$Width,$Height,30)){$writer.Write([uint32]$value)}
    [uint64]$offset=24+150*12
    foreach($file in $files){$writer.Write($offset);$writer.Write([uint32]$file.Length);$offset+=$file.Length}
    if($offset -gt 256*1024*1024){throw 'Packed alpha frames exceed renderer file bound.'}
    foreach($file in $files){$writer.Write([IO.File]::ReadAllBytes($file.FullName))}
}finally{$writer.Dispose()}
Move-Item -LiteralPath $temporary -Destination $target -Force
$manifest=@{format='MAGICAF1';frames=150;width=$Width;height=$Height;fps=30;sourceWidth=1152;sourceHeight=722;sourceSha256=$sourceHash;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $target).Length;conversion="FFmpeg libvpx-vp9 -> ${Width}x${Height} RGBA PNG; alpha preserved; 30fps, 150 frames";runtime='Read one PNG at a time; no video codec or full-clip RAM cache'}
$manifest|ConvertTo-Json|Set-Content -LiteralPath $manifestPath -Encoding utf8
$manifest|ConvertTo-Json
