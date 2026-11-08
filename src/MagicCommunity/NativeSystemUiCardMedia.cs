using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession{
    internal Action<object>? MediaCardChanged;
    private volatile bool mediaCardWanted;private int mediaCardQueue;private string lastMediaCard="";
    internal void MediaCardDemand(bool wanted){
        if(mediaCardWanted==wanted)return;mediaCardWanted=wanted;QueueMediaDemand();
    }
    private void QueueMediaDemand(){
        if(stop.IsCancellationRequested||Interlocked.CompareExchange(ref mediaCardQueue,1,0)!=0)return;
        _=Task.Run(async()=>{bool applied=mediaCardWanted;try{
            do{applied=mediaCardWanted;var view=await presentation.SetMediaCardDemand(applied,stop.Token);await Publish(view,stop.Token);}while(applied!=mediaCardWanted&&!stop.IsCancellationRequested);
        }catch(Exception e)when(e is not OutOfMemoryException){if(!stop.IsCancellationRequested)log("media_card_demand_failed",new{type=e.GetType().Name});}
        finally{Volatile.Write(ref mediaCardQueue,0);if(applied!=mediaCardWanted&&!stop.IsCancellationRequested)QueueMediaDemand();}
        });
    }
    private void EmitMediaCard(JsonElement? supplied=null){
        if(!mediaCardWanted){if(lastMediaCard.Length>0){lastMediaCard="";MediaCardChanged?.Invoke(new{available=true,hasSession=false});}return;}
        var provider=supplied??presentation.State.Providers.GetValueOrDefault("media");var media=Array(provider,"sessions").OrderByDescending(s=>Flag(s,"current")).FirstOrDefault();
        var data=JsonNode.Parse(media.ValueKind==JsonValueKind.Object?media.GetRawText():"{}")!.AsObject();data["available"]=Flag(provider,"available");data["hasSession"]=media.ValueKind==JsonValueKind.Object;
        string key=data.ToJsonString();if(key==lastMediaCard)return;lastMediaCard=key;data["observedTick"]=Environment.TickCount64;MediaCardChanged?.Invoke(data);
    }
    internal async Task MediaCardAction(string kind,string sessionId,string trackId,double positionMs){
        var state=await backend.Request("media-read",new{},stop.Token);if(kind=="media-retry"){EmitMediaCard(state);return;}var current=Array(state,"sessions").SingleOrDefault(s=>Text(s,"id")==sessionId&&Text(s,"trackId")==trackId);
        if(current.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("媒体或曲目已变化，请重新操作。");
        if(kind=="media-open"){await OpenMediaSource(JsonSerializer.SerializeToElement(new{nativeSessionId=sessionId}));return;}
        string action=kind switch{"media-previous"=>"previous","media-next"=>"next","media-toggle"=>"toggle","media-seek"=>"seek",_=>throw new InvalidDataException()};
        bool supported=action switch{"previous"=>Flag(current,"canPrevious"),"next"=>Flag(current,"canNext"),"seek"=>Flag(current,"canSeek"),_=>Flag(current,"canPlay")||Flag(current,"canPause")};
        if(!supported)throw new InvalidOperationException(action=="seek"?"当前媒体来源不支持调整播放进度。":"当前媒体来源不支持此操作。");
        var result=await backend.Request("media-action",new{action,sessionId,trackId,positionMs},stop.Token);if(!Flag(result,"accepted"))throw new InvalidOperationException("媒体来源未接受操作，请稍后重试。");
    }
}
