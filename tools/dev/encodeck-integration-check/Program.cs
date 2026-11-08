using MagicCommunity;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
var checks=new Dictionary<string,bool>();
void Check(string name,bool ok){checks[name]=ok;if(!ok)throw new InvalidOperationException(name);}
async Task Wait(Func<bool> ready,int timeout=4000){var watch=Stopwatch.StartNew();while(!ready()){if(watch.ElapsedMilliseconds>timeout)throw new TimeoutException();await Task.Delay(20);}}
string directory=Path.GetFullPath(args[0]),fixture=Path.Combine(directory,"fixture","EncoDeck.exe"),report=Path.Combine(directory,"result.json"),pipe="Magic.Owned.EncoDeck."+Guid.NewGuid().ToString("N");
string settingsRoot=Path.Combine(directory,"settings");Directory.CreateDirectory(Path.Combine(settingsRoot,"data"));
string settings=Path.Combine(settingsRoot,"data","encodeck.json");
var app=new EncoDeckIntegration(settingsRoot,pipe);
var owned=new HashSet<int>();
void Reject(string path,string name){bool rejected=false;try{EncoDeckIntegration.ValidateExecutable(path);}catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException){rejected=true;}Check(name,rejected);}
try{
 File.WriteAllText(Path.Combine(directory,"fixture","pipe-name.txt"),pipe);
 foreach(var (path,name) in new[]{("EncoDeck.exe","relative-denied"),(@"\\server\share\EncoDeck.exe","unc-denied"),(fixture+":stream","ads-denied"),(fixture+"\n","control-character-denied"),(Path.Combine(directory,"missing","EncoDeck.exe"),"missing-file-denied"),(Environment.ProcessPath!,"other-product-denied")})Reject(path,name);
 Check("canonical-local-file",EncoDeckIntegration.ValidateExecutable(fixture)==fixture);
 app.Configure(fixture);Check("path-config-roundtrip",app.ConfiguredPath()==fixture);
 Check("installed-not-running",app.Locate().Status=="installed-not-running");
 Check("unavailable-separate-installed-running",app.Unavailable().GetProperty("programInstalled").GetBoolean()&&!app.Unavailable().GetProperty("programRunning").GetBoolean());
 string prior=File.ReadAllText(settings);try{app.Configure("invalid");}catch(InvalidDataException){}Check("invalid-selection-preserves-settings",File.ReadAllText(settings)==prior);
 string launch=await app.Perform(false,CancellationToken.None);await Wait(()=>app.Locate().Running);var first=app.Locate();owned.Add(first.ProcessId);
 Check("only-configured-path-discovered",first.Path==fixture&&first.Running&&first.Created>0);
 Check("running-without-bridge-is-distinct",app.Unavailable().GetProperty("providerStatus").GetString()=="running-no-bridge");
 await Wait(()=>File.Exists(Path.Combine(directory,"fixture","launches.log")));
 Check("fixed-main-page-arguments",File.ReadAllText(Path.Combine(directory,"fixture","launches.log")).StartsWith("--page|Home\n",StringComparison.Ordinal));
 await app.Perform(false,CancellationToken.None);await Wait(()=>File.Exists(Path.Combine(directory,"fixture","commands.log")));
 Check("open-main-uses-authenticated-single-instance-pipe",File.ReadAllText(Path.Combine(directory,"fixture","commands.log")).Contains("--page|Home"));
 string restarted=await app.Perform(true,CancellationToken.None);await Wait(()=>app.Locate().Running&&app.Locate().ProcessId!=first.ProcessId);var next=app.Locate();owned.Add(next.ProcessId);
 Check("graceful-restart-exits-original",Process.GetProcessesByName("EncoDeck").All(p=>{using(p)return p.Id!=first.ProcessId;}));
 Check("restart-new-creation-identity",next.Created!=first.Created&&next.Path==first.Path);
 await Wait(()=>File.ReadAllLines(Path.Combine(directory,"fixture","launches.log")).Length==2);
 Check("fixed-background-restart-argument",File.ReadAllLines(Path.Combine(directory,"fixture","launches.log"))[1]=="--background");
 File.WriteAllText(Path.Combine(directory,"fixture","ignore-exit.txt"),"owned test only");
 var slow=app.Perform(true,CancellationToken.None);await Task.Delay(100);
 Check("concurrent-request-coalesced",(await app.Perform(false,CancellationToken.None)).Contains("上一次操作"));
 string slowResult=await slow;Check("normal-exit-timeout-no-force-kill",slowResult.Contains("未强制")&&app.Locate().ProcessId==next.ProcessId);
 File.Delete(Path.Combine(directory,"fixture","ignore-exit.txt"));
 using(var client=new NamedPipeClientStream(".",pipe,PipeDirection.Out,PipeOptions.Asynchronous)){
  await client.ConnectAsync(1500);await client.WriteAsync(Encoding.UTF8.GetBytes("--exit"));
 }
 await Wait(()=>!app.Locate().Running);
 File.WriteAllText(Path.Combine(directory,"fixture","no-pipe.txt"),"owned test only");
 using var decoy=Process.Start(new ProcessStartInfo(fixture){UseShellExecute=false})!;owned.Add(decoy.Id);
 await Wait(()=>app.Locate().Running);
 using(var foreign=new NamedPipeServerStream(pipe,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly)){
  var accept=foreign.WaitForConnectionAsync();var action=app.Perform(false,CancellationToken.None);await accept;
  string failed=await action;Check("wrong-actual-server-pid-rejected",failed.Contains("不属于当前实例"));
  Check("rejected-peer-gets-no-command",foreign.ReadByte()==-1);
 }
 decoy.Kill();await decoy.WaitForExitAsync();
 foreach(var (json,label) in new[]{("{\"schema\":99,\"executable\":\"x\"}","unknown-config-schema-safe"),("{\"schema\":\"bad\"}","malformed-config-safe"),(new string('x',8200),"oversized-config-safe")}){File.WriteAllText(settings,json);Check(label,app.Locate().Status=="invalid-path"&&File.ReadAllText(settings)==json);}
 File.WriteAllText(report,JsonSerializer.Serialize(new{allPass=true,count=checks.Count,checks,scope="Owned fake EncoDeck and private activation pipe only; no actual EncoDeck command or Bluetooth access."},new JsonSerializerOptions{WriteIndented=true}));return 0;
}catch(Exception error){File.WriteAllText(report,JsonSerializer.Serialize(new{allPass=false,count=checks.Count,checks,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
finally{foreach(int id in owned)try{using var p=Process.GetProcessById(id);if(EncoDeckIntegration.ImagePath(id).Equals(fixture,StringComparison.OrdinalIgnoreCase)){p.Kill();p.WaitForExit();}}catch(ArgumentException){}catch(InvalidOperationException){}}
