param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')
$common=& git -C $root rev-parse --path-format=absolute --git-common-dir
if($LASTEXITCODE -ne 0){throw 'This folder is not a Git checkout.'}
$primary=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($common.Trim()))
$branch=& git -C $root branch --show-current
if($LASTEXITCODE -ne 0){throw 'Unable to read branch.'}
$pairs=@(
    @('tools/dotnet','tools/dotnet'),
    @('experiments/p0/seelen-widget/node_modules','experiments/p0/seelen-widget/node_modules'),
    @('experiments/native-host-patch/node_modules','experiments/native-ui-deps/node_modules')
)
$linked=@()
foreach($pair in $pairs){
    $local=[IO.Path]::GetFullPath((Join-Path $root $pair[0]));$dependency=[IO.Path]::GetFullPath((Join-Path $primary $pair[1]))
    if(-not $local.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $dependency.StartsWith($primary+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Dependency path escaped its checkout.'}
    if(-not(Test-Path -LiteralPath $dependency -PathType Container)){throw ('Missing primary dependency: '+$dependency)}
    if($root -eq $primary){$linked+=@{path=$local;target=$dependency;kind='primary-existing'};continue}
    if(Test-Path -LiteralPath $local){
        $item=Get-Item -LiteralPath $local -Force
        if(-not($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetFullPath([string]$item.Target) -ne $dependency){throw ('Existing dependency is not the expected junction: '+$local)}
    }else{
        [void][IO.Directory]::CreateDirectory((Split-Path $local))
        New-Item -ItemType Junction -Path $local -Target $dependency -ErrorAction Stop|Out-Null
    }
    $linked+=@{path=$local;target=$dependency;kind='shared-read-only-by-policy'}
}
$artifact=Join-Path $root 'artifacts';[void][IO.Directory]::CreateDirectory($artifact)
$result=@{root=$root;primary=$primary;branch=$branch.Trim();dependencies=$linked;isolation='Only locked dependencies shared; source, dist, obj, artifacts and fixtures are separate. No desktop config or user data links.'}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $artifact 'setup-worktree.json') -Encoding UTF8
$result|ConvertTo-Json -Depth 5
