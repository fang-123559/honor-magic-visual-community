using System.IO;

namespace MagicCommunity;

// Pure decisions only. No observer, file store, COM object, worker or OS writes.
public static class SecondaryWallpaperPolicy
{
    public enum Mode { Unknown, StaticImages, SolidColor, Slideshow, Span }
    public enum Trigger { Startup, TopologyChanged, WindowsSettingsObserved, ReadOnlyCheck, ExplicitRestore }
    public sealed record Monitor(string DevicePath, bool Primary, bool ReliableIdentity = true);
    public sealed record Reading(string DevicePath, int HResult, string? ImagePath, bool ImageAvailable);
    public sealed record Observation(long Generation, Mode Mode, bool TopologyStable, bool Complete, IReadOnlyList<Reading> Readings);
    public sealed record BaselineEntry(string DevicePath, string ImagePath);
    public sealed record Baseline(long Revision, bool Trusted, IReadOnlyList<BaselineEntry> Entries);
    // Explicit per-target user action, bound to the displayed baseline revision.
    // SourceImageAvailable must be freshly checked by the eventual adapter.
    public sealed record RestoreApproval(string DevicePath, long Generation, long BaselineRevision, bool SourceImageAvailable);
    public sealed record Input(long Generation, IReadOnlyList<Monitor> Monitors, Trigger Trigger, Observation? Observation = null, Baseline? Baseline = null, RestoreApproval? Approval = null);
    public sealed record RestoreIntent(long Generation, long BaselineRevision, string DevicePath, string ImagePath, string ExpectedCurrentImagePath);
    public sealed record Decision(string Reason, bool NeedsObservation, IReadOnlyList<BaselineEntry> BaselineUpdates, RestoreIntent? Restore = null);

    public static bool ShouldObserve(IReadOnlyList<Monitor> monitors) => monitors.Count >= 2;

    public static Decision Evaluate(Input input)
    {
        static Decision Keep(string reason, bool observe = false) => new(reason, observe, []);
        if (!ShouldObserve(input.Monitors)) return Keep("single-display-no-work");
        if (input.Monitors.Count(m => m.Primary) != 1 || input.Monitors.Any(m => !Reliable(m.DevicePath, m.ReliableIdentity)) || Duplicate(input.Monitors.Select(m => m.DevicePath)))
            return Keep("ambiguous-monitor-identity");
        var observation = input.Observation;
        if (observation is null) return Keep("observe-current-Windows-state", true);
        if (observation.Generation != input.Generation) return Keep("stale-observation");
        if (!observation.TopologyStable) return Keep("topology-not-stable");
        if (observation.Mode != Mode.StaticImages) return Keep("non-static-or-unknown-mode");
        if (Duplicate(observation.Readings.Select(r => r.DevicePath))) return Keep("ambiguous-observation-identity");
        var baseline = input.Baseline;
        if (baseline is not null && (!baseline.Trusted || baseline.Revision < 0 || Duplicate(baseline.Entries.Select(e => e.DevicePath)) || baseline.Entries.Any(e => !Reliable(e.DevicePath, true) || NormalizeImagePath(e.ImagePath) is null)))
            return Keep("untrusted-or-invalid-baseline");
        var readings = observation.Readings.ToDictionary(r => r.DevicePath, StringComparer.OrdinalIgnoreCase);
        var saved = (baseline?.Entries ?? []).ToDictionary(e => e.DevicePath, StringComparer.OrdinalIgnoreCase);
        var approval = input.Trigger == Trigger.ExplicitRestore ? input.Approval : null;
        if (input.Trigger == Trigger.ExplicitRestore && approval is null) return Keep("explicit-restore-approval-required");
        if (approval is not null)
        {
            if (approval.Generation != input.Generation || baseline is null || approval.BaselineRevision != baseline.Revision)
                return Keep("stale-restore-approval");
            var target = input.Monitors.SingleOrDefault(m => StringComparer.OrdinalIgnoreCase.Equals(m.DevicePath, approval.DevicePath));
            if (target is null || target.Primary) return Keep("restore-target-offline-or-primary");
            if (!observation.Complete || input.Monitors.Any(m => ReadImage(readings.GetValueOrDefault(m.DevicePath)) is null)) return Keep("incomplete-observation");
            if (!saved.TryGetValue(target.DevicePath, out var entry) || !approval.SourceImageAvailable) return Keep("restore-source-unavailable");
            var current = ReadImage(readings.GetValueOrDefault(target.DevicePath));
            if (current is null) return Keep("restore-current-image-unknown");
            string image = NormalizeImagePath(entry.ImagePath)!;
            if (StringComparer.OrdinalIgnoreCase.Equals(current, image)) return Keep("current-image-already-matches");
            return new("explicit-secondary-restore", false, [], new(input.Generation, baseline.Revision, target.DevicePath, image, current));
        }
        var updates = new List<BaselineEntry>();
        foreach (var monitor in input.Monitors.Where(m => !m.Primary))
        {
            string? current = ReadImage(readings.GetValueOrDefault(monitor.DevicePath));
            if (current is null) continue; // Failed HRESULT, empty/solid or missing image never overwrites a known baseline.
            if (!saved.TryGetValue(monitor.DevicePath, out var previous)) updates.Add(new(monitor.DevicePath, current));
            else if (input.Trigger == Trigger.WindowsSettingsObserved && !StringComparer.OrdinalIgnoreCase.Equals(current, NormalizeImagePath(previous.ImagePath)))
                updates.Add(new(monitor.DevicePath, current));
            // A startup/topology difference may be intentional, including both screens using the same picture.
            // Preserve Windows and the prior baseline. Never infer corruption from this difference alone.
        }
        return new(updates.Count > 0 ? "merge-successful-secondary-baseline" : "keep-Windows-current-choice", false, updates);
    }

    // The adapter must supply a freshly read observation after checking the current
    // generation/topology/baseline revision, immediately before a target-specific write.
    // This does not make the following Windows COM call atomic with external changes.
    public static bool CanApply(RestoreIntent intent, Input freshInput) => intent is not null && Evaluate(freshInput).Restore == intent;

    public static string? NormalizeImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    private static string? ReadImage(Reading? reading) => reading is not null && reading.HResult == 0 && reading.ImageAvailable ? NormalizeImagePath(reading.ImagePath) : null;
    private static bool Reliable(string path, bool verified) => verified && !string.IsNullOrWhiteSpace(path) && !path.StartsWith("temporary:", StringComparison.OrdinalIgnoreCase);
    private static bool Duplicate(IEnumerable<string> ids) { var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); return ids.Any(id => string.IsNullOrWhiteSpace(id) || !seen.Add(id)); }
}
