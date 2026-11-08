using MagicCommunity;
using System.Diagnostics;
using System.Text.Json;

internal static class ProviderCycleChecks
{
    internal static async Task<int> Run(string executable, string hash, string report)
    {
        var observations = new List<object>(); var checks = new Dictionary<string, bool>();
        try
        {
            await using var backend = await NativeSystemClient.Start(executable, hash, CancellationToken.None);
            using var process = Process.GetProcessById(backend.ProcessId);
            foreach (string provider in new[] { "audio", "wifi", "media", "bluetooth" })
            {
                // Warm each API before recording steady-state cycle behavior.
                await backend.Request(provider + "-subscribe", new { enabled = true });
                await backend.Request(provider + "-subscribe", new { enabled = false }); await Task.Delay(1000);
                process.Refresh(); int baselineHandles = process.HandleCount; long baselinePrivate = process.PrivateMemorySize64;
                for (int batch = 0; batch < 4; batch++)
                {
                    for (int cycle = 0; cycle < 20; cycle++)
                    {
                        await backend.Request(provider + "-subscribe", new { enabled = true });
                        await backend.Request(provider + "-subscribe", new { enabled = false });
                    }
                    await Task.Delay(1000); process.Refresh(); var health = await backend.Request(provider + "-health", new { });
                    observations.Add(new { provider, completedCycles = (batch + 1) * 20, handleDelta = process.HandleCount - baselineHandles, privateDelta = process.PrivateMemorySize64 - baselinePrivate, handles = process.HandleCount, privateBytes = process.PrivateMemorySize64, health });
                    checks[provider + "-registrations-released-batch-" + batch] = provider switch
                    {
                        "audio" => health.GetProperty("deviceCallbacks").GetInt32() == 0 && health.GetProperty("volumeCallbacks").GetInt32() == 0,
                        "wifi" => health.GetProperty("callbacks").GetInt32() == 0 && health.GetProperty("cachedInterfaces").GetInt32() == 0,
                        "media" => health.GetProperty("managerCallbacks").GetInt32() == 0 && health.GetProperty("sessionCallbacks").GetInt32() == 0 && health.GetProperty("cachedSessions").GetInt32() == 0,
                        "bluetooth" => health.GetProperty("callbacks").GetInt32() == 0 && health.GetProperty("watchers").GetInt32() == 0,
                        _ => false
                    };
                }
                Console.WriteLine(JsonSerializer.Serialize(new { provider, completedCycles = 80 }));
            }
            await backend.Request("unsubscribe", new { }); process.Refresh(); var before = new { privateBytes = process.PrivateMemorySize64, handles = process.HandleCount };
            await Task.Delay(30000); process.Refresh(); var settled = new { privateBytes = process.PrivateMemorySize64, handles = process.HandleCount };
            var result = new { allPass = checks.Values.All(x => x), checks, observations, beforeIdle = before, after30SecondsIdle = settled, scope = "80 subscribe/unsubscribe cycles per public provider; no device actions or working-set trimming. Growth observations are measurements, not proof of leak-free behavior." };
            File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return result.allPass ? 0 : 1;
        }
        catch (Exception error) { File.WriteAllText(report, JsonSerializer.Serialize(new { allPass = false, checks, observations, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
    }
}
