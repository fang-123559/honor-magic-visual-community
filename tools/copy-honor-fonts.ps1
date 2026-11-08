param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Font output must stay within this worktree.'
}
$source = Join-Path $root 'assets/honor-fonts'
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
$target = Join-Path $output 'fonts'
[void][IO.Directory]::CreateDirectory($target)
foreach ($entry in $manifest.files) {
    if ($entry.name -ne [IO.Path]::GetFileName($entry.name)) { throw 'Invalid font manifest path.' }
    $file = Join-Path $source $entry.name
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw ('Font asset hash differs: ' + $entry.name)
    }
    Copy-Item -LiteralPath $file -Destination (Join-Path $target $entry.name)
}
foreach ($name in @('manifest.json', 'NOTICE.md', 'fonts.css')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name)
}
