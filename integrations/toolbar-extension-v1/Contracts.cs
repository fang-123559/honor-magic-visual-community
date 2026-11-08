using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magic.ToolbarExtension;

// Transport and view data only. No application names, shell commands, arbitrary
// scripts, file paths, HTML, framework controls or host-private credentials.
public static class ToolbarProtocol
{
    public const string Name = "Magic.ToolbarExtension/1";
    public const int MaximumFrameBytes = 65536;
    public const int HeartbeatSeconds = 5;
    public const int TimeoutSeconds = 15;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24 };
    private static readonly Regex Identifier = new("^[a-z][a-z0-9_.-]{0,95}\\z", RegexOptions.CultureInvariant);
    public static bool ValidId(string? value) => value != null && Identifier.IsMatch(value);
    public static void Validate(ToolbarContent content)
    {
        if (content == null || content.Segments is not { Length: > 0 and <= 16 } || !double.IsFinite(content.Gap) || content.Gap is < 0 or > 24 || content.MaxWidth is < 80 or > 1200 || content.BlinkPeriodMs is < 350 or > 2000) throw new InvalidDataException("Invalid toolbar layout.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in content.Segments)
        {
            if (segment == null || !ValidId(segment.Id) || !ids.Add(segment.Id) || segment.Runs is not { Length: > 0 and <= 8 }) throw new InvalidDataException("Invalid toolbar segment.");
            foreach (var run in segment.Runs) if (run == null || !Text(run.Text, 256)) throw new InvalidDataException("Invalid text run.");
            if (segment.Indicator is { } dot && (dot.Semantic is not ("positive" or "busy" or "attention" or "inactive") || dot.Size is < 3 or > 12)) throw new InvalidDataException("Invalid indicator.");
            Action(segment.ClickAction); Action(segment.ContextAction);
        }
        if (content.Overlay is { } overlay)
        {
            if (!Text(overlay.Source, 128) || !Text(overlay.Text, 2048) || overlay.Count is < 1 or > 10000) throw new InvalidDataException("Invalid overlay.");
            Action(overlay.ClickAction);
            if (overlay.TrailingAction is { } trailing && (!Text(trailing.Label, 64) || !ValidId(trailing.Id))) throw new InvalidDataException("Invalid trailing action.");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(content, Json).Length > 32768) throw new InvalidDataException("Toolbar content exceeds bound.");
    }
    private static bool Text(string? value, int max) => value != null && value.Length <= max && !value.Any(c => char.IsControl(c));
    private static void Action(string? id) { if (id != null && !ValidId(id)) throw new InvalidDataException("Invalid action identifier."); }
    public static HashSet<string> Actions(ToolbarContent content)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in content.Segments) { if (segment.ClickAction != null) result.Add(segment.ClickAction); if (segment.ContextAction != null) result.Add(segment.ContextAction); }
        if (content.Overlay?.ClickAction is { } click) result.Add(click);
        if (content.Overlay?.TrailingAction is { } trailing) result.Add(trailing.Id);
        return result;
    }
    public static ExtensionMessage Message(string type, object payload, string? requestId = null) => new(Name, type, requestId, JsonSerializer.SerializeToElement(payload, Json));
    public static async Task WriteAsync(Stream stream, ExtensionMessage frame, CancellationToken token = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (body.Length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Frame exceeds bound.");
        var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    public static async Task<ExtensionMessage> ReadAsync(Stream stream, CancellationToken token = default)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (count is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Frame exceeds bound.");
        var body = new byte[count]; await stream.ReadExactlyAsync(body, token);
        var frame = JsonSerializer.Deserialize<ExtensionMessage>(body, Json) ?? throw new InvalidDataException("Missing frame.");
        if (frame.Protocol != Name || !ValidId(frame.Type) || (frame.RequestId != null && !ValidId(frame.RequestId))) throw new InvalidDataException("Unsupported frame identity.");
        return frame;
    }
}

public sealed record ExtensionManifest(int SchemaVersion, string Protocol, string Id, string DisplayName, string Version, string Surface, string PipeName, string SessionId, string Token, int ProviderPid, long ProviderCreated, int HeartbeatSeconds, int TimeoutSeconds);
public sealed record ExtensionMessage(string Protocol, string Type, string? RequestId, JsonElement Payload);
public sealed record ToolbarRun(string Text, bool Bold = false);
public sealed record ToolbarIndicator(string Semantic, int Size = 6);
public sealed record ToolbarSegment(string Id, ToolbarRun[] Runs, ToolbarIndicator? Indicator = null, string? ClickAction = null, string? ContextAction = null);
public sealed record ToolbarAction(string Id, string Label);
public sealed record ToolbarOverlay(string Source, string Text, int Count = 1, bool Attention = false, string? ClickAction = null, ToolbarAction? TrailingAction = null);
// Segments always determine the intrinsic width, including while Overlay is
// visible. The host owns placement, font/color, DPI and its own hide policy.
public sealed record ToolbarContent(ToolbarSegment[] Segments, ToolbarOverlay? Overlay = null, double Gap = 6.4, int MaxWidth = 680, int BlinkPeriodMs = 700);
public sealed record ToolbarSnapshot(long Revision, ToolbarContent Content);
public sealed record HostHello(string ExtensionId, string SessionId, string Token);
public sealed record HostState(bool Mounted, bool Visible);
public sealed record HostAction(long Revision, string Id);
