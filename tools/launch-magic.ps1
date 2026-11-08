param([ValidateSet('App','Desktop','Workbench','Broker','Exit','Project')][string]$Mode='App',[switch]$CheckOnly,[switch]$NonInteractive,[switch]$PassThru,[switch]$SystemValidation,[switch]$Logon)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $projectRoot
function Get-SHA256([string]$path) {
    # Avoid module auto-loading differences when Windows PowerShell is launched from PS 7.
    $stream=[IO.File]::OpenRead($path);$algorithm=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose();$algorithm.Dispose() }
}
function Resolve-ProjectFile([string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) { throw '入口文件路径必须相对于项目。' }
    $path=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
    if (-not $path.StartsWith($projectRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '入口文件超出项目目录。' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "找不到入口文件：$relative" }
    $entry=Get-Item -LiteralPath $path
    while ($entry.FullName -ne $projectRoot) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '入口路径含重解析点。' }
        if ($entry -is [IO.FileInfo]) { $entry=$entry.Directory } else { $entry=$entry.Parent }
        if ($null -eq $entry) { throw '入口路径无法验证。' }
    }
    return $path
}
function Read-Pointer([string]$name) {
    $pointer=Get-Content -LiteralPath (Join-Path $projectRoot $name) -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($pointer.schemaVersion -ne 1 -or $pointer.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.]+)?$') { throw '应用入口清单格式无效。' }
    if ($pointer.entry -ne "versions/$($pointer.version)/MagicCommunity.dll") { throw '应用入口不是约定的版本目录。' }
    $files=@($pointer.files)
    $appHostRelative=[IO.Path]::ChangeExtension($pointer.entry,'.exe').Replace('\','/')
    if ($files.Count -lt 4 -or -not ($files.path -contains $pointer.entry) -or -not ($files.path -contains $appHostRelative)) { throw '应用入口缺少文件校验表。' }
    foreach ($file in $files) {
        if (-not $file.path.StartsWith("versions/$($pointer.version)/",[StringComparison]::Ordinal) -or $file.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw '应用校验记录无效。' }
        $path=Resolve-ProjectFile $file.path
        if ((Get-SHA256 $path) -ne $file.sha256) { throw "应用文件校验失败：$($file.path)" }
    }
    $hostPath=Resolve-ProjectFile 'tools/dotnet/dotnet.exe'
    if ($pointer.runtimeHostSha256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-SHA256 $hostPath) -ne $pointer.runtimeHostSha256) { throw '项目内 .NET 运行入口校验失败。' }
    return [pscustomobject]@{pointer=$pointer;entry=(Resolve-ProjectFile $pointer.entry);appHost=(Resolve-ProjectFile $appHostRelative);hostPath=$hostPath;pointerFile=$name}
}
try {
    if ($Mode -eq 'Exit') { & (Join-Path $PSScriptRoot 'stop-desktop.ps1'); exit $LASTEXITCODE }
    if ($Mode -eq 'Project') {
        if ($CheckOnly) { [pscustomobject]@{mode='Project';project=$projectRoot} | ConvertTo-Json; exit 0 }
        Start-Process -FilePath 'explorer.exe' -ArgumentList ('"'+$projectRoot+'"')
        exit 0
    }
    $selected=$null;$errors=@()
    # A failed current build must not silently launch a retired application.
    foreach ($name in @('app.current.json')) {
        try { $selected=Read-Pointer $name; break } catch { $errors+=($name+': '+$_.Exception.Message) }
    }
    if ($null -eq $selected) { throw ($errors -join [Environment]::NewLine) }
    if ($CheckOnly) {
        [pscustomobject]@{mode='App';version=$selected.pointer.version;entry=$selected.entry;appHost=$selected.appHost;hostPath=$selected.hostPath;pointerFile=$selected.pointerFile;verified=$true} | ConvertTo-Json
        exit 0
    }
    $desktopConfiguration=Join-Path $projectRoot 'desktop.current.json'
    $enterDesktop=$Mode -eq 'Desktop' -or ($Mode -eq 'App' -and (Test-Path -LiteralPath $desktopConfiguration) -and (Get-Content -LiteralPath $desktopConfiguration -Raw -Encoding UTF8 | ConvertFrom-Json).enabled)
    if ($enterDesktop) {
        if (-not $selected.pointer.desktopBridge) { throw '当前回退版本仅包含设置窗口，请使用打开设置与调试入口。' }
        & (Join-Path $PSScriptRoot 'start-desktop.ps1') -SystemValidation:$SystemValidation -Logon:$Logon
        if ($LASTEXITCODE -ne 0) { throw '桌面启动未通过，请检查 logs/desktop-launch.jsonl。' }
        exit 0
    }
    $env:DOTNET_ROOT=Join-Path $projectRoot 'tools/dotnet'
    $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_NOLOGO='1'
    $arguments='--root "'+$projectRoot+'"'+$(if($Mode -eq 'Broker'){' --desktop'}else{' --workbench'})
    # Start the WinExe apphost normally: no console window and no hidden startup state inherited by WPF.
    $process=Start-Process -FilePath $selected.appHost -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Normal -PassThru
    $logDirectory=Join-Path $projectRoot 'logs';New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    [ordered]@{at=(Get-Date).ToString('o');version=$selected.pointer.version;pid=$process.Id;pointer=$selected.pointerFile;status='process_started_not_user_accepted'} | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $logDirectory 'launcher.jsonl') -Encoding UTF8
    if($PassThru){Write-Output $process}
    exit 0
} catch {
    if ($CheckOnly) { Write-Error $_ -ErrorAction Continue; exit 1 }
    $failure=$_.Exception.Message
    try {
        $logDirectory=Join-Path $projectRoot 'logs';[void][IO.Directory]::CreateDirectory($logDirectory)
        @{at=(Get-Date).ToString('o');mode=$Mode;status='failed';reason=$failure;nonInteractive=[bool]$NonInteractive}|ConvertTo-Json -Compress|Add-Content -LiteralPath (Join-Path $logDirectory 'launcher.jsonl') -Encoding UTF8
    } catch {}
    if ($NonInteractive) { Write-Error $failure -ErrorAction Continue; exit 1 }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("应用入口无法启动。项目目录将打开，便于调试。`n"+$_.Exception.Message,'Magic 视界社区版') | Out-Null
    Start-Process -FilePath 'explorer.exe' -ArgumentList ('"'+$projectRoot+'"')
    exit 1
}
