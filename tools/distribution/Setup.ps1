param([switch]$Preview,[ValidateSet('Install','Update','Uninstall','Failure')][string]$Scenario='Install',[string]$RenderDirectory,[switch]$VerifyOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms
. (Join-Path $PSScriptRoot 'Setup.Core.ps1')
if($VerifyOnly){
    foreach($directory in @($PSScriptRoot,(Join-Path $PSScriptRoot 'update'))){
        $info=Read-MagicSetupJson (Join-Path $directory 'setup-bundle.json')
        if($info.protocol -cne $script:MagicInstallProtocol -or $info.packageName -cne 'MagicCommunity.Native' -or $info.dataRootMode -cne 'per-user' -or $info.packageFile -cnotmatch '^MagicCommunity\.Native_[0-9.]+_x64\.msix$'){throw '无效的安装包清单。'}
        if((Get-FileHash -LiteralPath (Join-Path $directory $info.packageFile)).Hash -cne $info.sha256){throw '安装包校验失败。'}
    }
    exit 0
}
if($RenderDirectory -and -not $Preview){throw 'Rendering requires Preview.'}
$script:record=$null;$script:package=$null;$script:readError=$null
$script:selectedBundle=$PSScriptRoot;$script:availableVersion=$null;$script:shortcutNotice=$null;$script:workingOnShortcut=$false
if(-not $Preview){
 try{
  $script:record=Get-MagicSetupRecord;$script:package=Get-MagicInstalledPackage
  $selected=Select-MagicSetupBundle $PSScriptRoot $package $record
  $script:selectedBundle=$selected.directory;$script:availableVersion=$selected.version
 }catch{$script:readError=$_.Exception.Message}
}
$script:action=if($Scenario -eq 'Uninstall'){'Uninstall'}else{'Install'};$script:stage=0;$script:busy=$false
$script:dataRoot=if($record -and ($record.state -eq 'installed' -or ($record.dataRoot -and (Test-Path -LiteralPath (Join-Path $record.dataRoot '.magic-data.json'))))){$record.dataRoot}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MagicCommunity\UserData'}
$script:update=($package -and $record -and $record.state -eq 'installed') -or ($Preview -and $Scenario -eq 'Update')
$script:legacyMigrates=$false;$script:keep=$true;$script:startup=if($record){[bool]$record.startupRequested}else{$false}
[xml]$xaml=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Title="Magic 视界社区版 · 安装与卸载" Width="850" Height="650" MinWidth="720" MinHeight="560" WindowStartupLocation="CenterScreen" Background="#F6F7FA" FontFamily="Microsoft YaHei UI" FontSize="15">
<Grid Margin="32" Name="Layout"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
<StackPanel Name="Header"/><ScrollViewer Grid.Row="1" Margin="0,22" VerticalScrollBarVisibility="Auto"><StackPanel Name="Body"/></ScrollViewer>
<DockPanel Grid.Row="2"><Button Name="Back" Content="返回" Padding="22,10" Margin="0,0,12,0" HorizontalAlignment="Left"/><Button Name="Next" Content="继续" Padding="28,10" Background="#0067C0" Foreground="White" HorizontalAlignment="Right"/></DockPanel>
</Grid></Window>
'@
$window=[Windows.Markup.XamlReader]::Load([Xml.XmlNodeReader]::new($xaml))
$header=$window.FindName('Header');$body=$window.FindName('Body');$back=$window.FindName('Back');$next=$window.FindName('Next')
function Text($Panel,[string]$Value,[int]$Size=15,[string]$Color='#202126') {
 $label=[Windows.Controls.TextBlock]::new();$label.Text=$Value;$label.FontSize=$Size;$label.Foreground=$Color;$label.TextWrapping='Wrap';$label.Margin='0,0,0,14';[void]$Panel.Children.Add($label)
}
function Option([string]$Label,[scriptblock]$Clicked) {
 $button=[Windows.Controls.Button]::new();$button.Content=$Label;$button.HorizontalContentAlignment='Left';$button.Padding='16,12';$button.Margin='0,0,0,10';$button.Add_Click($Clicked);[void]$body.Children.Add($button)
}
function Save-Options {
 if($script:pathBox){$script:dataRoot=$script:pathBox.Text};if($script:keepBox){$script:keep=[bool]$script:keepBox.IsChecked};if($script:startBox){$script:startup=[bool]$script:startBox.IsChecked}
}
function Show-Stage {
 $header.Children.Clear();$body.Children.Clear();$script:pathBox=$null;$script:keepBox=$null;$script:startBox=$null
 Text $header 'Magic 视界社区版' 27
 Text $header $(if($Preview){'流程预览 · 所有按钮均不会安装、卸载或更改系统'}else{'程序由 Windows 管理 · 用户数据独立保存'}) 13 '#626773'
 $back.Visibility=if($stage -eq 0 -or $stage -ge 3){'Collapsed'}else{'Visible'};$next.IsEnabled=$true
 switch($stage){
  0 {
   Text $body '欢迎使用安装与卸载向导' 22
   Text $body '首次安装可选择布局、卡片和反馈的数据目录；更新会沿用原目录。卸载时可单独决定是否保留数据。'
   Text $body '程序位置由 Windows 管理，无需选择 WindowsApps 路径。' 14 '#626773'
   if($readError){Text $body $readError 14 '#B42318';$next.IsEnabled=$false}
   Option '自定义安装或更新…' {$script:action='Install';$script:stage=1;Show-Stage}
   Option '配置已安装版本的数据目录' {$script:action='Configure';$script:stage=1;Show-Stage}
   Option '卸载社区版' {$script:action='Uninstall';$script:stage=1;Show-Stage}
   if($package){Text $body ('当前已安装：'+$package.Version) 14 '#626773'}
   if($availableVersion){Text $body ('本次安装包：'+$availableVersion) 14 '#626773'}
   $next.Content=if($package -and $availableVersion -and [version]$package.Version -ge $availableVersion){'关闭'}elseif($package -or $update){'一键更新'}else{'一键安装'}
  }
  1 {
   if($action -eq 'Uninstall'){
    Text $body '卸载与数据保留' 22
    Text $body '正常退出社区版，移除本应用及其 MSIX 开机启动项。Windows 桌面文件、外部插件及其它应用不会被删除。'
    $script:keepBox=[Windows.Controls.CheckBox]::new();$keepBox.Content='保留我的布局、卡片、反馈和恢复记录（推荐）';$keepBox.IsChecked=$keep;$keepBox.Margin='0,12,0,20';[void]$body.Children.Add($keepBox)
    Text $body ('数据位置：'+$dataRoot) 14 '#626773'
    Text $body '取消勾选后，仅将经过归属检查的社区版数据目录移入回收站。仍有实际收纳文件时，须先移出文件或保留数据。' 14 '#626773'
   }else{
    Text $body $(if($update){'更新沿用原数据位置'}else{'选择数据存放位置'}) 22
    Text $body $(if($update){'更新保留现有布局和卡片。本次不重新选择或迁移目录。'}else{'建议使用默认目录，也可选择本地专用子文件夹。支持中文、空格和中文括号。'})
    $script:pathBox=[Windows.Controls.TextBox]::new();$pathBox.Text=$dataRoot;$pathBox.Padding='12';$pathBox.IsReadOnly=$update;$pathBox.Margin='0,0,0,12';[void]$body.Children.Add($pathBox)
    if(-not $update){Option '浏览数据文件夹…' {
     $picker=[Windows.Forms.FolderBrowserDialog]::new();$picker.Description='选择社区版专用数据文件夹，可新建空目录。';$picker.SelectedPath=$script:dataRoot
     try{if($picker.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$script:pathBox.Text=$picker.SelectedPath}}finally{$picker.Dispose()}
    }}
    $script:startBox=[Windows.Controls.CheckBox]::new();$startBox.Content='登录 Windows 时自动进入社区版';$startBox.IsChecked=$startup;$startBox.Margin='0,12,0,12';[void]$body.Children.Add($startBox)
    Text $body '首次安装默认不开机自启。通知访问和管理员窗口助手由 Windows 在需要时单独授权。' 14 '#626773'
   }
   $next.Content='检查并继续'
  }
  2 {
   Text $body '确认本次操作' 22
   Text $body ('操作：'+$(switch($action){'Install'{if($update){'更新社区版'}else{'安装社区版'}}'Configure'{'配置数据目录'}'Uninstall'{'卸载社区版'}})) 18
   Text $body ('数据目录：'+$dataRoot)
   if($action -eq 'Uninstall'){Text $body $(if($keep){'卸载后保留数据，重新安装可选回原目录。'}else{'卸载成功后，将社区版数据目录移入回收站。'}) 16}
   else{Text $body ('开机启动：'+$(if($startup){'开启'}else{'关闭'}))}
   if($script:legacyMigrates){Text $body '同时撤销已核对归属的旧开发版开机任务，并保存任务备份；旧仓库及数据完整保留。' 14}
   Text $body '若正常退出、文件校验或 Windows 部署失败，会显示具体原因，不强制结束程序，不自动导入证书。' 14 '#626773'
   $next.Content=if($Preview){'模拟执行'}elseif($action -eq 'Uninstall'){'确认卸载'}else{'确认执行'}
  }
  3 {
   Text $body $(if($workingOnShortcut){'正在创建桌面快捷方式'}else{'正在执行，请稍候'}) 22
   Text $body '正在检查安装包、应用退出状态及 Windows 部署结果。完成前请勿关闭向导。'
   $progress=[Windows.Controls.ProgressBar]::new();$progress.Height=5;$progress.IsIndeterminate=$true;[void]$body.Children.Add($progress)
   $next.Content='处理中…';$next.IsEnabled=$false
  }
  4 {
   Text $body $(if($Preview){'流程预览完成'}elseif($action -eq 'Uninstall'){'已卸载社区版'}else{'已完成安装配置'}) 22
   Text $body $(if($Preview){'本次没有安装或卸载，没有更改数据、权限或开机启动。'}elseif($action -eq 'Uninstall'){if($keep){'数据已保留，重新安装时可选择原目录。'}else{'社区版数据已移入回收站。'}}else{'可从 Windows 开始菜单打开 Magic 视界社区版。首次进入后，请按需开启通知访问。'})
   Text $body ('数据位置：'+$dataRoot) 14 '#626773'
   if($action -ne 'Uninstall'){
    Option '创建桌面快捷方式' {
     if($Preview){$script:shortcutNotice='预览：快捷方式会放到当前账户的桌面。';Show-Stage}else{Start-WizardWork -Shortcut}
    }
    Option '立即打开 Magic 视界社区版' {
     if(-not $Preview){try{Start-Process -FilePath (Join-Path ([Environment]::GetFolderPath('Windows')) 'explorer.exe') -ArgumentList (Get-MagicSetupAppTarget (Get-MagicInstalledPackage))}catch{[void][Windows.MessageBox]::Show($_.Exception.Message,'启动提示')}}
    }
   }
   if($shortcutNotice){Text $body $shortcutNotice 14 '#626773'}
   $next.Content='完成'
  }
  5 {
   Text $body '操作未完整完成' 22 '#B42318';Text $body $script:failure 15 '#B42318'
   Text $body '请按原因处理后重试。若程序已安装、仅数据或启动配置失败，可返回选择“配置已安装版本的数据目录”。未将部分成功显示为全部完成。' 14 '#626773'
   $next.Content='返回检查'
  }
 }
}
function Start-WizardWork([switch]$Shortcut) {
 if($script:busy){return}
 $script:workingOnShortcut=[bool]$Shortcut;$script:stage=3;$script:busy=$true;Show-Stage
 $script:worker=[PowerShell]::Create()
 [void]$worker.AddScript('param($core,$shortcut,$action,$root,$bundle,$keep,$startup); . $core; if($shortcut){New-MagicSetupDesktopShortcut}else{Invoke-MagicSetup -Action $action -DataRoot $root -BundleDirectory $bundle -KeepData $keep -Startup $startup}')
 foreach($arg in @((Join-Path $PSScriptRoot 'Setup.Core.ps1'),[bool]$Shortcut,$action,$dataRoot,$selectedBundle,$keep,$startup)){[void]$worker.AddArgument($arg)}
 $script:running=$worker.BeginInvoke();$script:timer=[Windows.Threading.DispatcherTimer]::new();$timer.Interval=[TimeSpan]::FromMilliseconds(200)
 $timer.Add_Tick({
  if(-not $running.IsCompleted){return};$timer.Stop()
  try{
   $result=@($worker.EndInvoke($running))
   if($worker.HadErrors){throw ($worker.Streams.Error|Out-String)}
   $script:shortcutNotice=if($workingOnShortcut){'桌面快捷方式：'+[string]$result[-1]}elseif($action -eq 'Uninstall' -and $result.Count){$result[-1].shortcutNotice}
   $script:stage=4
  }catch{
   if($workingOnShortcut){$script:shortcutNotice='快捷方式未创建：'+$_.Exception.Message;$script:stage=4}
   else{$script:failure=$_.Exception.Message;$script:stage=5}
  }finally{$worker.Dispose();$script:busy=$false;$script:workingOnShortcut=$false}
  Show-Stage
 });$timer.Start()
}
$back.Add_Click({Save-Options;$script:stage=[Math]::Max(0,$stage-1);Show-Stage})
$next.Add_Click({
 Save-Options
 if($stage -eq 4){$window.Close();return}
 if($stage -eq 5){$script:stage=0;Show-Stage;return}
 if($stage -eq 0){
  if($next.Content -eq '关闭'){$window.Close();return}
  $script:action='Install'
 }
 if($stage -lt 2 -and -not $Preview){
  try{
   $plan=Get-MagicSetupPlan $action $dataRoot $selectedBundle
   $script:dataRoot=$plan.dataRoot;$script:update=$plan.update;$script:legacyMigrates=$null -ne $plan.legacy
  }catch{[void][Windows.MessageBox]::Show($_.Exception.Message,'检查未通过','OK','Warning');return}
 }
 if($stage -eq 1 -or ($stage -eq 0 -and $script:legacyMigrates)){$script:stage=2;Show-Stage;return}
 if($Preview){if($Scenario -eq 'Failure'){$script:failure='示例：Windows 未信任安装包签名。';$script:stage=5}else{$script:stage=4};Show-Stage;return}
 Start-WizardWork
})
$window.Add_Closing({param($sender,$eventArgs)if($script:busy){$eventArgs.Cancel=$true}})
Show-Stage
if($RenderDirectory){
 $layout=$window.Content;$window.Content=$null;$export=[Windows.Controls.Border]::new();$export.Background='#F6F7FA';$export.Child=$layout
 $output=[IO.Path]::GetFullPath($RenderDirectory);[void][IO.Directory]::CreateDirectory($output)
 foreach($view in @('Install','Update','Uninstall','Failure')){
  $script:action=if($view -eq 'Uninstall'){'Uninstall'}else{'Install'};$script:update=$view -eq 'Update'
  foreach($page in @(0,1,2,4,5)){
   if($page -eq 5 -and $view -ne 'Failure'){continue}
   $script:stage=$page;$script:failure='示例：安装包签名未受 Windows 信任。没有更改证书库。';Show-Stage
   $export.Measure([Windows.Size]::new(850,650));$export.Arrange([Windows.Rect]::new(0,0,850,650));$export.UpdateLayout()
   $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(850,650,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($export)
   $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$file=[IO.File]::Create((Join-Path $output ($view+'-'+$page+'.png')));try{$encoder.Save($file)}finally{$file.Dispose()}
  }
 }
 return
}
[void]$window.ShowDialog()
