using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession
{
    private void QueueExtensionProjection(){
        if(stop.IsCancellationRequested||Volatile.Read(ref closing)!=0||Interlocked.Exchange(ref extensionProjectionQueued,1)!=0)return;
        ui.BeginInvoke(new Action(async()=>{
            Volatile.Write(ref extensionProjectionQueued,0);if(stop.IsCancellationRequested||CurrentView.ValueKind!=JsonValueKind.Object)return;
            try{await Publish(JsonNode.Parse(CurrentView.GetRawText())!.AsObject(),stop.Token,projectionOnly:true);}catch(Exception e)when(e is not OutOfMemoryException){log("toolbar_extension_projection_failed",new{type=e.GetType().Name});}
        }));
    }
    private void ExtensionRenderState(JsonElement command){
        string id=Text(command,"id");var row=Offered(id);if(row is not {} offered||Text(offered,"type")!="Extension")return;
        if(!long.TryParse(Text(command,"extensionRevision"),NumberStyles.None,CultureInfo.InvariantCulture,out long revision))return;
        long window=(long)Number(command,"surfaceWindow");if(window<=0)return;
        extensions.Report(Text(offered,"nativeExtension"),Text(command,"extensionSession"),revision,window,Flag(command,"mounted"),Flag(command,"mounted")&&Flag(command,"visible"));
    }
    private async Task ExtensionInvoke(JsonElement command){
        try{
            var row=Offered(Text(command,"id"));if(row is not {} offered||Text(offered,"type")!="Extension")throw new InvalidOperationException("扩展已离线。");
            if(!long.TryParse(Text(command,"extensionRevision"),NumberStyles.None,CultureInfo.InvariantCulture,out long revision))throw new InvalidOperationException("扩展版本已变化。");
            await extensions.Invoke(Text(offered,"nativeExtension"),Text(command,"extensionSession"),revision,Text(command,"extensionAction"));
        }catch(Exception e)when(e is not OutOfMemoryException){if(!stop.IsCancellationRequested)await Notice(e);}
    }
    private async Task ToggleExtension(string id){
        if(!Magic.ToolbarExtension.ToolbarProtocol.ValidId(id))throw new InvalidOperationException("扩展标识无效。");
        if(extensions.Enabled(id)){extensions.SetEnabled(id,false);return;}
        await configurationGate.WaitAsync(stop.Token);
        try{
            var read=await backend.Request("configuration-read",new{},stop.Token);var config=JsonNode.Parse(read.GetProperty("configuration").GetRawText())!.AsObject();var groups=config["toolbar"]!["sections"]!.AsObject();
            bool present=new[]{"left","center","right"}.SelectMany(s=>groups[s]!.AsArray()).Any(v=>v?["kind"]?.GetValue<string>()=="compatibility"&&v?["sourceModuleId"]?.GetValue<string>()==ToolbarExtensions.SourcePrefix+id);
            if(!present){groups["center"]!.AsArray().Add(new JsonObject{["id"]=ToolbarExtensions.RowId(id),["kind"]="compatibility",["displayName"]=extensions.Label(id),["sourceModuleId"]=ToolbarExtensions.SourcePrefix+id});await SaveConfiguration(config);}
            extensions.SetEnabled(id,true);
        }finally{configurationGate.Release();}
    }
}
