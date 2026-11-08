using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagicCommunity;

internal static class InstalledFirstStartChecks
{
    internal static async Task<int> Run(string executable, string hash, string output)
    {
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        output = Path.GetFullPath(output);
        if (!output.StartsWith(Path.Combine(root,"artifacts") + "\\", StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
            throw new IOException("Use a fresh artifacts directory.");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        void Check(string name, bool pass) { checks.Add(new { name, pass }); if (!pass) throw new Exception(name); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var ct = deadline.Token;
        string profile = Path.Combine(output, "首次使用 （测试）");
        Directory.CreateDirectory(profile);
        string data = InstalledProfileDirectories.PrepareBackendDirectory(profile);
        Check("Fresh profile creates the backend directory", Directory.Exists(data));
        Check("Reinitializing a profile uses the same directory", data == InstalledProfileDirectories.PrepareBackendDirectory(profile));
        string configFile = Path.Combine(data, "configuration.json");
        await using var backend = await NativeSystemClient.Start(executable, hash, ct, data);
        var absent = await backend.Request("configuration-read", new {}, ct);
        Check("Actual backend accepts the empty profile before seeding", !absent.GetProperty("present").GetBoolean());
        int seeds = 0;
        JsonObject Defaults() { seeds++; return NativeSystemBarDefaults.CreateInstalledConfiguration(); }

        JsonObject firstView;
        await using (var session = new NativeSystemPresentationSession(backend, false, Defaults))
            firstView = await session.Initialize(ct);
        var monitors = firstView["monitors"]!.AsArray();
        Check("All detected displays project an enabled Dock and toolbar", monitors.Count > 0 && monitors.All(m => m!["toolbar"]!.GetValue<bool>() && m["dock"]!.GetValue<bool>()));
        Check("Published first toolbar includes clock and user modules", firstView["toolbar"]!.ToJsonString().Contains("magic.clock") && firstView["toolbar"]!.ToJsonString().Contains("magic.user"));
        Check("Published first Dock includes Start, Explorer and recycle bin", firstView["dock"]!.ToJsonString().Contains("magic.start") && firstView["dock"]!.ToJsonString().Contains("explorer.exe",StringComparison.OrdinalIgnoreCase) && firstView["dock"]!.ToJsonString().Contains("magic.recycle"));
        var read = await backend.Request("configuration-read", new {}, ct);
        Check("Validated native write initializes revision one exactly once", seeds == 1 && read.GetProperty("revision").GetInt32()==1 && File.Exists(configFile));
        var custom = JsonNode.Parse(read.GetProperty("configuration").GetRawText())!.AsObject();
        custom["revision"]=2;custom["dock"]!["appearance"]!["iconSize"]=46;
        await backend.Request("configuration-write",new { expectedRevision=1, configuration=custom },ct);
        var before = SHA256.HashData(File.ReadAllBytes(configFile));
        await using (var repeat = new NativeSystemPresentationSession(backend,false,Defaults)) await repeat.Initialize(ct);
        Check("Restart preserves existing configuration byte for byte", seeds==1 && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(configFile))));
        // Verify the error path with this fixture alone. Never overwrite a damaged user configuration.
        File.WriteAllText(configFile,"{broken",System.Text.Encoding.UTF8);
        bool rejected=false;
        await using (var invalid = new NativeSystemPresentationSession(backend,false,Defaults))
            try { await invalid.Initialize(ct); } catch (NativeSystemRequestException) { rejected=true; }
        Check("Damaged configuration is reported and preserved", rejected && seeds==1 && File.ReadAllText(configFile)=="{broken");
        string trace = InstalledProfileDirectories.PrepareTransitionDirectory(profile);
        Check("Animation marker stays under the selected profile", trace==Path.Combine(profile,"runtime","desktop") && Directory.Exists(trace));
        var report=new {allPass=true,checks,seedWrites=seeds,displays=monitors.Count,expectedBars=monitors.Count*2,scope="Real native backend IPC and bar projection with a new profile; no Explorer/taskbar takeover."};
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new {allPass=true,count=checks.Count,expectedBars=monitors.Count*2}));
        return 0;
    }
}
