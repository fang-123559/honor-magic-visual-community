param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $destination.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence destination must be inside this project'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$issues = @()
$packages = @()
try {
    $packages = @(Get-AppxPackage | Where-Object { $_.Name -match 'Seelen' } |
        Select-Object Name,Version,PackageFullName,PackageFamilyName,InstallLocation,Status)
} catch { $issues += "Appx query unavailable: $($_.Exception.Message)" }
$tasks = @()
try {
    $tasks = @(Get-ScheduledTask | Where-Object { $_.TaskPath -eq '\Seelen\' -or $_.TaskName -eq 'Seelen-UI' })
    foreach ($task in $tasks) {
        $safeName = ($task.TaskPath + $task.TaskName) -replace '[^a-zA-Z0-9._-]', '_'
        Export-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName |
            Set-Content -LiteralPath (Join-Path $destination ($safeName + '.xml')) -Encoding UTF8
    }
} catch { $issues += "Task query/export unavailable: $($_.Exception.Message)" }
$candidateDirectories = @(
    (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'com.seelen.seelen-ui'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'com.seelen.seelen-ui'),
    (Join-Path ([System.IO.Path]::GetTempPath()) 'com.seelen.seelen-ui')
)
$containersRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Packages'
if (Test-Path -LiteralPath $containersRoot) {
    $candidateDirectories += @(Get-ChildItem -LiteralPath $containersRoot -Directory -Filter '*Seelen*' | ForEach-Object FullName)
}
$directories = @()
foreach ($candidate in ($candidateDirectories | Select-Object -Unique)) {
    $exists = Test-Path -LiteralPath $candidate
    $files = @()
    if ($exists) {
        foreach ($file in (Get-ChildItem -LiteralPath $candidate -File -Recurse -Force)) {
            $files += [ordered]@{relative_path=$file.FullName.Substring($candidate.Length).TrimStart('\');
                bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
        }
    }
    $directories += [ordered]@{path=$candidate; existed=$exists; files=$files}
}
$relatedProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(seelen-ui|slu-service|slu-console)\.exe$' } |
    Select-Object Name,ProcessId,ParentProcessId,ExecutablePath)
$state = [ordered]@{
    captured_at=(Get-Date).ToString('o'); user=$env:USERNAME;
    installed_packages=$packages;
    tasks=@($tasks | Select-Object TaskPath,TaskName,State);
    directories=$directories; processes=$relatedProcesses;
    user_path=[Environment]::GetEnvironmentVariable('Path','User');
    issues=$issues;
    note='Read-only pre/post snapshot. Existing files are hashed, not deleted. This snapshot is not a backup of their contents.'
}
$state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'system-state.json') -Encoding UTF8
[pscustomobject]$state | Select-Object captured_at,installed_packages,tasks,directories,processes,issues | ConvertTo-Json -Depth 6
