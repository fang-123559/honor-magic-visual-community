using Microsoft.Win32;
using System.Runtime.InteropServices;
namespace MagicCommunity;
// User-initiated only. No watcher, timer, elevation or CloudStore binary patch.
// HKCU personalization is a Windows compatibility setting, not a WinRT theme setter.
internal static class NativeAppearanceSettings
{
    internal static bool IsDark {get{try{return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int value&&value==0;}catch(Exception e)when(e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException){return false;}}}
    internal static void SetDark(bool dark)
    {
        const string path=@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key=Registry.CurrentUser.CreateSubKey(path,true)??throw new InvalidOperationException("无法写入当前用户的主题设置。");
        int value=dark?0:1;object? beforeApps=key.GetValue("AppsUseLightTheme"),beforeSystem=key.GetValue("SystemUsesLightTheme");
        try{key.SetValue("AppsUseLightTheme",value,RegistryValueKind.DWord);key.SetValue("SystemUsesLightTheme",value,RegistryValueKind.DWord);
            if(key.GetValue("AppsUseLightTheme") is not int read||read!=value)throw new InvalidOperationException("Windows 未确认主题设置。");}
        catch{Restore(key,"AppsUseLightTheme",beforeApps);Restore(key,"SystemUsesLightTheme",beforeSystem);throw;}
        // Bounded broadcast; settings windows/native surfaces already handle this event.
        SendMessageTimeout(new IntPtr(0xffff),0x1a,IntPtr.Zero,"ImmersiveColorSet",2,200,out _);
    }
    private static void Restore(RegistryKey key,string name,object? value){try{if(value is int n)key.SetValue(name,n,RegistryValueKind.DWord);else if(value is null)key.DeleteValue(name,false);}catch{}}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr SendMessageTimeout(IntPtr window,int message,IntPtr w,string l,uint flags,uint timeout,out IntPtr result);
}
