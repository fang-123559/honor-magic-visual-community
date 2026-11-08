using System.Globalization;
using System.Text.Json;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession
{
 private int pendingMediaOpen;
 private async Task OpenMediaSource(JsonElement offered){
  if(Interlocked.CompareExchange(ref pendingMediaOpen,1,0)!=0)return;
  try{
   string sessionId=Text(offered,"nativeSessionId");var state=await backend.Request("media-read",new{},stop.Token);var session=Array(state,"sessions").SingleOrDefault(s=>Text(s,"id")==sessionId);
   string appId=Text(session,"appId"),title=Text(session,"title");if(appId.Length==0)throw new InvalidOperationException("播放来源已变化，请重试。");
   var snapshot=await backend.Request("snapshot",new{},stop.Token);
   var windows=Array(snapshot,"windows").Where(w=>NativeMediaSource.Matches(appId,Text(w,"aumid"),Text(w,"executable"))).Take(17).ToArray();
   if(windows.Length is 0 or >16)throw new InvalidOperationException("无法唯一定位正在播放的应用窗口。");
   JsonElement target=default;bool browser=windows.Any(w=>NativeMediaSource.Browser(Text(w,"executable")));
   NativeMediaSource.Window Identity(JsonElement w)=>new(Text(w,"id"),long.Parse(Text(w,"hwnd"),CultureInfo.InvariantCulture),(int)Number(w,"pid"),long.Parse(Text(w,"processCreated"),CultureInfo.InvariantCulture),Text(w,"executable"),Text(w,"aumid"),Text(w,"title"));
   if(browser){var found=await NativeMediaSource.Query(title,windows.Select(Identity).ToArray(),false,stop.Token);target=windows.SingleOrDefault(w=>Text(w,"id")==Text(found,"id"));}
   else {var named=windows.Where(w=>NativeMediaSource.SameTitle(title,Text(w,"title"))).ToArray();if(named.Length==1)target=named[0];else if(windows.Length==1)target=windows[0];}
   if(target.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException(browser?"未能唯一定位正在播放的标签页；请在浏览器中打开该页面后重试。":"存在多个播放窗口，请在应用中选择播放窗口。");
   var live=await backend.Request("media-read",new{},stop.Token);if(!Array(live,"sessions").Any(s=>Text(s,"id")==sessionId&&Text(s,"appId")==appId&&Text(s,"title")==title))throw new InvalidOperationException("播放来源已变化，请重试。");
   var action=JsonSerializer.SerializeToElement(new{nativeWindowId=Text(target,"id"),hwnd=long.Parse(Text(target,"hwnd"),CultureInfo.InvariantCulture)});
   await PerformWindowAction(action,"activate");
   if(browser){var selected=await NativeMediaSource.Query(title,[Identity(target)],true,stop.Token);if(!Flag(selected,"selected"))throw new InvalidOperationException("来源窗口已打开，但未能切换到播放标签页。");await PerformWindowAction(action,"activate");}
  }catch(Exception error)when(error is not OutOfMemoryException){await Notice(error);}finally{Volatile.Write(ref pendingMediaOpen,0);}
 }
}
