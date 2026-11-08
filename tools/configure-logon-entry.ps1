param([switch]$CheckOnly)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$startup=[Environment]::GetFolderPath('Startup')
$name=[string]::Concat('Magic ',[char]0x89C6,[char]0x754C,[char]0x793E,[char]0x533A,[char]0x7248,'.lnk')
$path=Join-Path $startup $name
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher=Join-Path $root 'tools/launch-magic.ps1'
$arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$launcher+'" -Mode Desktop -NonInteractive -Logon'
$shell=New-Object -ComObject WScript.Shell
if(Test-Path -LiteralPath $path){
    $old=$shell.CreateShortcut($path)
    if($old.TargetPath -ne $powershell -or -not $old.Arguments.Contains($launcher)){throw 'A different shortcut owns this Startup name; retained.'}
}
if(-not $CheckOnly){
    $shortcut=$shell.CreateShortcut($path)
    $shortcut.TargetPath=$powershell;$shortcut.Arguments=$arguments
    $shortcut.WorkingDirectory=$root;$shortcut.WindowStyle=7
    $shortcut.Description='Start the current verified Magic Community desktop; never pin a version.'
    $shortcut.Save()
}
if(-not(Test-Path -LiteralPath $path)){throw 'Current-user logon shortcut is missing.'}
$verified=$shell.CreateShortcut($path)
if($verified.TargetPath -ne $powershell -or $verified.Arguments -ne $arguments){throw 'Logon shortcut verification failed.'}
$record=@{at=(Get-Date).ToString('o');path=$path;target=$verified.TargetPath;arguments=$verified.Arguments;versionIndependent=$true;scope='Current user Startup folder; normal user token; no service/task changes';rebootAcceptance='pending'}
if(-not $CheckOnly){$record|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'runtime/desktop/logon-entry.json') -Encoding UTF8}
$record|ConvertTo-Json -Depth 4
