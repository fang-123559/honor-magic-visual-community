# Loaded only by the installation wizard; COM is initialized on explicit creation.
$script:MagicShortcutNames=@('Magic 视界社区版.lnk','Magic 视界社区版（安装版）.lnk')
function Get-MagicSetupUserDesktop { [Environment]::GetFolderPath('DesktopDirectory') }
function Get-MagicShortcutReceiptPath { Join-Path (Split-Path (Get-MagicSetupRegistrationPath)) 'desktop-shortcut.json' }
function Get-MagicShortcutReceipt([string]$Family) {
    $path=Get-MagicShortcutReceiptPath
    if(-not (Test-Path -LiteralPath $path)){return}
    $receipt=Read-MagicSetupJson $path
    if($receipt.protocol -cne 'Magic.DesktopShortcut/1' -or $receipt.ownerSid -cne (Get-MagicSetupSid) -or $receipt.family -cne $Family -or $receipt.name -cnotin $script:MagicShortcutNames -or $receipt.sha256 -cnotmatch '^[0-9A-F]{64}$'){
        throw '快捷方式记录无法核验，原文件已保留。'
    }
    return $receipt
}
function Get-MagicSetupAppTarget($Package) {
    if(-not $Package -or $Package.Status -ne 'Ok' -or $Package.PackageFamilyName -cnotmatch '^MagicCommunity\.Native_[a-z0-9]+$'){
        throw '当前账户尚未完成社区版安装。'
    }
    return 'shell:AppsFolder\'+$Package.PackageFamilyName+'!App'
}
function New-MagicSetupDesktopShortcut {
    $lock=[Threading.Mutex]::new($false,('Global\MagicCommunitySetup-'+(Get-MagicSetupSid)))
    $owned=$false
    try{
        try{$owned=$lock.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
        if(-not $owned){throw '安装操作仍在进行，请稍后再创建快捷方式。'}
        $package=Get-MagicInstalledPackage
        $target=Get-MagicSetupAppTarget $package
        $record=Get-MagicSetupRecord
        if(-not $record -or $record.state -cne 'installed' -or $record.packageFamily -cne $package.PackageFamilyName){throw '请先完成当前账户的数据目录配置。'}
        $desktop=Get-MagicSetupUserDesktop
        if(-not $desktop -or -not [IO.Directory]::Exists($desktop)){throw '当前账户的桌面目录不可用。'}
        if(-not ('MagicCommunity.Setup.DesktopLink' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'SetupShortcut.cs')}
        $receipt=Get-MagicShortcutReceipt $package.PackageFamilyName
        if($receipt){
            $link=Join-Path $desktop $receipt.name
            if([IO.File]::Exists($link) -and -not ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
                try{if([MagicCommunity.Setup.DesktopLink]::PointsTo($target,$link)){return $link}}catch{}
            }
        }
        $name=$script:MagicShortcutNames | Where-Object {-not (Test-Path -LiteralPath (Join-Path $desktop $_))} | Select-Object -First 1
        if(-not $name){throw '桌面上两个可用名称均被占用，请先保留或重命名现有文件。'}
        $link=Join-Path $desktop $name
        $temporary=Join-Path $desktop ('.magic-shortcut-'+[guid]::NewGuid().ToString('N')+'.lnk')
        try{
            [MagicCommunity.Setup.DesktopLink]::Save($target,$temporary)
            if(-not [MagicCommunity.Setup.DesktopLink]::PointsTo($target,$temporary)){throw 'Windows 未保存正确的应用入口。'}
            $expected=(Get-FileHash -LiteralPath $temporary).Hash
            # Record intent first. Move without replacement; a concurrent user file wins.
            Write-MagicSetupJson (Get-MagicShortcutReceiptPath) @{protocol='Magic.DesktopShortcut/1';ownerSid=$record.ownerSid;family=$package.PackageFamilyName;name=$name;sha256=$expected}
            [IO.File]::Move($temporary,$link)
            return $link
        }finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
    }finally{if($owned){$lock.ReleaseMutex()};$lock.Dispose()}
}
function Move-MagicShortcutToRecycleBin([string]$Path){
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}
function Remove-MagicSetupDesktopShortcut($Record) {
    $receipt=Get-MagicShortcutReceipt $Record.packageFamily
    if(-not $receipt){return}
    $desktop=Get-MagicSetupUserDesktop
    if(-not $desktop -or -not [IO.Directory]::Exists($desktop)){return '桌面目录不可用，快捷方式已保留。'}
    $link=Join-Path $desktop $receipt.name
    if(-not [IO.File]::Exists($link)){return}
    if(((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $link).Hash -cne $receipt.sha256){return '桌面快捷方式已经改变，已保留该文件。'}
    Move-MagicShortcutToRecycleBin $link
}
