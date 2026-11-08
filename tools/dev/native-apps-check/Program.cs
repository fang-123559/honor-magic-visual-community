using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
var type=assembly.GetType("MagicCommunity.NativeSystemUiSession",true)!;
var entry=type.GetNestedType("AppEntry",BindingFlags.NonPublic)!;
object session=RuntimeHelpers.GetUninitializedObject(type);
void Set(string name,object value)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,value);
var catalog=Array.CreateInstance(entry,1);
catalog.SetValue(Activator.CreateInstance(entry,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{"app:fixture","Owned app","C:\\Owned\\App.exe",""},null),0);
var completionType=typeof(TaskCompletionSource<>).MakeGenericType(catalog.GetType());
var completion=Activator.CreateInstance(completionType)!;
Set("appScanGate",new object());Set("appScan",completionType.GetProperty("Task")!.GetValue(completion)!);Set("appsRead",DateTimeOffset.UtcNow);
Set("appsWidth",824d);Set("appsHeight",510d);Set("applicationEntries",catalog);Set("appsAll",true);Set("appsSearch","");Set("appsFolder","");Set("favorites",new JsonArray());Set("stop",new CancellationTokenSource());
var method=type.GetMethod("AppsPanel",BindingFlags.NonPublic|BindingFlags.Instance)!;
async Task<JsonObject> Panel(){var task=(Task)method.Invoke(session,null)!;await task;var panel=task.GetType().GetProperty("Result")!.GetValue(task)!;return (JsonObject)panel.GetType().GetProperty("Data",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(panel)!;}
var clock=Stopwatch.StartNew();var initial=await Panel();clock.Stop();
bool seed=initial["rows"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="app:fixture");
bool loading=initial["rows"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="apps-loading");
completionType.GetMethod("SetResult")!.Invoke(completion,new object[]{catalog});var refreshed=await Panel();
bool updated=!refreshed["rows"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="apps-loading");
var checks=new Dictionary<string,bool>{{"pending-shell-catalog-does-not-block-first-panel",clock.ElapsedMilliseconds<250},{"first-panel-retains-known-app-and-loading-state",seed&&loading},{"completed-catalog-refreshes-without-reopening",updated},{"initial-panel-can-display-all-apps",initial["appsView"]!.GetValue<string>()=="all"},{"refresh-retains-foreground-owner",refreshed["hostPid"]!.GetValue<int>()==Environment.ProcessId}};
var many=Array.CreateInstance(entry,47);for(int i=0;i<47;i++)many.SetValue(Activator.CreateInstance(entry,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{"app:"+i,"Owned app "+i,"C:/Owned/App"+i+".exe",""},null),i);
var ready=Activator.CreateInstance(completionType)!;completionType.GetMethod("SetResult")!.Invoke(ready,new object[]{many});Set("appScan",completionType.GetProperty("Task")!.GetValue(ready)!);
foreach(var (width,height,expected) in new[]{(824d,510d,18),(760d,490d,18),(620d,390d,10),(420d,320d,3),(320d,250d,2)}){
 Set("appsWidth",width);Set("appsHeight",height);Set("appsPage",0);var first=await Panel();int capacity=first["appsPageSize"]!.GetValue<int>(),pages=first["pages"]!.GetValue<int>();var observed=new List<string>();bool full=true;
 for(int page=0;page<pages;page++){Set("appsPage",page);var panel=await Panel();var apps=panel["rows"]!.AsArray().Where(r=>r!["id"]!.GetValue<string>().StartsWith("app:")).ToArray();full&=apps.Length==(page==pages-1?47-page*capacity:capacity);observed.AddRange(apps.Select(r=>r!["id"]!.GetValue<string>()));}
 checks[$"page-count-agrees-with-dimensions-{width}-{height}"]=capacity==expected&&pages==(47+expected-1)/expected;
 checks[$"every-app-offered-once-in-full-pages-{width}-{height}"]=full&&observed.Count==47&&observed.Distinct().Count()==47;
}
File.WriteAllText(args[1],JsonSerializer.Serialize(new{allPass=checks.Values.All(x=>x),checks,firstPanelMs=clock.Elapsed.TotalMilliseconds,scope="Actual panel builder with a deferred catalog; no HWND, backend, settings or desktop access"},new JsonSerializerOptions{WriteIndented=true}));
return checks.Values.All(x=>x)?0:1;
