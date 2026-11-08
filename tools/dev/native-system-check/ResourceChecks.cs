using MagicCommunity;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class ResourceChecks
{
    internal static async Task<int> Run(string executable, string hash, string report)
    {
        var stages = new List<object>(); var checks = new Dictionary<string, bool>(); var errors = new List<string>();
        var samples = new List<Sample>(); int pid = 0;
        try
        {
            await using var backend = await NativeSystemClient.Start(executable, hash, CancellationToken.None);
            pid = backend.ProcessId; using var process = Process.GetProcessById(pid); using var gpu = new GpuProbe(pid);
            async Task Stage(string name, int seconds, bool demand)
            {
                var rows = new List<Sample>(); process.Refresh(); TimeSpan cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
                long cursor = (await backend.Request("health", new { })).GetProperty("revision").GetInt64();
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    if (demand) { var next = await backend.Request("next", new { afterRevision = cursor, timeoutMs = 1000 }); cursor = next.GetProperty("revision").GetInt64(); }
                    else await Task.Delay(1000);
                    process.Refresh(); var row = new Sample(name, watch.Elapsed.TotalSeconds, process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, process.Threads.Count, gpu.Read()); rows.Add(row); samples.Add(row);
                }
                process.Refresh(); var health = await backend.Request("health", new { });
                stages.Add(new { name, seconds = watch.Elapsed.TotalSeconds, cpuCorePercent = (process.TotalProcessorTime - cpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds * 100, first = rows.First(), last = rows.Last(), privatePeak = rows.Max(x => x.privateBytes), handlePeak = rows.Max(x => x.handles), health });
                Console.WriteLine(JsonSerializer.Serialize(new { stage = name, elapsedSeconds = watch.Elapsed.TotalSeconds, privateBytes = rows.Last().privateBytes }));
            }
            await Stage("cold-idle", 15, false);
            await backend.Request("subscribe", new { windows = true, system = true, intervalMs = 1000 });
            await Stage("system-and-windows", 25, true);
            foreach (string provider in new[] { "audio", "wifi", "bluetooth", "media" })
                try { await backend.Request(provider + "-subscribe", new { enabled = true }); } catch (NativeSystemRequestException e) { errors.Add(provider + ":" + e.ErrorCode); }
            var notification = await backend.Request("notifications-status", new { });
            if (notification.GetProperty("available").GetBoolean()) await backend.Request("notifications-subscribe", new { enabled = true, intervalMs = 5000 });
            await Stage("providers-subscribed", 25, true);
            await backend.Request("unsubscribe", new { });
            var released = await backend.Request("health", new { });
            checks["all-provider-registrations-released"] = Released(released);
            long samplesAtRelease = released.GetProperty("systemSamples").GetInt64();
            await Stage("released-idle", 30, false);
            var afterIdle = await backend.Request("health", new { });
            checks["no-system-sampling-after-unsubscribe"] = afterIdle.GetProperty("systemSamples").GetInt64() == samplesAtRelease;
            checks["no-provider-registrations-after-idle"] = Released(afterIdle);
            process.Refresh(); int settledHandles = process.HandleCount; long settledPrivate = process.PrivateMemorySize64;
            var cycleRows = new List<object>();
            for (int cycle = 0; cycle < 12; cycle++)
            {
                foreach (string provider in new[] { "audio", "wifi", "bluetooth", "media" })
                    try { await backend.Request(provider + "-subscribe", new { enabled = true }); } catch (NativeSystemRequestException) { }
                await backend.Request("unsubscribe", new { }); process.Refresh();
                cycleRows.Add(new { cycle, handles = process.HandleCount, privateBytes = process.PrivateMemorySize64 });
            }
            await Task.Delay(1500); process.Refresh();
            checks["repeated-provider-cycles-release-registrations"] = Released(await backend.Request("health", new { }));
            int handleDelta = process.HandleCount - settledHandles; long privateDelta = process.PrivateMemorySize64 - settledPrivate;
            checks["provider-cycle-handle-growth-bounded"] = handleDelta <= 16;
            checks["provider-cycle-private-growth-bounded"] = privateDelta <= 8 * 1024 * 1024;
            var latency = new List<double>();
            for (int i = 0; i < 40; i++) { var clock = Stopwatch.StartNew(); await backend.Request("health", new { }); latency.Add(clock.Elapsed.TotalMilliseconds); }
            latency.Sort();
            var result = new { allPass = checks.Values.All(x => x), pid, checks, stages, cycles = cycleRows, cycleHandleDelta = handleDelta, cyclePrivateDelta = privateDelta, controlLatencyMedianMs = latency[latency.Count / 2], controlLatencyP95Ms = latency[(int)(latency.Count * .95) - 1], gpuProbeErrors = gpu.Errors, providerErrors = errors, samples, scope = "Isolated partial backend; read-only real device queries and owned subscriptions; not full-desktop performance or comparison with the derived host. CPU is percent of one logical core. No working-set trimming or forced GC." };
            File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return result.allPass ? 0 : 1;
        }
        catch (Exception error) { File.WriteAllText(report, JsonSerializer.Serialize(new { allPass = false, pid, checks, stages, samples, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
    }
    private static bool Released(JsonElement h) => h.GetProperty("nativeHooks").GetInt32() == 0 && h.GetProperty("powerRegistrations").GetInt32() == 0 && !h.GetProperty("networkNotificationActive").GetBoolean()
        && h.GetProperty("audio").GetProperty("deviceCallbacks").GetInt32() == 0 && h.GetProperty("audio").GetProperty("volumeCallbacks").GetInt32() == 0
        && h.GetProperty("media").GetProperty("managerCallbacks").GetInt32() == 0 && h.GetProperty("media").GetProperty("sessionCallbacks").GetInt32() == 0 && h.GetProperty("media").GetProperty("cachedSessions").GetInt32() == 0
        && h.GetProperty("wifi").GetProperty("callbacks").GetInt32() == 0 && h.GetProperty("wifi").GetProperty("cachedInterfaces").GetInt32() == 0
        && h.GetProperty("bluetooth").GetProperty("callbacks").GetInt32() == 0 && h.GetProperty("bluetooth").GetProperty("cachedDevices").GetInt32() == 0
        && h.GetProperty("notifications").GetProperty("callbacks").GetInt32() == 0 && h.GetProperty("notifications").GetProperty("cachedNotifications").GetInt32() == 0 && !h.GetProperty("notifications").GetProperty("fallbackPolling").GetBoolean();
    private sealed record Sample(string stage, double seconds, long workingSetBytes, long privateBytes, int handles, int threads, Dictionary<string, double?> gpu);
    private sealed class GpuProbe : IDisposable
    {
        private IntPtr query; private readonly int pid; private readonly Dictionary<string, IntPtr> counters = new(); internal List<string> Errors { get; } = new();
        internal GpuProbe(int processId)
        {
            pid = processId; uint status = PdhOpenQuery(null, UIntPtr.Zero, out query); if (status != 0) { Errors.Add("open:" + status.ToString("X")); return; }
            foreach (var (key, path) in new[] { ("dedicatedBytes", @"\GPU Process Memory(*)\Dedicated Usage"), ("sharedBytes", @"\GPU Process Memory(*)\Shared Usage"), ("enginePercentSum", @"\GPU Engine(*)\Utilization Percentage") })
            {
                status = PdhAddEnglishCounter(query, path, UIntPtr.Zero, out var counter);
                if (status == 0) counters.Add(key, counter); else Errors.Add(key + ":add:" + status.ToString("X"));
            }
            PdhCollectQueryData(query);
        }
        internal Dictionary<string, double?> Read()
        {
            var result = new Dictionary<string, double?>();
            uint collect = query != IntPtr.Zero ? PdhCollectQueryData(query) : 1;
            if (collect != 0) Remember("collect:" + collect.ToString("X"));
            foreach (var key in new[] { "dedicatedBytes", "sharedBytes", "enginePercentSum" })
            {
                result[key] = null; if (collect != 0 || !counters.TryGetValue(key, out var counter)) continue;
                uint bytes = 0, count = 0; uint status = PdhGetFormattedCounterArray(counter, 0x200, ref bytes, ref count, IntPtr.Zero);
                if (status != 0x800007D2 || bytes > 16 * 1024 * 1024) { if (status != 0x800007D5) Remember(key + ":size:" + status.ToString("X")); continue; }
                IntPtr memory = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    status = PdhGetFormattedCounterArray(counter, 0x200, ref bytes, ref count, memory); if (status != 0) { Remember(key + ":read:" + status.ToString("X")); continue; }
                    double sum = 0; bool found = false; int size = Marshal.SizeOf<CounterItem>();
                    if ((ulong)count * (ulong)size > bytes) { Remember(key + ":invalid-buffer"); continue; }
                    for (int i = 0; i < count; i++) { var item = Marshal.PtrToStructure<CounterItem>(memory + i * size); string name = Marshal.PtrToStringUni(item.name) ?? ""; if (name.StartsWith("pid_" + pid + "_", StringComparison.OrdinalIgnoreCase) && item.status <= 1) { sum += item.value; found = true; } }
                    // No matching PID is absence of a GPU instance, not an observed zero.
                    result[key] = found ? sum : null;
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            return result;
        }
        private void Remember(string text) { if (!Errors.Contains(text)) Errors.Add(text); }
        public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct CounterItem { [FieldOffset(0)] internal IntPtr name; [FieldOffset(8)] internal uint status; [FieldOffset(16)] internal double value; }
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, UIntPtr user, out IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }
}
