using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
namespace MagicCommunity;
// Own the chooser independently of Explorer's hidden desktop ListView.
internal static class ShellOpenWith
{
 internal static bool IsChooser(DesktopContextMenu.Entry row)=>string.Equals(row.Verb,"openas",StringComparison.OrdinalIgnoreCase)||row.Key=="openas"||Regex.Replace(row.Label.Split('\t')[0],@"\(&.\)|&","").Trim() is "选择其他应用" or "选择其它应用" or "Choose another app" or "Choose another application";
 internal static OpenAsInfo Request(string[] targets){
  if(targets.Length!=1||!Path.IsPathFullyQualified(targets[0])||targets[0].StartsWith(@"\\",StringComparison.Ordinal)||!File.Exists(targets[0])||Directory.Exists(targets[0]))throw new InvalidDataException("请选择一个仍存在的本机文件。");
  return new(){File=targets[0],Flags=4}; // OAIF_EXEC: Windows owns the user's application choice.
 }
 internal static void Show(string[] targets){
  Request(targets);
  // A real user click with a visible, foreground owner is the verified path
  // on Windows builds where a windowless launch leaves an inert OpenWith task.
  // Keep this temporary owner alive until the synchronous system picker ends.
  var panel=new StackPanel{Margin=new Thickness(24)};
  panel.Children.Add(new TextBlock{Text=Path.GetFileName(targets[0]),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
  var status=new TextBlock{Text="点击下方按钮，在 Windows 中选择应用。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};panel.Children.Add(status);
  var button=new Button{Content="选择应用…",Padding=new Thickness(16,8,16,8),HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(button);
  var window=new Window{Title="打开方式",Content=panel,Width=420,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,ShowInTaskbar=true};
  button.Click+=(_,_)=>{
   button.IsEnabled=false;
   try{
    var request=Request(targets);
    int result=SHOpenWithDialog(new WindowInteropHelper(window).Handle,ref request);
    if(result!=unchecked((int)0x800704c7))Marshal.ThrowExceptionForHR(result);
    window.Close();
   }catch(Exception error){status.Text="Windows 未完成应用选择："+error.Message;button.IsEnabled=true;}
  };
  window.ShowDialog();
 }
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct OpenAsInfo{[MarshalAs(UnmanagedType.LPWStr)]public string File;[MarshalAs(UnmanagedType.LPWStr)]public string? Class;public uint Flags;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHOpenWithDialog(IntPtr owner,ref OpenAsInfo info);
}
