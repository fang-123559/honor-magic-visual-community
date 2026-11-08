using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MagicCommunity;

internal static class Win32Installation
{
    internal const string Family="MagicCommunity.Unpackaged";
    internal static string CodeRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"MagicCommunityDesktop");
    internal static bool IsUnpackaged { get { uint length=0;return GetCurrentPackageFamilyName(ref length,IntPtr.Zero)==15700; } }
    internal static bool Privileged(string sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal static void ValidateCode()
    {
        if(!string.Equals(AppContext.BaseDirectory.TrimEnd('\\'),CodeRoot,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请运行安装后的开始菜单入口，不能直接启动解压文件或开发工作树。");
        ValidateProtected(new DirectoryInfo(Path.GetDirectoryName(CodeRoot)!));
        ValidateProtected(new DirectoryInfo(CodeRoot));
        int count=0;
        foreach(var file in new DirectoryInfo(CodeRoot).EnumerateFileSystemInfos("*",SearchOption.AllDirectories)){
            if(++count>8192)throw new InvalidDataException("安装文件数量异常。");ValidateProtected(file);
        }
    }
    internal static void ValidateProtected(FileSystemInfo file)
    {
        if((file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("安装文件不能是链接："+file.Name);
        FileSystemSecurity acl=file is DirectoryInfo dir?dir.GetAccessControl():((FileInfo)file).GetAccessControl();
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner||!Privileged(owner.Value))throw new InvalidDataException("安装文件所有者不是管理员："+file.Name);
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&(unchecked((uint)rule.FileSystemRights)&((uint)writes|0x50000000u))!=0&&!Privileged(rule.IdentityReference.Value))
                throw new InvalidDataException("安装目录允许普通用户改写，请重新安装："+file.Name);
    }
    internal static bool Configure()
    {
        ValidateCode();
        string sid=WindowsIdentity.GetCurrent().User!.Value;
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MagicCommunity","UserData");
        JsonElement? prior=null;
        if(File.Exists(InstalledDesktop.RegistrationPath)){
            prior=InstalledDesktop.Read(InstalledDesktop.RegistrationPath);
            if(prior.Value.GetProperty("ownerSid").GetString()!=sid)throw new InvalidDataException("安装记录不属于当前账户。");
            data=InstalledDesktop.ValidatePath(prior.Value.GetProperty("dataRoot").GetString()!);
        }
        var window=new Window{Title="Magic 视界社区版 · 首次运行与启动设置",Width=640,Height=410,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,FontFamily=CommunityFont.Family,FontSize=15};
        var panel=new StackPanel{Margin=new Thickness(28)};window.Content=panel;
        void Text(string text,int size=15)=>panel.Children.Add(new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
        Text("配置当前账户",25);Text(prior is null?"程序已安装。请选择布局、卡片和反馈的数据目录。通知访问会在使用通知中心时单独询问。":"将沿用当前账户的布局和卡片；原安装记录会保留备份。其它账户的数据不受影响。");
        var path=new TextBox{Text=data,Padding=new Thickness(8),IsReadOnly=prior is not null,Margin=new Thickness(0,0,0,10)};panel.Children.Add(path);
        if(prior is null){var browse=new Button{Content="浏览数据文件夹…",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,0,12)};browse.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFolderDialog{Title="选择社区版专用空目录",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};if(picker.ShowDialog(window)==true)path.Text=picker.FolderName;};panel.Children.Add(browse);}
        var startup=new CheckBox{Content="登录 Windows 时自动进入社区版",IsChecked=prior?.TryGetProperty("startupRequested",out var enabled)==true&&enabled.ValueKind==JsonValueKind.True,Margin=new Thickness(0,6,0,12)};panel.Children.Add(startup);
        Text("卸载程序时保留数据；重新安装后可继续使用。",13);
        var confirm=new Button{Content="保存并进入",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(22,10,22,10)};panel.Children.Add(confirm);
        confirm.Click+=(_,_)=>{
            try{ConfigureProfile(path.Text,sid,startup.IsChecked==true,prior);window.DialogResult=true;}
            catch(Exception e)when(e is not OutOfMemoryException){MessageBox.Show(window,e.Message,"配置未完成",MessageBoxButton.OK,MessageBoxImage.Warning);}
        };
        return window.ShowDialog()==true;
    }
    internal static void ConfigureProfile(string value,string sid,bool startup,JsonElement? prior)
    {
        string root=InstalledDesktop.ValidatePath(value),markerPath=Path.Combine(root,".magic-data.json");
        string id=Guid.NewGuid().ToString("N");
        if(File.Exists(markerPath)){
            var marker=InstalledDesktop.Read(markerPath);
            if(marker.GetProperty("protocol").GetString()!=InstalledDesktop.Protocol||marker.GetProperty("ownerSid").GetString()!=sid||!string.Equals(Path.GetFullPath(marker.GetProperty("dataRoot").GetString()!).TrimEnd('\\'),root,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(marker.GetProperty("installId").GetString(),"N",out _))throw new InvalidDataException("已有数据目录归属不一致。");
            id=marker.GetProperty("installId").GetString()!;
            if(prior is { } record&&record.GetProperty("installId").GetString()!=id)throw new InvalidDataException("安装记录与数据目录标识不一致。");
        }else if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidDataException("请选择新的空目录；不会覆盖已有文件。");
        Directory.CreateDirectory(root);
        var markerValue=new{protocol=InstalledDesktop.Protocol,ownerSid=sid,dataRoot=root,installId=id};
        StateStore.AtomicWrite(markerPath,JsonSerializer.Serialize(markerValue),null);
        string registration=InstalledDesktop.RegistrationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        if(File.Exists(registration))File.Copy(registration,registration+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".bak",false);
        StateStore.AtomicWrite(registration,JsonSerializer.Serialize(new{protocol=InstalledDesktop.Protocol,state="installed",ownerSid=sid,dataRoot=root,installId=id,packageFamily=Family,packageVersion=AppController.Version,installKind="win32",codeDirectory=CodeRoot,startupRequested=startup,startupConfigured=true}),null);
    }
    [DllImport("kernel32.dll")]private static extern int GetCurrentPackageFamilyName(ref uint length,IntPtr name);
}
