param([ValidateSet('Check','Secure','PrepareUser','ScanCommon','SyncUser','SyncCommon')][string]$Mode='Check',[string]$Version,[string]$ExpectedInstallRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Win32Install.Core.ps1')
try {
    Assert-MagicInstallTarget $ExpectedInstallRoot
    $target=Get-MagicWin32Root
    if($Mode -eq 'PrepareUser'){
        Request-MagicInstalledUserExit
        exit (64 + (Get-MagicShortcutMask @(Get-MagicOwnedShortcuts (Get-MagicShortcutFolders))))
    }
    if($Mode -eq 'ScanCommon'){exit (64 + (Get-MagicShortcutMask @(Get-MagicOwnedShortcuts (Get-MagicShortcutFolders -Common))))}
    if($Mode -eq 'SyncUser'){Sync-MagicOwnedShortcuts (Get-MagicShortcutFolders);exit 0}
    if($Mode -eq 'SyncCommon'){Sync-MagicOwnedShortcuts (Get-MagicShortcutFolders -Common) -Common;exit 0}
    # ExpectedInstallRoot is only compared with the protected machine directory;
    # it never selects a caller-supplied target for file or ACL changes.
    for($ancestor=$target;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装路径含链接，请先修复安装目录。'}
    }
    if($Mode -eq 'Check'){
        Assert-MagicInstalledStopped
        if(Test-Path -LiteralPath $target){
            $items=@((Get-Item -LiteralPath $target)) + @(Get-ChildItem -LiteralPath $target -Force -Recurse)
            $privileged=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
            foreach($item in $items){
                if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '现有安装中含链接，未覆盖。'}
                $acl=Get-Acl -LiteralPath $item.FullName
                if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $privileged){throw '现有安装目录所有者异常，请先检查并移走该目录，未覆盖。'}
                foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
                    if($rule.AccessControlType -eq 'Allow' -and -not($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and ([long]$rule.FileSystemRights -band (0x50000000 -bor [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership)) -and $rule.IdentityReference.Value -notin $privileged){throw '现有安装允许普通用户改写，请先修复目录权限，未覆盖。'}
                }
            }
            $current=Join-Path $target 'MagicCommunity.exe'
            if($Version -and (Test-Path -LiteralPath $current) -and [version]([version](Get-Item -LiteralPath $current).VersionInfo.FileVersion).ToString(3) -gt [version]$Version){throw '已安装版本较新，不能用此安装包降级。'}
        }
        Assert-MagicInstalledFilesFree
    }else{
        $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $acl=[Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin)
        foreach($spec in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($spec[0]),$spec[1],'ContainerInherit,ObjectInherit','None','Allow'))
        }
        Set-Acl -LiteralPath $target -AclObject $acl
        # Directory inheritance is reset in parent-before-child order, with no links.
        foreach($item in Get-ChildItem -LiteralPath $target -Force -Recurse | Sort-Object {$_.FullName.Length}){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '安装内含链接，权限处理已停止。'}
            $security=Get-Acl -LiteralPath $item.FullName
            $security.SetOwner($admin)
            foreach($rule in @($security.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier]))){[void]$security.RemoveAccessRuleSpecific($rule)}
            $security.SetAccessRuleProtection($false,$false)
            Set-Acl -LiteralPath $item.FullName -AclObject $security
        }
    }
    exit 0
}catch{
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Magic 视界社区版安装检查','OK','Warning')
    exit 32
}
