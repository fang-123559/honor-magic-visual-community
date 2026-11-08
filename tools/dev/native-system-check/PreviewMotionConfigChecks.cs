using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagicCommunity;

internal static class PreviewMotionConfigChecks
{
    public static async Task<int> Run(string exe,string hash,string output)
    {
        Directory.CreateDirectory(output);string fixture=Path.Combine(output,"owned-configuration");Directory.CreateDirectory(fixture);
        var checks=new Dictionary<string,bool>();void Check(string name,bool pass){checks[name]=pass;if(!pass)throw new InvalidDataException(name);}
        var config=JsonNode.Parse("""{"protocol":"Magic.System.Configuration/1","schemaVersion":1,"revision":1,"toolbar":{"locked":false,"sections":{"left":[],"center":[],"right":[]},"appearance":{}},"dock":{"locked":false,"sections":{"left":[],"center":[],"right":[]},"appearance":{}},"preferences":{}}""")!.AsObject();
        await using(var backend=await NativeSystemClient.Start(exe,hash,CancellationToken.None,fixture)){
            await backend.Request("configuration-write",new{expectedRevision=0,configuration=config});Check("old-config-without-tuning-remains-valid",true);
            config["revision"]=2;config["dock"]!["previewMotion"]=JsonNode.Parse("""{"duration":150,"scale":0.88,"shift":8}""");
            await backend.Request("configuration-write",new{expectedRevision=1,configuration=config});var read=await backend.Request("configuration-read",new{});
            Check("dock-tuning-roundtrips",read.GetProperty("configuration").GetProperty("dock").GetProperty("previewMotion").GetProperty("scale").GetDouble()==.88);
            config["revision"]=3;config["dock"]!["previewMotion"]=JsonNode.Parse("""{"duration":170}""");await backend.Request("configuration-write",new{expectedRevision=2,configuration=config});Check("partial-tuning-supported",true);
            string file=Path.Combine(fixture,"configuration.json");byte[] before=File.ReadAllBytes(file);
            foreach(string tuning in new[]{"{\"duration\":79}","{\"duration\":150.5}","{\"scale\":1.1}","{\"shift\":-1}","{\"duration\":\"150\"}","{\"unknown\":1}"}){
                var bad=config.DeepClone();bad["revision"]=4;bad["dock"]!["previewMotion"]=JsonNode.Parse(tuning);bool rejected=false;try{await backend.Request("configuration-write",new{expectedRevision=3,configuration=bad});}catch(NativeSystemRequestException){rejected=true;}
                Check("reject-without-mutation-"+tuning,rejected&&before.SequenceEqual(File.ReadAllBytes(file)));
            }
            var toolbar=config.DeepClone();toolbar["revision"]=4;toolbar["toolbar"]!["previewMotion"]=JsonNode.Parse("{}");bool denied=false;try{await backend.Request("configuration-write",new{expectedRevision=3,configuration=toolbar});}catch(NativeSystemRequestException){denied=true;}Check("tuning-stays-dock-only",denied&&before.SequenceEqual(File.ReadAllBytes(file)));
        }
        var settings=typeof(NativeSystemPresentation).GetMethod("Settings",BindingFlags.Static|BindingFlags.NonPublic)!;
        using var first=JsonDocument.Parse("""{"locked":false,"appearance":{},"previewMotion":{"duration":170}}""");using var second=JsonDocument.Parse("""{"locked":false,"appearance":{},"previewMotion":{"duration":190}}""");
        var a=(JsonObject)settings.Invoke(null,[first.RootElement,true])!;var b=(JsonObject)settings.Invoke(null,[second.RootElement,true])!;var bar=(JsonObject)settings.Invoke(null,[first.RootElement,false])!;
        Check("adapter-forwards-hidden-tuning",a["previewMotion"]!["duration"]!.GetValue<int>()==170);
        Check("tuning-is-static-and-does-not-change-settings-signature",a.ToJsonString()==b.ToJsonString());Check("toolbar-settings-unaffected",!bar.ContainsKey("previewMotion"));
        File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{allPass=true,count=checks.Count,checks,formalDesktopLoaded=false,realConfigurationChanged=false},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
