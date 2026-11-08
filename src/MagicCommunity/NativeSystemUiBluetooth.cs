using System.Text.Json;
using static MagicCommunity.NativeSystemPanel;
namespace MagicCommunity;
internal sealed partial class NativeSystemUiSession
{
    // Shares the bounded, event-invalidated snapshot mechanism with networking.
    // Provider failure is panel-local and never terminates the presentation pump.
    private async Task PublishBluetoothDetails(string token, long revision, JsonElement value, Exception? error)
    {
        bool Current() { lock (currentGate) return currentToken == token && currentKind == "@magic/bluetooth-popup" && bluetoothDetails.IsCurrent(token, revision); }
        if (!Current() || stop.IsCancellationRequested) return;
        if (error is not null)
        {
            log("independent_bluetooth_refresh_failed", new { type = error.GetType().Name, error = error.Message });
            value = JsonSerializer.SerializeToElement(new { available = false });
        }
        var state = presentation.State;
        var panel = NativeSystemPanel.Build("@magic/bluetooth-popup", state with
        { Providers = new Dictionary<string, JsonElement>(state.Providers) { ["bluetooth"] = value } });
        panel.Data["hostPid"] = Environment.ProcessId;
        lock (currentGate) { if (!Current()) return; currentPanel = panel; }
        await popups.Update(JsonSerializer.SerializeToElement(new { token, data = panel.Data }), stop.Token);
    }
}
