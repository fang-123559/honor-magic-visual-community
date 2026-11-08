param([Parameter(Mandatory=$true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?$')][string]$Version,[switch]$ManualAcceptance)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $projectRoot
. (Join-Path $PSScriptRoot 'file-hash.ps1')
$evidence=Join-Path $projectRoot "evidence/preview/$Version"
$receiptPath=Join-Path $evidence 'build-receipt.json'
$pointer=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($pointer.schemaVersion -ne 1 -or $pointer.version -ne $Version -or $pointer.entry -ne "versions/$Version/MagicCommunity.dll"){throw 'Version receipt identity differs.'}
if($ManualAcceptance){
    if($pointer.acceptance -ne 'build_only_manual_pending' -or $pointer.testsRun -ne $false -or $pointer.sourceCommit -notmatch '^[0-9a-fA-F]{40}$'){throw 'Explicit manual acceptance requires a frozen BuildOnly receipt.'}
}else{
$test=Get-Content -LiteralPath (Join-Path $evidence 'self-test.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$smoke=Get-Content -LiteralPath (Join-Path $evidence 'window-smoke.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($pointer.schemaVersion -ne 1 -or $pointer.version -ne $Version -or $pointer.entry -ne "versions/$Version/MagicCommunity.dll" -or $test.version -ne $Version -or $smoke.version -ne $Version -or
    -not $test.allPass -or $smoke.error -or -not $smoke.mainWindowVisible -or @($smoke.screenWindows).Count -eq 0 -or @($smoke.screenWindows | Where-Object {-not $_.visible}).Count -gt 0) { throw '版本或自动检查回执无效，未切换入口。' }
}
foreach ($file in $pointer.files) {
    $path=[IO.Path]::GetFullPath((Join-Path $projectRoot $file.path))
    if (-not $file.path.StartsWith("versions/$Version/",[StringComparison]::Ordinal) -or -not $path.StartsWith($projectRoot+"\versions\$Version\",[StringComparison]::OrdinalIgnoreCase)) { throw '版本文件路径无效。' }
    if ((Get-MagicSHA256 $path) -ne $file.sha256) { throw '自动检查后的版本文件发生变化，未切换入口。' }
}
if ((Get-MagicSHA256 (Join-Path $projectRoot 'tools/dotnet/dotnet.exe')) -ne $pointer.runtimeHostSha256) { throw '运行入口校验失败。' }
if($ManualAcceptance){
    $invocation=Join-Path $evidence ('manual-activation-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N')+'.json')
    @{version=$Version;sourceCommit=$pointer.sourceCommit;buildReceiptSha256=(Get-MagicSHA256 $receiptPath);at=(Get-Date -Format o);mode='explicit_user_manual_acceptance';testsRun=$false;manualAcceptance='pending'}|ConvertTo-Json|Set-Content -LiteralPath $invocation -Encoding UTF8
}
$current=Join-Path $projectRoot 'app.current.json'
$temp=Join-Path $projectRoot ('app.current.tmp-'+[Guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllText($temp,($pointer | ConvertTo-Json -Depth 8),(New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $current) { [IO.File]::Replace($temp,$current,(Join-Path $projectRoot 'app.previous.json')) }
    else { [IO.File]::Move($temp,$current) }
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
& (Join-Path $PSScriptRoot 'launch-magic.ps1') -CheckOnly
if ($LASTEXITCODE -ne 0) { throw '固定入口校验失败，先检查 app.previous.json。' }
