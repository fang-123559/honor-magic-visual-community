param([string]$OutputDirectory=('artifacts/encodeck-integration-'+(Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'));$out=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(-not $out.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Owned fixtures must remain in worktree artifacts.'}
if(Test-Path -LiteralPath $out){throw 'Use fresh fixture output to preserve prior evidence.'}
[void][IO.Directory]::CreateDirectory($out)
$env:DOTNET_ROOT=Join-Path $root 'tools/dotnet';$env:DOTNET_CLI_HOME=Join-Path $root 'tools/dotnet-home';$env:NUGET_PACKAGES=Join-Path $root 'tools/nuget-packages';$dotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet publish (Join-Path $PSScriptRoot 'encodeck-integration-fixture') -o (Join-Path $out 'fixture') --nologo *> (Join-Path $out 'fixture-build.log')
if($LASTEXITCODE -ne 0){throw ('Owned EncoDeck fixture build failed: '+$out)}
& $dotnet run --project (Join-Path $PSScriptRoot 'encodeck-integration-check') -- $out *> (Join-Path $out 'check.log')
if($LASTEXITCODE -ne 0){throw ('EncoDeck integration check failed: '+$out)}
$result=Get-Content (Join-Path $out 'result.json') -Raw|ConvertFrom-Json
if(-not $result.allPass){throw 'EncoDeck integration check reported failure.'}
$result|Select-Object allPass,count,scope|ConvertTo-Json
