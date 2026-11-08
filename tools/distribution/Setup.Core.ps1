# Windows PowerShell 5.1 and PowerShell 7. No action runs on import.
$ErrorActionPreference='Stop'
$script:MagicInstallProtocol='Magic.Install/1'
. (Join-Path $PSScriptRoot 'Setup.Shortcut.ps1')
function Get-MagicSetupRegistrationPath { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MagicCommunity\Deployment\installation.json' }
function Read-MagicSetupJson([string]$Path,[int]$Maximum=65536) {
    $file=Get-Item -LiteralPath $Path
    if($file.Length -gt $Maximum -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '配置文件过大或含链接。'}
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Write-MagicSetupJson([string]$Path,$Value) {
    [void][IO.Directory]::CreateDirectory((Split-Path $Path))
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temp,($Value|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temp,$Path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
}
function Get-MagicSetupSid { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
function Get-MagicInstalledPackage {
    $packages=@(Get-AppxPackage -Name MagicCommunity.Native -ErrorAction Stop)
    if($packages.Count -gt 1){throw '发现多份安装记录，请先在 Windows 应用设置中检查。'}
    if($packages.Count){return $packages[0]}
}
function Get-MagicSetupRecord {
    $path=Get-MagicSetupRegistrationPath
    if(-not(Test-Path -LiteralPath $path)){return $null}
    $value=Read-MagicSetupJson $path
    if($value.protocol -cne $script:MagicInstallProtocol -or $value.ownerSid -cne (Get-MagicSetupSid) -or $value.installId -cnotmatch '^[a-f0-9]{32}$' -or $value.packageFamily -cnotmatch '^MagicCommunity\.Native_[a-z0-9]+$' -or $value.state -cnotin @('installed','uninstalled')){throw '已有安装记录无法验证，未覆盖。'}
    return $value
}
function Assert-MagicSetupPath([string]$Path,[switch]$ProbeWrite) {
    if([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:\\' -or $Path.StartsWith('\\') -or $Path.Contains('"')){throw '请选择本机磁盘上的完整数据路径。'}
    $resolved=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    if($resolved.Length -lt 4 -or $resolved.Length -gt 200 -or $resolved.Substring(3).Contains(':')){throw '不能使用磁盘根目录或过长路径。'}
    foreach($part in $Path.Substring(3).TrimEnd('\').Split('\')){
        if(-not $part -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$' -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0){throw '路径包含 Windows 不支持的名称、字符或末尾空格。'}
    }
    foreach($special in @('Windows','ProgramFiles','ProgramFilesX86')){
        $protected=[Environment]::GetFolderPath($special)
        if($protected -and ($resolved.Equals($protected,[StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($protected.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))){throw '不能将数据放在 Windows 或程序安装目录中。'}
    }
    $local=[Environment]::GetFolderPath('LocalApplicationData')
    $default=Join-Path $local 'MagicCommunity\UserData'
    if($resolved.StartsWith($local.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -and -not $resolved.Equals($default,[StringComparison]::OrdinalIgnoreCase)){
        throw '在 AppData 内请使用默认数据目录；自选目录可放在文档或其它本地磁盘。'
    }
    foreach($special in @('UserProfile','DesktopDirectory','MyDocuments','LocalApplicationData','ApplicationData')){
        $protected=[Environment]::GetFolderPath($special)
        if($protected -and $resolved.Equals($protected,[StringComparison]::OrdinalIgnoreCase)){throw '请选择专用于社区版的子文件夹，不能直接使用桌面、文档或用户根目录。'}
    }
    $ancestor=$resolved;$existing=$null
    while($ancestor){
        if(Test-Path -LiteralPath $ancestor){
            $item=Get-Item -LiteralPath $ancestor -Force
            if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '数据路径包含文件、符号链接或重解析目录。'}
            if(Test-Path -LiteralPath (Join-Path $ancestor '.git')){throw '不能将源码工作树作为用户数据目录。'}
            if(-not $existing){$existing=$ancestor}
        }
        $ancestor=[IO.Path]::GetDirectoryName($ancestor)
    }
    if(-not $existing){throw '所选磁盘不可用。'}
    if(Test-Path -LiteralPath $resolved){
        $marker=Join-Path $resolved '.magic-data.json'
        if(Test-Path -LiteralPath $marker){
            $owned=Read-MagicSetupJson $marker
            if($owned.protocol -cne $script:MagicInstallProtocol -or $owned.ownerSid -cne (Get-MagicSetupSid) -or $owned.installId -cnotmatch '^[a-f0-9]{32}$' -or [IO.Path]::GetFullPath($owned.dataRoot).TrimEnd('\') -ine $resolved){throw '已有数据目录不属于当前用户，未接管。'}
        }elseif(@(Get-ChildItem -LiteralPath $resolved -Force | Select-Object -First 1).Count){throw '目录不是空目录；请选择新的子文件夹，避免混入其它文件。'}
    }
    if($ProbeWrite){
        $probe=Join-Path $existing ('.magic-write-probe-'+[guid]::NewGuid().ToString('N'))
        try{$stream=[IO.FileStream]::new($probe,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,1,[IO.FileOptions]::DeleteOnClose);$stream.Dispose()}
        catch{throw '当前账户无法写入所选位置，请换一个数据目录。'}
    }
    return $resolved
}
function Assert-MagicSetupOwnedTree($Record) {
    $root=Assert-MagicSetupPath $Record.dataRoot
    $marker=Read-MagicSetupJson (Join-Path $root '.magic-data.json')
    if($marker.installId -cne $Record.installId){throw '数据目录安装标识不匹配。'}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($root);$count=0
    while($pending.Count){
        foreach($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force){
            if(++$count -gt 200000){throw '数据目录过大，保留数据并由用户手动整理。'}
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '数据目录内含链接，无法安全整体移至回收站。'}
            if($entry.PSIsContainer){$pending.Push($entry.FullName)}
        }
    }
    # Collected desktop files live outside the data root. Keep their recovery journal.
    $collections=Join-Path $root 'data\collection-operations'
    if(Test-Path -LiteralPath $collections){
        foreach($entry in Get-ChildItem -LiteralPath $collections -File -Filter '*.json'){
            $operation=Read-MagicSetupJson $entry.FullName 1048576
            foreach($file in @($operation.files)){if($file.stored -and (Test-Path -LiteralPath $file.stored)){throw '仍有实际文件保存在“Magic 视界收纳”中。请先在桌面中移出文件；本次应保留数据和恢复记录。'}}
        }
    }
    return $root
}
function Get-MagicLegacyStartup($Package,$Record) {
    $legacyRoot=if($Record -and $Record.legacyRoot){[string]$Record.legacyRoot}else{$null}
    if(-not $legacyRoot -and $Package){
        $launchPath=Join-Path $Package.InstallLocation 'native-launch.json'
        if(Test-Path -LiteralPath $launchPath){
            $launch=Read-MagicSetupJson $launchPath 8192
            if($launch.schemaVersion -eq 1){$legacyRoot=[string]$launch.root}
        }
    }
    if(-not $legacyRoot){return $null}
    if($legacyRoot -notmatch '^[A-Za-z]:\\' -or $legacyRoot.Contains('"')){throw '旧安装目录记录无效。'}
    $legacyRoot=[IO.Path]::GetFullPath($legacyRoot).TrimEnd('\')
    $task=Get-ScheduledTask -TaskName 'Magic Community Desktop' -TaskPath '\' -ErrorAction SilentlyContinue
    if(-not $task){return $null}
    $shell=Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $legacyRoot 'tools\launch-magic.ps1')+'" -Mode Desktop -NonInteractive'
    # Task Scheduler exports the canonical SID even when the CIM view returns
    # a bare local name that LSA cannot translate. Do not guess a machine/domain.
    $taskXml=Export-ScheduledTask -TaskName 'Magic Community Desktop' -TaskPath '\'
    [xml]$identityXml=$taskXml
    $ns=[Xml.XmlNamespaceManager]::new($identityXml.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $principals=$identityXml.SelectNodes('/t:Task/t:Principals/t:Principal',$ns)
    $users=$identityXml.SelectNodes('/t:Task/t:Principals/t:Principal/t:UserId',$ns)
    if($principals.Count -ne 1 -or $users.Count -ne 1 -or $identityXml.SelectNodes('/t:Task/t:Principals/t:Principal/t:GroupId',$ns).Count -ne 0 -or $users[0].InnerText -notmatch '^S-1-'){throw '旧开机任务必须包含唯一的规范用户 SID，未迁移。'}
    $sid=([Security.Principal.SecurityIdentifier]::new($users[0].InnerText)).Value
    if([string]$task.Principal.UserId -match '^S-1-' -and [string]$task.Principal.UserId -cne $sid){throw '旧开机任务身份在读取时不一致，未迁移。'}
    if($sid -cne (Get-MagicSetupSid) -or $task.Description -cne ('Managed Magic Community entry: '+$legacyRoot) -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine $shell -or $task.Actions[0].Arguments -cnotin @($arguments,($arguments+' -Logon')) -or $task.Actions[0].WorkingDirectory -ine $legacyRoot -or [string]$task.Principal.RunLevel -ne 'Limited' -or [string]$task.Principal.LogonType -ne 'Interactive'){throw '同名开机任务不符合当前用户的社区版入口，未更改。'}
    return [pscustomobject]@{root=$legacyRoot;xml=$taskXml}
}
function Remove-MagicLegacyStartup($Plan,$Record) {
    if(-not $Plan.legacy){return}
    $now=Get-MagicLegacyStartup $Plan.package $Record
    if(-not $now){return}
    if($now.xml -cne $Plan.legacy.xml){throw '旧开机任务在确认后发生变化，未移除。'}
    $backup=Join-Path $Record.dataRoot ('data\installation-backups\legacy-startup-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')+'.xml')
    [void][IO.Directory]::CreateDirectory((Split-Path $backup));[IO.File]::WriteAllText($backup,$now.xml,[Text.UTF8Encoding]::new($false))
    Unregister-ScheduledTask -TaskName 'Magic Community Desktop' -TaskPath '\' -Confirm:$false -ErrorAction Stop
    if(Get-ScheduledTask -TaskName 'Magic Community Desktop' -TaskPath '\' -ErrorAction SilentlyContinue){throw '旧开机任务仍然存在，请核对权限后重试。'}
}
function Select-MagicSetupBundle([string]$Directory,$Package,$Record) {
    $base=Read-MagicSetupJson (Join-Path $Directory 'setup-bundle.json')
    $updateDirectory=Join-Path $Directory 'update'
    if(Test-Path -LiteralPath (Join-Path $updateDirectory 'setup-bundle.json')){
        $update=Read-MagicSetupJson (Join-Path $updateDirectory 'setup-bundle.json')
        if($update.protocol -cne $base.protocol -or $update.packageName -cne $base.packageName -or $update.packageFamily -cne $base.packageFamily -or $update.signingMode -cne $base.signingMode -or [version]$update.version -le [version]$base.version){throw '更新验收包与首次安装包不匹配。'}
        $useUpdate=($Package -and [version]$Package.Version -ge [version]$base.version) -or (-not $Package -and $Record -and $Record.packageVersion -and [version]$Record.packageVersion -ge [version]$update.version)
        if($useUpdate){return [pscustomobject]@{directory=$updateDirectory;version=[version]$update.version}}
    }
    return [pscustomobject]@{directory=$Directory;version=[version]$base.version}
}
function Get-MagicSetupPlan([ValidateSet('Install','Configure','Uninstall')][string]$Action,[string]$DataRoot,[string]$BundleDirectory) {
    $record=Get-MagicSetupRecord;$package=Get-MagicInstalledPackage
    $recoverRetained=$false
    if($record -and $package -and $record.packageFamily -cne $package.PackageFamilyName){
        # Windows may already have deployed the new family before configuration stopped.
        if($Action -eq 'Configure' -and $record.state -ceq 'uninstalled' -and $BundleDirectory){
            $incoming=Read-MagicSetupJson (Join-Path $BundleDirectory 'setup-bundle.json')
            $recoverRetained=$incoming.protocol -ceq $script:MagicInstallProtocol -and $incoming.packageName -ceq 'MagicCommunity.Native' -and $incoming.packageFamily -cmatch '^MagicCommunity\.Native_[a-z0-9]{13}$' -and $incoming.packageFamily -ceq $package.PackageFamilyName
        }
        if(-not $recoverRetained){throw '当前安装身份与数据记录不一致。'}
    }
    if($Action -eq 'Uninstall'){
        if(-not $package){throw '当前用户没有安装社区版。'}
        if(-not $record -or $record.state -ne 'installed'){throw '这是旧的开发验收安装，请先完成新版安装配置；本向导不会删除开发仓库或未知的任务。'}
        return [pscustomobject]@{action=$Action;package=$package;record=$record;dataRoot=$record.dataRoot;update=$false;legacy=(Get-MagicLegacyStartup $package $record)}
    }
    if($record -and ($record.state -eq 'installed' -or $recoverRetained)){
        $DataRoot=$record.dataRoot
    }elseif(-not $DataRoot){$DataRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MagicCommunity\UserData'}
    $DataRoot=Assert-MagicSetupPath $DataRoot -ProbeWrite
    if($record -and ($record.state -eq 'installed' -or $recoverRetained)){
        $marker=Read-MagicSetupJson (Join-Path $DataRoot '.magic-data.json')
        if($marker.installId -cne $record.installId){throw '安装记录与数据目录标识不匹配，未覆盖。'}
    }
    $bundle=$null;$msix=$null
    if($Action -eq 'Install'){
        $bundle=Read-MagicSetupJson (Join-Path $BundleDirectory 'setup-bundle.json')
        if($bundle.protocol -cne $script:MagicInstallProtocol -or $bundle.packageName -cne 'MagicCommunity.Native' -or $bundle.packageFile -cnotmatch '^[A-Za-z0-9._-]+\.msix$' -or $bundle.sha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or $bundle.dataRootMode -cne 'per-user'){throw '安装包描述无效或仍绑定开发仓库。'}
        if($null -ne $bundle.packageFamily){
            if($bundle.packageFamily -cnotmatch '^MagicCommunity\.Native_[a-z0-9]{13}$'){throw '安装包的发布者身份无效。'}
            if($package -and $package.PackageFamilyName -cne $bundle.packageFamily){
                throw '当前安装来自另一发布者。请先用旧版向导卸载并保留数据，再用新版安装并选回原数据目录。'
            }
        }
        $msix=Join-Path $BundleDirectory $bundle.packageFile
        if((Get-FileHash -LiteralPath $msix).Hash -ine $bundle.sha256){throw '安装包校验失败，请重新取得完整安装文件。'}
        if($bundle.signingMode -ceq 'public-ca'){
            $signature=Get-AuthenticodeSignature -LiteralPath $msix
            if($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate){throw 'Windows 未能验证此公开安装包的签名或时间戳。请重新从项目发布页下载安装包。'}
        }
        if($package -and [version]$bundle.version -le [version]$package.Version){throw '当前版本不低于安装包版本；更新必须使用更高版本，不执行降级或覆盖重注册。'}
    }elseif(-not $package){throw '请先通过本向导安装社区版。'}
    return [pscustomobject]@{action=$Action;package=$package;record=$record;dataRoot=$DataRoot;legacy=(Get-MagicLegacyStartup $package $record);update=($null -ne $record -and $record.state -eq 'installed');bundle=$bundle;msix=$msix}
}
function Get-MagicOtherSessionOwnerSid($Process) {
    try{
        $row=Get-CimInstance Win32_Process -Filter ('ProcessId='+$Process.Id) -ErrorAction Stop
        if(-not $row -or $row.SessionId -ne $Process.SessionId -or $row.Name -ine ($Process.ProcessName+'.exe')){return}
        $owner=Invoke-CimMethod -InputObject $row -MethodName GetOwnerSid -ErrorAction Stop
        if($owner.ReturnValue -eq 0 -and $owner.Sid -match '^S-1-'){return $owner.Sid}
    }catch{}
}
function Get-MagicSetupProcesses($Package) {
    $code=if($Package){[IO.Path]::GetFullPath($Package.InstallLocation).TrimEnd('\')+'\'}else{$null}
    $names=@('MagicCommunity','MagicDesktopComposition','MagicDesktopGuard','MagicNativeEntry','MagicNotificationPermission','MagicSystemNative','MagicTaskbarGuard','MagicWindowAction')
    foreach($process in Get-Process -ErrorAction Stop){
        $path=$null;$session=-1
        try{if($process.Path){$path=[IO.Path]::GetFullPath($process.Path)}}catch{}
        $fromPackage=$path -and $code -and $path.StartsWith($code,[StringComparison]::OrdinalIgnoreCase)
        if(-not $fromPackage -and $process.ProcessName -notin $names){continue}
        # An unreadable/elevated process is not evidence that the old desktop exited.
        try{$session=$process.SessionId}catch{}
        if($session -ge 0 -and $session -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){
            # AppX registration is per user. Leave an identified other user's session to Windows.
            $ownerSid=Get-MagicOtherSessionOwnerSid $process
            if($ownerSid -match '^S-1-(5-21|12-1)-' -and $ownerSid -ne (Get-MagicSetupSid)){continue}
        }
        [pscustomobject]@{Id=$process.Id;Name=$process.ProcessName;Path=$path;SessionId=$session;FromPackage=[bool]$fromPackage;Process=$process}
    }
}
function Assert-MagicSetupProcessScope($Processes) {
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach($process in $Processes){
        if(-not $process.FromPackage -or $process.SessionId -ne $session){
            throw ('发现其它版本、会话或无法核验路径的社区版组件（'+$process.Name+'，PID '+$process.Id+'）。请先正常退出相关桌面，未结束进程或继续安装。')
        }
    }
}
function Request-MagicSetupDesktopExit($Record) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{$key=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Record.dataRoot.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',('MagicCommunity-'+$key),[IO.Pipes.PipeDirection]::Out)
    try{$pipe.Connect(3000);$writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true;$writer.WriteLine('quit');$writer.Dispose()}finally{$pipe.Dispose()}
}
function Stop-MagicSetupDesktop($Package,$Record) {
    # Check the whole desktop, even when its main process (or AppX record) is gone.
    $running=@(Get-MagicSetupProcesses $Package)
    if(-not $running.Count){return}
    Assert-MagicSetupProcessScope $running
    $owned=@($running | Where-Object Name -eq 'MagicCommunity')
    if($owned.Count){
        if(-not $Record){throw '开发验收桌面仍在运行，请先从社区版菜单正常退出，再继续安装。'}
        Request-MagicSetupDesktopExit $Record
        foreach($process in $owned){if(-not $process.Process.WaitForExit(15000)){throw '社区版尚未正常退出，未强制结束或继续更新。'}}
    }
    # A guard/backend/helper can outlive the main process. Wait for its own exit,
    # but never ask Windows to force-close it, an unknown process or another session.
    $until=[DateTime]::UtcNow.AddSeconds(10)
    do{
        $remaining=@(Get-MagicSetupProcesses $Package)
        if(-not $remaining.Count){return}
        Assert-MagicSetupProcessScope $remaining
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $until)
    throw '仍有社区版组件未退出，保留安装和数据，请关闭相关操作后重试。'
}
function Set-MagicSetupStartup($Package,[bool]$Enabled) {
    $entry=Join-Path $Package.InstallLocation 'MagicNativeEntry.exe'
    $process=Start-Process -FilePath $entry -ArgumentList @('--configure-startup',$(if($Enabled){'enable'}else{'disable'})) -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0){throw 'Windows 未允许更改开机启动。安装及数据已保留，可在 Windows 启动应用设置中检查。'}
}
function Move-MagicSetupDataToRecycleBin([string]$Root) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Root,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}
function Invoke-MagicSetup([ValidateSet('Install','Configure','Uninstall')][string]$Action,[string]$DataRoot,[string]$BundleDirectory,[bool]$KeepData=$true,[bool]$Startup=$false) {
    # Repeat all checks immediately before mutation, not just when drawing the page.
    $plan=Get-MagicSetupPlan $Action $DataRoot $BundleDirectory
    $mutex=[Threading.Mutex]::new($false,('Global\MagicCommunitySetup-'+(Get-MagicSetupSid)))
    $locked=$false
    try{
        try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
        if(-not $locked){throw '另一个安装或卸载操作正在进行。'}
        $plan=Get-MagicSetupPlan $Action $DataRoot $BundleDirectory
        if($Action -eq 'Uninstall'){
            $removeRoot=if(-not $KeepData){Assert-MagicSetupOwnedTree $plan.record}else{$null}
            Stop-MagicSetupDesktop $plan.package $plan.record
            Remove-MagicLegacyStartup $plan $plan.record
            Remove-AppxPackage -Package $plan.package.PackageFullName -ErrorAction Stop
            if(Get-MagicInstalledPackage){throw 'Windows 仍报告应用已安装，未清理用户数据。'}
            if($removeRoot){
                # Recheck after shutdown/removal; no wildcard deletion and no external collected files.
                $removeRoot=Assert-MagicSetupOwnedTree $plan.record
                Move-MagicSetupDataToRecycleBin $removeRoot
                if(Test-Path -LiteralPath $removeRoot){throw '程序已卸载，但数据未能移入回收站；数据保留。'}
            }
            $plan.record.state='uninstalled'
            Write-MagicSetupJson (Get-MagicSetupRegistrationPath) $plan.record
            try{$shortcutNotice=Remove-MagicSetupDesktopShortcut $plan.record}catch{$shortcutNotice='程序已卸载。快捷方式清理提示：'+$_.Exception.Message}
            return [pscustomobject]@{state='uninstalled';dataRetained=$KeepData;dataRoot=$plan.dataRoot;startupRemovedWithPackage=$true;externalFilesChanged=$false;shortcutNotice=$shortcutNotice}
        }
        if($Action -eq 'Install'){
            Stop-MagicSetupDesktop $plan.package $plan.record
            # Windows performs certificate trust and package signature validation.
            # No trust-store import, force shutdown, downgrade or silent re-registration.
            Add-AppxPackage -Path $plan.msix -ErrorAction Stop
        }
        $package=Get-MagicInstalledPackage
        if(-not $package -or $package.Status -ne 'Ok'){throw 'Windows 未确认安装成功。'}
        if($Action -eq 'Install' -and $plan.bundle.packageFamily -and $package.PackageFamilyName -cne $plan.bundle.packageFamily){throw 'Windows 中注册的发布者与安装包不符，数据配置已停止。'}
        $descriptor=Read-MagicSetupJson (Join-Path $package.InstallLocation 'installed-desktop.json')
        if($descriptor.protocol -cne $script:MagicInstallProtocol){throw '当前安装包不支持独立数据目录，请使用新版安装包。'}
        if($Action -eq 'Install' -and [string]$package.Version -ne $plan.bundle.version){throw 'Windows 中的实际版本与目标版本不一致。'}
        $markerPath=Join-Path $plan.dataRoot '.magic-data.json'
        $id=if(Test-Path -LiteralPath $markerPath){(Read-MagicSetupJson $markerPath).installId}else{[guid]::NewGuid().ToString('N')}
        $record=[ordered]@{protocol=$script:MagicInstallProtocol;state='installed';ownerSid=(Get-MagicSetupSid);dataRoot=$plan.dataRoot;installId=$id;packageFamily=$package.PackageFamilyName;packageVersion=[string]$package.Version;startupRequested=$(if($plan.record){[bool]$plan.record.startupRequested}else{$false});startupConfigured=$false;legacyRoot=$(if($plan.legacy){$plan.legacy.root}else{$null})}
        Write-MagicSetupJson $markerPath ([ordered]@{protocol=$record.protocol;ownerSid=$record.ownerSid;dataRoot=$record.dataRoot;installId=$id})
        Write-MagicSetupJson (Get-MagicSetupRegistrationPath) $record
        Remove-MagicLegacyStartup $plan ([pscustomobject]$record)
        $record.legacyRoot=$null
        if(-not $plan.package -or -not $plan.record -or $plan.record.state -ne 'installed' -or -not $plan.record.startupConfigured -or [bool]$plan.record.startupRequested -ne $Startup){Set-MagicSetupStartup $package $Startup}
        $record.startupRequested=$Startup;$record.startupConfigured=$true;Write-MagicSetupJson (Get-MagicSetupRegistrationPath) $record
        return [pscustomobject]@{state='installed';dataRoot=$plan.dataRoot;version=[string]$package.Version;startup=$Startup;updated=$plan.update}
    }finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
