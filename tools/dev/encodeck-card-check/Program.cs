using MagicCommunity;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

var checks=new Dictionary<string,bool>();
void Check(string name,bool value){checks[name]=value;if(!value)throw new InvalidOperationException(name);}
JsonElement Parse(string json){using var d=JsonDocument.Parse(json);return d.RootElement.Clone();}
JsonElement State(int level=50,bool ready=true)=>JsonSerializer.SerializeToElement(new{available=true,deviceName="Owned earphone fixture",deviceConnected=true,controlReady=ready,canSetNoise=true,noiseMode="NoiseCancellation",noiseStale=false,left=new{level,charging=false,stale=false},right=new{level=60,charging=true,stale=false},@case=new{level=70,charging=false,stale=true},modes=new[]{new{mode="NoiseCancellation",label="降噪"},new{mode="Transparency",label="通透"}},levels=new[]{new{index=3,label="舒适",selected=true}}});
async Task Wait(Func<bool> test,int timeout=3000){long start=Environment.TickCount64;while(!test()){if(Environment.TickCount64-start>timeout)throw new TimeoutException();await Task.Delay(10);}}
JsonElement Stats(CompositionEncoDeckCardData client)=>JsonSerializer.SerializeToElement(client.Diagnostics);
async Task RejectLine(string text,string name){bool denied=false;try{await new CompositionEncoDeckCardData.BoundedLines(new StringReader(text)).Read(CancellationToken.None);}catch(Exception error)when(error is IOException or InvalidDataException){denied=true;}Check(name,denied);}
void RejectState(string json,string name){bool denied=false;try{CompositionEncoDeckCardData.Normalize(Parse(json));}catch(Exception error)when(error is InvalidDataException or InvalidOperationException or KeyNotFoundException){denied=true;}Check(name,denied);}
NamedPipeServerStream Server(string name)=>new(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,4096,4096);
async Task Pair(NamedPipeServerStream server,CompositionEncoDeckCardData client){
 using var deadline=new CancellationTokenSource(3000);var accept=server.WaitForConnectionAsync(deadline.Token);client.Demand(true);await accept;using(var initial=Writer(server))await initial.WriteLineAsync(JsonSerializer.Serialize(new{version=1,type="state",data=State()}));await Wait(()=>client.IsConnected);
}
StreamReader Reader(Stream stream)=>new(stream,new UTF8Encoding(false,true),false,1024,true);
StreamWriter Writer(Stream stream)=>new(stream,new UTF8Encoding(false,true),1024,true){AutoFlush=true};
async Task<string> Line(StreamReader r)=>await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))??throw new EndOfStreamException();
try{
 var safe=CompositionEncoDeckCardData.Normalize(State());
 Check("native-state-whitelist",!safe.TryGetProperty("deviceId",out _)&&safe.GetProperty("left").GetProperty("level").GetInt32()==50);
 Check("state-choice-current-mode",CompositionEncoDeckCardData.Allowed(safe,"setNoiseMode",1));
 Check("state-choice-reject-not-reported-mode",!CompositionEncoDeckCardData.Allowed(safe,"setNoiseMode",2));
 Check("state-choice-explicit-level-index",CompositionEncoDeckCardData.Allowed(safe,"setNoiseOption",3));
 Check("state-choice-reject-array-position",!CompositionEncoDeckCardData.Allowed(safe,"setNoiseOption",0));
 Check("state-choice-no-arbitrary-action",!CompositionEncoDeckCardData.Allowed(safe,"execute",0));
 Check("state-choice-refresh-no-arguments",CompositionEncoDeckCardData.Allowed(safe,"refresh",null)&&!CompositionEncoDeckCardData.Allowed(safe,"refresh",0));
 Check("state-control-readiness-enforced",!CompositionEncoDeckCardData.Allowed(CompositionEncoDeckCardData.Normalize(State(50,false)),"setNoiseMode",0));
 Check("unknown-battery-minus-one",CompositionEncoDeckCardData.Normalize(Parse("""{"left":{"level":-1}}""")).GetProperty("left").GetProperty("level").ValueKind==JsonValueKind.Null);
 Check("missing-state-safe-defaults",!CompositionEncoDeckCardData.Normalize(Parse("{}")).GetProperty("available").GetBoolean());
 RejectState("[]","state-root-array-rejected");
 RejectState("""{"modes":[false]}""","state-mode-type-rejected");
 RejectState("""{"levels":[{"index":256,"label":"bad"}]}""","state-level-bound");
 RejectState("""{"levels":[{"index":1},{"index":1}]}""","state-level-duplicate");
 RejectState("""{"modes":[{"mode":"Off"},{"mode":"Off"}]}""","state-mode-duplicate");
 RejectState("""{"available":true,"available":false}""","state-duplicate-field-rejected");
 RejectState("""{"left":{"level":101}}""","state-battery-bound");
 RejectState("""{"left":{"level":"100"}}""","state-battery-string-rejected");
 RejectState("""{"available":"true"}""","state-boolean-type-rejected");
 RejectState("""{"deviceName":"unsafe\nlabel"}""","state-control-characters-rejected");
 RejectState(JsonSerializer.Serialize(new{deviceName=new string('x',81)}),"state-label-bound");
 var extra=CompositionEncoDeckCardData.Normalize(Parse("""{"deviceId":"hidden","bluetoothAddress":"private","deviceName":"ok"}"""));
 Check("external-internal-fields-dropped",!extra.TryGetProperty("deviceId",out _)&&!extra.TryGetProperty("bluetoothAddress",out _));
 var lines=new CompositionEncoDeckCardData.BoundedLines(new StringReader("one\r\ntwo\n"));Check("bounded-reader-preserves-next-frame",await lines.Read(CancellationToken.None)=="one"&&await lines.Read(CancellationToken.None)=="two");
 Check("bounded-reader-clean-eof",await lines.Read(CancellationToken.None)==null);
 Check("bounded-reader-exact-limit",(await new CompositionEncoDeckCardData.BoundedLines(new StringReader(new string('a',8192)+"\n")).Read(CancellationToken.None))!.Length==8192);
 await RejectLine(new string('a',8193)+"\n","oversize-frame-rejected");
 await RejectLine(new string('\r',8193)+"\n","carriage-return-budget-enforced");
 await RejectLine("{}","truncated-frame-rejected");
 string name="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 var updates=new ConcurrentQueue<JsonElement>();
 using(var server=Server(name))
 using(var client=new CompositionEncoDeckCardData(value=>{updates.Enqueue(value);return Task.CompletedTask;},name)){
  client.Demand(false);await Task.Delay(25);Check("no-visible-card-no-connection",Stats(client).GetProperty("attempts").GetInt64()==0);
  await Pair(server,client);using var r=Reader(server);using var w=Writer(server);
  Check("owned-pipe-subscription",(Parse(await Line(r))).GetProperty("op").GetString()=="subscribe");
  string state=JsonSerializer.Serialize(new{type="state",data=State()});
  await w.WriteLineAsync(state);await Wait(()=>updates.Any(x=>x.TryGetProperty("available",out var a)&&a.GetBoolean()));
  for(int i=0;i<50;i++)await w.WriteLineAsync(state);
  await Wait(()=>Stats(client).GetProperty("duplicates").GetInt64()>=50);
  Check("duplicate-state-not-redrawn",updates.Count<=2);
  Check("invalid-choice-never-sent",!await client.SendActionAsync("setNoiseMode",5,CancellationToken.None));
  var action=client.SendActionAsync("setNoiseMode",1,CancellationToken.None);
  var command=Parse(await Line(r));Check("supported-mode-fixed-command",command.GetProperty("action").GetString()=="setNoiseMode"&&command.GetProperty("choice").GetInt32()==1);
  Check("pending-command-limit",!await client.SendActionAsync("refresh",null,CancellationToken.None));
  await w.WriteLineAsync(JsonSerializer.Serialize(new{type="result",id=command.GetProperty("id").GetInt64(),ok=true}));
  Check("matching-result-completes",await action);
  await Task.Delay(1050);int before=updates.Count;
  for(int i=0;i<50;i++)await w.WriteLineAsync(JsonSerializer.Serialize(new{type="state",data=State(i)}));
  await Wait(()=>updates.Any(x=>x.TryGetProperty("left",out var l)&&l.GetProperty("level").GetInt32()==49));
  Check("burst-coalesced-to-bounded-paint",updates.Count-before<=4);
  Check("latest-state-retained",updates.Last().GetProperty("left").GetProperty("level").GetInt32()==49);
  var pending=client.SendActionAsync("refresh",null,CancellationToken.None);_ = await Line(r);client.Demand(false);
  Check("hidden-card-cancels-pending",!await pending.WaitAsync(TimeSpan.FromSeconds(1)));
  await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());int hidden=updates.Count;await Task.Delay(60);
  Check("hidden-card-no-late-state",updates.Count==hidden&&!client.IsConnected);
 }
 string badName="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var server=Server(badName))
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,badName)){
  await Pair(server,client);using var r=Reader(server);using var w=Writer(server);_ = await Line(r);
  await w.WriteLineAsync("""{"type":"state","data":{"modes":[true]}}""");
  await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  for(int i=0;i<30;i++)client.Demand(true);
  await Task.Delay(60);Check("malformed-state-isolated-no-snapshot-retry",Stats(client).GetProperty("attempts").GetInt64()==1&&!client.IsConnected);
 }
 string absent="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,absent)){
  client.Demand(true);await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  for(int i=0;i<30;i++)client.Demand(true);await Task.Delay(50);
  Check("failed-provider-no-background-reconnect",Stats(client).GetProperty("attempts").GetInt64()==1);
  client.Retry();Check("explicit-retry-allowed",Stats(client).GetProperty("attempts").GetInt64()==2);
  client.Dispose();await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  Check("dispose-releases-connection",!client.IsConnected);
 }
 string rapid="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,rapid)){
  client.Demand(true);client.Demand(false);client.Demand(true);
  await Wait(()=>Stats(client).GetProperty("attempts").GetInt64()==2&&!Stats(client).GetProperty("active").GetBoolean());
  Check("rapid-hide-show-starts-only-new-generation",Stats(client).GetProperty("attempts").GetInt64()==2);
 }
 string timeoutName="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var server=Server(timeoutName))
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,timeoutName)){
  await Pair(server,client);using var r=Reader(server);_ = await Line(r);
  var timer=System.Diagnostics.Stopwatch.StartNew();var action=client.SendActionAsync("refresh",null,CancellationToken.None);_ = await Line(r);
  Check("unresponsive-action-has-five-second-bound",!await action.WaitAsync(TimeSpan.FromSeconds(6))&&timer.ElapsedMilliseconds<5800);
 }
 string reasonPipe="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 JsonElement reported=default;
 using(var client=new CompositionEncoDeckCardData(value=>{reported=value;return Task.CompletedTask;},reasonPipe,()=>JsonSerializer.SerializeToElement(new{available=false,programRunning=true,programInstalled=true,providerStatus="running-no-bridge"}))) {
  client.Demand(true);await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  Check("running-process-not-confused-with-ipc-availability",reported.GetProperty("programRunning").GetBoolean()&&!reported.GetProperty("available").GetBoolean()&&reported.GetProperty("providerStatus").GetString()=="running-no-bridge");
 }
 string wrongPeer="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var server=Server(wrongPeer))
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,wrongPeer,acceptPeer:pid=>false)) {
  var accept=server.WaitForConnectionAsync();client.Demand(true);await accept;await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  Check("unexpected-server-identity-rejected",!client.IsConnected);
 }
 string noState="Magic.EncoDeck.Check."+Guid.NewGuid().ToString("N");
 using(var server=Server(noState))
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask,noState)) {
  var accept=server.WaitForConnectionAsync();var watch=System.Diagnostics.Stopwatch.StartNew();client.Demand(true);await accept;
  using var r=Reader(server);using var w=Writer(server);_ = await Line(r);
  Check("pipe-connect-is-not-state-readiness",!client.IsConnected);
  for(int i=0;i<35&&Stats(client).GetProperty("active").GetBoolean();i++){
   try{await w.WriteLineAsync("{\"version\":1,\"type\":\"keepalive\"}");}catch(IOException){break;}await Task.Delay(100);
  }
  await Wait(()=>!Stats(client).GetProperty("active").GetBoolean());
  Check("junk-frames-cannot-extend-first-state-deadline",!client.IsConnected&&watch.ElapsedMilliseconds<4400);
 }
 using(var client=new CompositionEncoDeckCardData(value=>Task.CompletedTask)) {
  string? scopedName=(string?)typeof(CompositionEncoDeckCardData).GetField("pipeName",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(client);
  Check("default-pipe-namespaced-by-windows-session",scopedName=="EncoDeck.DesktopCard.v1.s"+System.Diagnostics.Process.GetCurrentProcess().SessionId);
 }
 File.WriteAllText(args[0],JsonSerializer.Serialize(new{allPass=true,count=checks.Count,checks,scope="Owned private pipe and offline data only. No Bluetooth operations or real EncoDeck commands."},new JsonSerializerOptions{WriteIndented=true}));return 0;
}catch(Exception error){
 File.WriteAllText(args[0],JsonSerializer.Serialize(new{allPass=false,count=checks.Count,checks,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;
}

