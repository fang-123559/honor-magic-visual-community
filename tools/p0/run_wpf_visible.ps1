$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$executable = Join-Path $projectRoot 'experiments/p0/wpf-visible/bin/Release/net8.0-windows/WpfVisible.exe'
$evidence = Join-Path $projectRoot 'evidence/p0/2026-10-14/wpf-visible'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$runs = @()
$binaryHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
$sourceHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'experiments/p0/wpf-visible/Program.cs') -Algorithm SHA256).Hash
foreach ($itemCount in @(0,100)) {
    foreach ($renderMethod in @('native','software')) {
        $output = Join-Path $evidence ("visible-2host-$renderMethod-$itemCount.json")
        if (Test-Path -LiteralPath $output) { throw "Existing evidence must not be overwritten: $output" }
        $started = (Get-Date).ToString('o')
        Write-Output "Starting 2 monitors, $renderMethod, $itemCount synthetic labels per monitor"
        $process = Start-Process -FilePath $executable -ArgumentList @('2',$renderMethod,('"' + $output + '"'),$itemCount) -WindowStyle Normal -PassThru
        if (-not $process.WaitForExit(180000)) {
            Stop-Process -Id $process.Id -Force
            throw "Own test process timed out: $($process.Id)"
        }
        $runs += [ordered]@{started_at=$started; ended_at=(Get-Date).ToString('o');
            pid=$process.Id; exit_code=$process.ExitCode; output=$output;
            executable_sha256=$binaryHash; source_sha256=$sourceHash}
        $runs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'run-receipts.json') -Encoding UTF8
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $output)) { throw 'Visible WPF test failed' }
        Write-Output "Completed $renderMethod/$itemCount, exit 0"
    }
}
