using System.Diagnostics;
using System.Text.Json.Nodes;
using MagicCommunity;

internal static class SamplingChecks
{
    internal static async Task Run(string executable,string hash,string directory,Dictionary<string,bool> checks)
    {
        void Check(string name,bool pass){checks[name]=pass;if(!pass)throw new InvalidDataException(name);}
        string path=Path.Combine(directory,"sample-period-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        await using var backend=await NativeSystemClient.Start(executable,hash,CancellationToken.None,path);
        var config=JsonNode.Parse("""{"protocol":"Magic.System.Configuration/1","schemaVersion":1,"revision":1,"toolbar":{"locked":false,"sections":{"left":[{"id":"memory-sample","kind":"module","module":"magic.memory"},{"id":"cpu-sample","kind":"module","module":"magic.cpu"}],"center":[],"right":[]},"appearance":{}},"dock":{"locked":false,"sections":{"left":[],"center":[],"right":[]},"appearance":{}},"preferences":{}}""")!;
        await backend.Request("configuration-write",new{expectedRevision=0,configuration=config});
        await using var presentation=new NativeSystemPresentationSession(backend);
        await presentation.Initialize();long before=(await backend.Request("health",new{})).GetProperty("systemSamples").GetInt64();
        Check("resource-subscription-initial-snapshot-once",before==1);
        var clock=Stopwatch.StartNew();bool renewed=false;
        while(clock.ElapsedMilliseconds<2300){
            await presentation.Next(300);
            if(!renewed&&clock.ElapsedMilliseconds>1000){await backend.Request("subscribe",new{windows=true,system=true,gpu=false,storage=false,intervalMs=10000});renewed=true;}
        }
        long early=(await backend.Request("health",new{})).GetProperty("systemSamples").GetInt64();
        Check("resource-updates-not-one-second-polling",early==before);
        Check("unchanged-subscription-does-not-reset-resource-deadline",renewed&&early==before);
        long later=early;
        while(clock.ElapsedMilliseconds<11500&&later==before){await presentation.Next(1000);later=(await backend.Request("health",new{})).GetProperty("systemSamples").GetInt64();}
        Check("resource-update-arrives-at-ten-second-period",later==before+1&&clock.ElapsedMilliseconds>=8500);
        File.WriteAllText(Path.Combine(path,"timing.json"),System.Text.Json.JsonSerializer.Serialize(new{before,early,later,elapsedMs=clock.ElapsedMilliseconds,desktopChanged=false}));
    }
}
