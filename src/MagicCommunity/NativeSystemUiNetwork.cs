using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession
{
    private NativeSystemPresentationSession.Published NetworkState(NativeSystemPresentationSession.Published state) => networkPortal.Enrich(state);
    private NativeSystemPresentationSession.Published NetworkPanelState()
    {
        var state = presentation.State;
        var details = networkDetails.Snapshot;
        if (details.ValueKind == JsonValueKind.Object)
            state = state with { Providers = new Dictionary<string, JsonElement>(state.Providers) { ["wifi"] = details } };
        return NetworkState(state);
    }
    private bool NetworkPopupAcceptsDetails(string token)
    {
        lock (currentGate) return currentToken == token && currentKind == "@magic/network-popup" &&
            currentPanel?.Data["nativeNetworkPassword"]?.GetValue<bool>() != true;
    }
    private void NetworkActionCompleted(string token)
    {
        networkDetails.Invalidate();
        lock (currentGate)
        {
            if (currentToken != token || currentKind != "@magic/network-popup") return;
            if (currentPanel is not null) currentPanel.Data["nativeNetworkPassword"] = false;
            networkDetails.Show(token);
        }
    }
    private async Task<JsonElement> ReadNetworkDetails(CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        var wifi = await backend.Request("wifi-read", new { networkDetails = true }, cancellation);
        // Populate the provider's offered profile set before exposing actions.
        foreach (var adapter in Array(wifi, "interfaces"))
            await backend.Request("wifi-profiles", new { interfaceId = Text(adapter, "id") }, cancellation);
        log("independent_network_details_timing", new { elapsedMs = watch.Elapsed.TotalMilliseconds, interfaces = Array(wifi, "interfaces").Count() });
        return wifi;
    }
    private async Task PublishNetworkDetails(string token, long revision, JsonElement wifi, Exception? error)
    {
        try
        {
            if (stop.IsCancellationRequested || !networkDetails.IsCurrent(token, revision)) return;
            if (!NetworkPopupAcceptsDetails(token)) return;
            var state = presentation.State;
            if (error is null) state = state with { Providers = new Dictionary<string, JsonElement>(state.Providers) { ["wifi"] = wifi } };
            else
            {
                log("independent_network_refresh_failed", new { type = error.GetType().Name });
                var basic = state.Providers.GetValueOrDefault("wifi");
                var unavailable = basic.ValueKind == JsonValueKind.Object ? JsonNode.Parse(basic.GetRawText())!.AsObject() : new JsonObject();
                unavailable["networkDetailsFailed"] = true;
                state = state with { Providers = new Dictionary<string, JsonElement>(state.Providers) { ["wifi"] = JsonSerializer.SerializeToElement(unavailable) } };
            }
            var panel = NativeSystemPanel.Build("@magic/network-popup", NetworkState(state), notificationQuiet: notificationQuiet);
            panel.Data["hostPid"] = Environment.ProcessId;
            lock (currentGate) { if (!NetworkPopupAcceptsDetails(token) || !networkDetails.IsCurrent(token, revision)) return; currentPanel = panel; }
            await popups.Update(JsonSerializer.SerializeToElement(new { token, data = panel.Data }), stop.Token);
        }
        catch (Exception publishError) when (publishError is not OutOfMemoryException) { if (!stop.IsCancellationRequested) log("independent_network_publish_failed", new { type = publishError.GetType().Name }); }
    }
    private void PortalChanged()
    {
        if (stop.IsCancellationRequested || Volatile.Read(ref closing) != 0) return;
        ui.BeginInvoke(new Action(async () =>
        {
            string? token; lock (currentGate) { if (currentKind != "@magic/network-popup" || currentToken is null) return; token = currentToken; }
            if (!NetworkPopupAcceptsDetails(token)) return;
            try
            {
                // Portal text reuses the latest snapshot; it must not enumerate
                // nearby networks again or stall the shared presentation pump.
                var panel = NativeSystemPanel.Build("@magic/network-popup", NetworkPanelState(), notificationQuiet: notificationQuiet);
                panel.Data["hostPid"] = Environment.ProcessId;
                lock (currentGate) { if (currentToken != token) return; currentPanel = panel; }
                await popups.Update(JsonSerializer.SerializeToElement(new { token, data = panel.Data }), stop.Token);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { log("wifi_auth_status_failed", new { type = e.GetType().Name }); }
        }));
    }
}
