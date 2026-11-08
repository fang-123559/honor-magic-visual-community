$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktop=[Environment]::GetFolderPath('DesktopDirectory')
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$shell=New-Object -ComObject WScript.Shell
$receipts=@()
foreach ($definition in @(@{name='Magic 视界社区版.lnk';mode='App';description='固定桌面入口，版本更新时由项目清单切换'},@{name='Magic 视界社区版 - 项目目录.lnk';mode='Project';description='打开源码、日志、反馈与调试工具'},@{name='Magic 视界社区版 - 设置与调试.lnk';mode='Workbench';description='打开设置与调试窗口，桌面模式继续运行'},@{name='Magic 视界社区版 - 退出桌面.lnk';mode='Exit';description='退出本次桌面，保留布局并返回 Windows'})) {
    $path=Join-Path $desktop $definition.name
    if (Test-Path -LiteralPath $path) {
        $existing=$shell.CreateShortcut($path)
        if ($existing.TargetPath -ne $powershell -or -not $existing.Arguments.Contains((Join-Path $projectRoot 'tools/launch-magic.ps1'))) { throw "存在同名其他快捷方式，已保留：$path" }
    }
    $shortcut=$shell.CreateShortcut($path)
    $shortcut.TargetPath=$powershell
    $shortcut.Arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $projectRoot 'tools/launch-magic.ps1')+'" -Mode '+$definition.mode
    $shortcut.WorkingDirectory=$projectRoot;$shortcut.Description=$definition.description
    $shortcut.WindowStyle=7
    $shortcut.IconLocation=Join-Path $env:SystemRoot 'System32/shell32.dll'
    $shortcut.Save()
    $reopened=$shell.CreateShortcut($path)
    if ($reopened.TargetPath -ne $powershell -or $reopened.Arguments -ne $shortcut.Arguments) { throw '保存后的快捷方式校验失败。' }
    $receipts+=[ordered]@{path=$path;target=$reopened.TargetPath;arguments=$reopened.Arguments;versionIndependent=$true}
}
$receipts | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $projectRoot 'evidence/preview/shortcuts.json') -Encoding UTF8
$receipts | ConvertTo-Json -Depth 4
