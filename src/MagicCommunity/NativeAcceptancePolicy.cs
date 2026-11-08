using System.IO;
using System.Text.Json;
namespace MagicCommunity;
// Continuous acceptance is explicit in both selection and authorization. Timed
// permits retain their single-use, one-hour behavior for existing callers.
internal static class NativeAcceptancePolicy
{
    internal static string Mode(JsonElement value)
    {
        if (!value.TryGetProperty("acceptanceMode", out var field)) return "timed";
        if (field.ValueKind == JsonValueKind.String && field.GetString() is "timed" or "continuous") return field.GetString()!;
        throw new InvalidDataException("Unknown native acceptance mode.");
    }
    internal static void Validate(JsonElement value, bool continuous, DateTimeOffset now)
    {
        if (Mode(value) != (continuous ? "continuous" : "timed")) throw new InvalidDataException("Native acceptance mode differs.");
        if (continuous)
        {
            if (!value.TryGetProperty("durationSeconds", out var duration) || duration.ValueKind != JsonValueKind.Null ||
                value.TryGetProperty("expiresUtc", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Continuous acceptance cannot carry a deadline.");
            return;
        }
        if (!value.TryGetProperty("durationSeconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out int count) || count != 3600)
            throw new InvalidDataException("Timed native acceptance requires one hour.");
        if (value.TryGetProperty("startedUtc", out var started) && started.ValueKind == JsonValueKind.String)
            throw new InvalidDataException("Native acceptance is already consumed; explicit renewal is required.");
        if (!value.TryGetProperty("launchBeforeUtc", out var launch) || launch.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(launch.GetString(), out var before) || before <= now || before > now.AddMinutes(10))
            throw new InvalidDataException("Native acceptance launch window expired.");
    }
}
