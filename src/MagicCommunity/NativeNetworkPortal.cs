using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
// One bounded probe for a newly observed connected network lacking Internet.
// No periodic task, network scan, provider-specific credentials or redirect fetch.
internal sealed class NativeNetworkPortal : IDisposable
{
    internal const string AuthenticationUri="http://www.msftconnecttest.com/redirect";
    private readonly object gate=new();private readonly Action changed;private readonly Action<string> open;private readonly Func<CancellationToken,Task<string>>? ownedProbe;
    private readonly CancellationTokenSource stop=new();private readonly CancellationToken cancellation;private readonly Dictionary<string,string> results=new(StringComparer.Ordinal);private readonly HashSet<string> seen=new(StringComparer.Ordinal);private readonly Queue<string> pending=new();private HashSet<string> connected=new(StringComparer.Ordinal);private bool running,disposed;
    private readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=8192};
    internal NativeNetworkPortal(Action changed,Action<string> open,Func<CancellationToken,Task<string>>? ownedProbe=null){this.ownedProbe=ownedProbe;this.changed=changed;this.open=open;cancellation=stop.Token;}
    internal static string Key(JsonElement item){string adapter=Text(item,"id"),ssid=Text(item,"ssidHex");return adapter.Length>0&&ssid.Length is >0 and <=64&&ssid.All(Uri.IsHexDigit)?adapter+"/"+ssid:"";}
    internal static string Classify(HttpStatusCode status,Uri? location,string body){
        if((int)status is >=300 and <=399&&location is not null&&(location.IsAbsoluteUri?location.Scheme is "http" or "https":!location.OriginalString.StartsWith("//",StringComparison.Ordinal)))return "portal";
        if(status==HttpStatusCode.OK&&body.Trim()=="Microsoft Connect Test")return "online";
        if(status==HttpStatusCode.OK&&(body.Contains("<form",StringComparison.OrdinalIgnoreCase)||body.Contains("captive",StringComparison.OrdinalIgnoreCase)))return "portal";
        return "unconfirmed";
    }
    internal void Observe(JsonElement wifi){
        var rows=Array(wifi,"interfaces").Where(i=>Number(i,"state")==1).Take(32).ToArray();bool start=false;
        lock(gate){if(disposed)return;connected=rows.Select(Key).Where(k=>k.Length>0).ToHashSet(StringComparer.Ordinal);
            foreach(var row in rows){string key=Key(row);if(key.Length==0)continue;
                if(Flag(row,"internetAvailable")){results.Remove(key);continue;}
                if(!Flag(row,"internetKnown"))continue;
                if(seen.Count>=64||pending.Count>=8||!seen.Add(key))continue;results[key]="checking";pending.Enqueue(key);
            }if(!running&&pending.Count>0){running=true;start=true;}
        }if(start)_=Run();
    }
    private async Task Run(){
        while(true){string key;lock(gate){if(disposed||pending.Count==0){running=false;return;}key=pending.Dequeue();if(!connected.Contains(key))continue;}
            string state="unconfirmed";
            try{if(ownedProbe is not null)state=await ownedProbe(cancellation);else{using var request=new HttpRequestMessage(HttpMethod.Get,"http://www.msftconnecttest.com/connecttest.txt");using var response=await http.SendAsync(request,HttpCompletionOption.ResponseContentRead,cancellation);string body=await response.Content.ReadAsStringAsync(cancellation);state=Classify(response.StatusCode,response.Headers.Location,body);}}
            catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException){}
            bool active;lock(gate){active=!disposed&&connected.Contains(key)&&results.ContainsKey(key);if(active)results[key]=state;}
            if(!active)continue;changed();
            if(state=="portal")try{open(AuthenticationUri);}catch(Exception e)when(e is not OutOfMemoryException){lock(gate)if(!disposed)results[key]="manual";changed();}
        }
    }
    internal NativeSystemPresentationSession.Published Enrich(NativeSystemPresentationSession.Published state){
        lock(gate)if(results.Count==0)return state;
        if(!state.Providers.TryGetValue("wifi",out var value))return state;var wifi=JsonNode.Parse(value.GetRawText())!.AsObject();
        if(wifi["interfaces"] is JsonArray interfaces)foreach(var node in interfaces.OfType<JsonObject>()){
            using var document=JsonDocument.Parse(node.ToJsonString());string key=Key(document.RootElement);string? status="";lock(gate)results.TryGetValue(key,out status);
            if(Flag(document.RootElement,"internetAvailable"))status="";
            node["portalState"]=status??"";node["needsAction"]=status is "portal" or "manual";node["portalHint"]=status is "portal" or "manual"?"需要操作，没有 Internet":status=="unconfirmed"?"没有 Internet，认证状态未确认":status=="checking"?"正在确认网络访问状态":"";
        }
        var providers=new Dictionary<string,JsonElement>(state.Providers){["wifi"]=JsonSerializer.SerializeToElement(wifi)};return state with{Providers=providers};
    }
    public void Dispose(){lock(gate){disposed=true;pending.Clear();results.Clear();connected.Clear();}stop.Cancel();http.Dispose();stop.Dispose();}
}
