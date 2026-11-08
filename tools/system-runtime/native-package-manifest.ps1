# Only the public toolbar registry opts out of MSIX AppData virtualization.
# Do not disable file system or registry virtualization for the whole package.
function Get-MagicNativePackageManifest {
    param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion,[switch]$Distribution,[string]$PackagePublisher='CN=MagicCommunity.Native.Local')
    $template=@'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10" xmlns:virtualization="http://schemas.microsoft.com/appx/manifest/virtualization/windows10" IgnorableNamespaces="uap uap3 uap10 rescap virtualization desktop">
 <Identity Name="MagicCommunity.Native" Publisher="__MAGIC_PUBLISHER__" Version="__MAGIC_PACKAGE_VERSION__" ProcessorArchitecture="x64" />
 <Properties><DisplayName>Magic 视界社区版</DisplayName><PublisherDisplayName>__MAGIC_PUBLISHER_DISPLAY__</PublisherDisplayName><Logo>Assets\Logo150.png</Logo>
  <virtualization:FileSystemWriteVirtualization>
   <virtualization:ExcludedDirectories>
    <virtualization:ExcludedDirectory>$(KnownFolder:LocalAppData)\MagicCommunity\Extensions\v1</virtualization:ExcludedDirectory>
__MAGIC_DATA_EXCLUSIONS__
   </virtualization:ExcludedDirectories>
  </virtualization:FileSystemWriteVirtualization>
 </Properties>
 <Resources><Resource Language="zh-cn"/><Resource Language="en-us"/></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.28000.0" /></Dependencies>
 <Applications><Application Id="App" Executable="MagicNativeEntry.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="Magic 视界社区版" Description="Magic 视界社区版原生桌面" BackgroundColor="transparent" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png" />__MAGIC_STARTUP__</Application></Applications>
 <Capabilities><rescap:Capability Name="unvirtualizedResources"/><rescap:Capability Name="runFullTrust"/><rescap:Capability Name="allowElevation"/><rescap:Capability Name="globalMediaControl"/><uap3:Capability Name="userNotificationListener"/><DeviceCapability Name="bluetooth"/><DeviceCapability Name="radios"/></Capabilities>
</Package>

'@
    $data=if($Distribution){@'
    <virtualization:ExcludedDirectory>$(KnownFolder:LocalAppData)\MagicCommunity\Deployment</virtualization:ExcludedDirectory>
    <virtualization:ExcludedDirectory>$(KnownFolder:LocalAppData)\MagicCommunity\UserData</virtualization:ExcludedDirectory>
'@}else{''}
    $startup=if($Distribution){'<Extensions><desktop:Extension Category="windows.startupTask" Executable="MagicNativeEntry.exe" EntryPoint="Windows.FullTrustApplication" uap10:Parameters="--logon"><desktop:StartupTask TaskId="MagicCommunityStartup" Enabled="false" DisplayName="Magic 视界社区版"/></desktop:Extension></Extensions>'}else{''}
    if($Distribution){$template=$template.Replace('MinVersion="10.0.19041.0"','MinVersion="10.0.22000.0"')}
    $display=if($PackagePublisher -ceq 'CN=MagicCommunity.Native.Local'){'Magic Community Local Validation'}else{'Magic 视界社区版'}
    return $template.Replace('__MAGIC_PUBLISHER__',[Security.SecurityElement]::Escape($PackagePublisher)).Replace('__MAGIC_PUBLISHER_DISPLAY__',$display).Replace('__MAGIC_PACKAGE_VERSION__',$PackageVersion).Replace('__MAGIC_DATA_EXCLUSIONS__',$data).Replace('__MAGIC_STARTUP__',$startup)
}
