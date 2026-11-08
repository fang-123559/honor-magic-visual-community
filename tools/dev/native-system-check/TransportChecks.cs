using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using MagicCommunity;

internal static class TransportChecks
{
    internal static async Task Run(string output)
    {
        var checks=new Dictionary<string,bool>();
        void Check(string name,bool value){Console.WriteLine(name+"="+value);checks[name]=value;if(!value)throw new InvalidOperationException(name);}
        using var process=Process.GetCurrentProcess();
        string name="MagicOwnedTransport-"+Guid.NewGuid().ToString("N");
        var owner=(NativeSystemClient)Activator.CreateInstance(typeof(NativeSystemClient),BindingFlags.NonPublic|BindingFlags.Instance,null,[process,name],null)!;
        object Field(string field)=>typeof(NativeSystemClient).GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(owner)!;
        using var control=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var devices=new NamedPipeServerStream(name+"-devices",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var bluetooth=new NamedPipeServerStream(name+"-bluetooth",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var urgent=new NamedPipeServerStream(name+"-urgent",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        async Task<JsonElement> Read(Stream stream){byte[] size=new byte[4];await stream.ReadExactlyAsync(size);byte[] data=new byte[BitConverter.ToInt32(size)];await stream.ReadExactlyAsync(data);return JsonSerializer.Deserialize<JsonElement>(data);}
        async Task Reply(Stream stream,JsonElement request){byte[] data=JsonSerializer.SerializeToUtf8Bytes(new{protocol=NativeSystemClient.Protocol,id=request.GetProperty("id").GetString(),ok=true,result=new{accepted=true}});await stream.WriteAsync(BitConverter.GetBytes(data.Length));await stream.WriteAsync(data);await stream.FlushAsync();}
        try{
            await Task.WhenAll(control.WaitForConnectionAsync(),((NamedPipeClientStream)Field("control")).ConnectAsync(2000)).WaitAsync(TimeSpan.FromSeconds(3));
            async Task Healthy(){var work=owner.Request("configuration-read",new{});var request=await Read(control).WaitAsync(TimeSpan.FromSeconds(1));await Reply(control,request);await work.WaitAsync(TimeSpan.FromSeconds(1));}
            foreach(var (server,operation,next) in new[]{(devices,"wifi-read","wifi-read"),(bluetooth,"bluetooth-read","bluetooth-read"),(urgent,"window-action","health-fast")}){
                using var deadline=new CancellationTokenSource();
                var connected=server.WaitForConnectionAsync();var slow=owner.Request(operation,new{},deadline.Token);
                await connected.WaitAsync(TimeSpan.FromSeconds(2));
                var request=await Read(server).WaitAsync(TimeSpan.FromSeconds(2));
                Check(operation+"-independent-pipe",request.GetProperty("operation").GetString()==operation);
                await Healthy();Check(operation+"-does-not-block-control",!slow.IsCompleted);
                deadline.Cancel();bool rejected=false;try{await slow;}catch(NativeSystemRequestException){rejected=true;}
                Check(operation+"-scoped-timeout",rejected);
                await Healthy();Check(operation+"-control-survives-timeout",true);
                server.Disconnect();connected=server.WaitForConnectionAsync();
                // No implicit resend after disconnect, including a mutation.
                await Task.Delay(60);Check(operation+"-not-replayed",!connected.IsCompleted);
                var again=owner.Request(next,new{});await connected.WaitAsync(TimeSpan.FromSeconds(2));
                request=await Read(server).WaitAsync(TimeSpan.FromSeconds(1));
                Check(operation+"-explicit-request-reconnects",request.GetProperty("operation").GetString()==next);
                await Reply(server,request);await again.WaitAsync(TimeSpan.FromSeconds(1));
                Check(operation+"-fresh-response-accepted",true);
            }
            Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{allPass=true,checks,scope="Owned private authenticated pipe fixtures only; no backend, radios or desktop actions."},new JsonSerializerOptions{WriteIndented=true}));
        }finally{foreach(string field in new[]{"control","events","urgent","wifi","bluetooth"})((IDisposable)Field(field)).Dispose();}
    }
}
