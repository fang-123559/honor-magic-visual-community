using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
namespace MagicCommunity;
internal static class NativeMediaSource
{
 internal sealed record Window(string Id,long Hwnd,int Pid,long Created,string Path,string Aumid,string Title);
 internal sealed record Probe(string Title,Window[] Windows,bool Select);
 internal static bool Matches(string appId,string aumid,string executable){
  if(string.IsNullOrWhiteSpace(appId))return false;
  if(appId.Equals(aumid,StringComparison.OrdinalIgnoreCase))return true;
  string name=System.IO.Path.GetFileName(executable);
  if(appId.Equals(name,StringComparison.OrdinalIgnoreCase)||appId.Equals(System.IO.Path.GetFileNameWithoutExtension(name),StringComparison.OrdinalIgnoreCase))return true;
  return name.ToLowerInvariant() switch{"msedge.exe"=>appId.Equals("MicrosoftEdge",StringComparison.OrdinalIgnoreCase)||appId.StartsWith("MSEdge",StringComparison.OrdinalIgnoreCase),"chrome.exe"=>appId.Equals("Chrome",StringComparison.OrdinalIgnoreCase),"firefox.exe"=>appId.Equals("org.mozilla.firefox",StringComparison.OrdinalIgnoreCase),_=>false};
 }
 internal static bool Browser(string path)=>System.IO.Path.GetFileName(path).ToLowerInvariant() is "msedge.exe" or "chrome.exe" or "firefox.exe" or "brave.exe" or "vivaldi.exe" or "opera.exe";
 internal static bool SameTitle(string title,string actual){
  title=title.Trim();actual=actual.Trim();if(title.Length<2||title.Length>512)return false;
  return actual.Equals(title,StringComparison.OrdinalIgnoreCase)||actual.StartsWith(title+" - ",StringComparison.OrdinalIgnoreCase)||actual.StartsWith(title+" — ",StringComparison.OrdinalIgnoreCase);
 }
 private static bool Verify(Window w){
  if(!IsWindow(new(w.Hwnd)))return false;GetWindowThreadProcessId(new(w.Hwnd),out uint pid);if(pid!=w.Pid)return false;
  using var p=Process.GetProcessById(w.Pid);return p.StartTime.ToFileTimeUtc()==w.Created&&string.Equals(p.MainModule?.FileName,w.Path,StringComparison.OrdinalIgnoreCase);
 }
 internal static int Run(){
  try{
   char[] chars=new char[32769];int count=0,n;while(count<chars.Length&&(n=Console.In.Read(chars,count,chars.Length-count))>0)count+=n;if(count>32768)throw new InvalidDataException();
   var request=JsonSerializer.Deserialize<Probe>(new string(chars,0,count))??throw new InvalidDataException();if(request.Title.Length is <2 or >512||request.Windows.Length is <1 or >16)throw new InvalidDataException();
   var watch=Stopwatch.StartNew();var matches=new List<(Window W,AutomationElement Tab)>();int nodes=0;
   foreach(var w in request.Windows){
    if(!Browser(w.Path)||!Verify(w))continue;
    void Visit(AutomationElement element,int depth){
     if(depth>12||++nodes>1200||watch.ElapsedMilliseconds>2200)return;
     var current=element.Current;var type=current.ControlType;
     if(type==ControlType.Document||type==ControlType.Edit)return; // Do not traverse web content or address/search fields.
     if(type==ControlType.TabItem){if(SameTitle(request.Title,current.Name)&&current.IsEnabled)matches.Add((w,element));return;}
     var walker=TreeWalker.ControlViewWalker;var child=walker.GetFirstChild(element);
     while(child is not null&&nodes<1200&&watch.ElapsedMilliseconds<=2200){Visit(child,depth+1);child=walker.GetNextSibling(child);}
    }
    Visit(AutomationElement.FromHandle(new(w.Hwnd)),0);
   }
   if(nodes>=1200||watch.ElapsedMilliseconds>2200||matches.Count!=1){Console.WriteLine("{}");return 0;}
   var chosen=matches[0];if(!Verify(chosen.W))throw new InvalidDataException();bool selected=false;
   if(request.Select){if(!chosen.Tab.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var pattern))throw new InvalidOperationException();var item=(SelectionItemPattern)pattern;if(!item.Current.IsSelected)item.Select();selected=item.Current.IsSelected;}
   Console.WriteLine(JsonSerializer.Serialize(new{id=chosen.W.Id,hwnd=chosen.W.Hwnd,selected}));return 0;
  }catch(Exception e)when(e is not OutOfMemoryException){Console.WriteLine("{}");return 1;}
 }
 internal static async Task<JsonElement> Query(string title,Window[] windows,bool select,CancellationToken token){
  var start=new ProcessStartInfo(System.IO.Path.Combine(AppContext.BaseDirectory,"MagicCommunity.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--media-source-probe");
  using var child=Process.Start(start)??throw new IOException();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(4));
  try{
   string packet=JsonSerializer.Serialize(new Probe(title,windows,select));if(packet.Length>32768)throw new InvalidDataException();await child.StandardInput.WriteAsync(packet.AsMemory(),deadline.Token);child.StandardInput.Close();
   var output=child.StandardOutput.ReadToEndAsync(deadline.Token);var error=child.StandardError.ReadToEndAsync(deadline.Token);await child.WaitForExitAsync(deadline.Token);string data=await output;await error;if(data.Length>2048||child.ExitCode!=0)throw new InvalidOperationException("无法读取媒体来源的标签页。");return JsonSerializer.Deserialize<JsonElement>(data);
  }finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
 }
 [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
}
