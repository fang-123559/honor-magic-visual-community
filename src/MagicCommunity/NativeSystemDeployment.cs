using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagicCommunity;

// Selected only by an explicit main-tree deployment. No Seelen discovery or
// inherited authorization. The signed package has its own notification identity.
internal sealed class NativeSystemDeployment
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<NativeSystemDeployment?>> selected = new(StringComparer.OrdinalIgnoreCase);
    internal const string Protocol = NativeSystemClient.Protocol;
    internal required string Root { get; init; }
    internal required string CodeDirectory { get; init; }
    internal required string DataDirectory { get; init; }
    internal required string PackageFamily { get; init; }
    internal required string Nonce { get; init; }
    internal required Dictionary<string, string> Files { get; init; }
    private readonly object gate = new();
    private DateTimeOffset? deadline;
    private bool acceptanceStarted;
    internal bool Continuous { get; init; }
    internal bool Installed { get; init; }
    internal string AcceptanceMode => Continuous ? "continuous" : "timed";
    internal string Backend => Path.Combine(CodeDirectory, "MagicSystemNative.exe");
    internal string Guard => Path.Combine(CodeDirectory, "MagicTaskbarGuard.exe");
    internal string Permit => Path.Combine(Root, "runtime/system/native-acceptance.json");
    internal static bool IsSelected(string root)
    {
        if (InstalledDesktop.IsCurrent(root)) return true;
        string path = Path.Combine(root, "runtime/system/selection.json"); if (!File.Exists(path)) return false;
        using var doc = Read(path, 8192); return doc.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True && doc.RootElement.TryGetProperty("protocol", out var value) && value.GetString() == Protocol;
    }
    internal static NativeSystemDeployment? Selected(string root) => selected.GetOrAdd(Path.GetFullPath(root), path => new(() => Load(path))).Value;
    private static NativeSystemDeployment? Load(string root)
    {
        if (InstalledDesktop.IsCurrent(root)) return InstalledDesktop.Deployment(root);
        if (!IsSelected(root)) return null; root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        using var pointer = Read(Path.Combine(root, "runtime/system/selection.json"), 8192); var p = pointer.RootElement;
        if (p.GetProperty("schemaVersion").GetInt32() != 2 || p.GetProperty("mode").GetString() != "validation" || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(p.GetProperty("owner").GetString()!), root)) throw new InvalidDataException("Native system selection ownership differs.");
        string relative = p.GetProperty("manifest").GetString()!; string manifest = Path.GetFullPath(Path.Combine(root, relative)); if (!manifest.StartsWith(Path.Combine(root, "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(manifest) != "native-system.json") throw new InvalidDataException("Native manifest outside version directory.");
        VerifyHash(manifest, p.GetProperty("manifestSha256").GetString()!); using var source = Read(manifest, 65536); var s = source.RootElement;
        if (s.GetProperty("protocol").GetString() != Protocol || s.GetProperty("implementation").GetString() != "independent-win32") throw new InvalidDataException("Native implementation marker differs.");
        string family = p.GetProperty("packageFamily").GetString()!;
        string currentFamily = PackageText(GetCurrentPackageFamilyName); string code = PackageText(GetCurrentPackagePath);
        if (!family.StartsWith("MagicCommunity.Native_", StringComparison.Ordinal) || !StringComparer.Ordinal.Equals(currentFamily, family) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(code).TrimEnd(Path.DirectorySeparatorChar))) throw new InvalidDataException("Independent package identity is absent or differs.");
        string data = Path.GetFullPath(p.GetProperty("dataDirectory").GetString()!); if (!data.Equals(Path.GetFullPath(Path.Combine(root, "data", "native-system")), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native configuration namespace differs.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in s.GetProperty("files").EnumerateObject()) { if (file.Name.Contains('/') || file.Name.Contains('\\') || files.Count >= 128) throw new InvalidDataException("Native payload file name invalid."); VerifyHash(Path.Combine(code, file.Name), file.Value.GetString()!); files.Add(file.Name, file.Value.GetString()!); }
        foreach (string required in new[] { "MagicWindowAction.exe", "MagicSystemNative.exe", "MagicTrayCapture.dll", "MagicTaskbarGuard.exe", "MagicDesktopComposition.exe", "MagicCommunity.dll" }) if (!files.ContainsKey(required)) throw new InvalidDataException("Native payload incomplete: " + required);
        string nonce = p.GetProperty("nonce").GetString()!; if (nonce.Length != 32 || nonce.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Native acceptance nonce invalid.");
        var deployment = new NativeSystemDeployment { Root = root, CodeDirectory = code, DataDirectory = data, PackageFamily = family, Nonce = nonce, Files = files, Continuous = NativeAcceptancePolicy.Mode(p) == "continuous" }; deployment.CheckAuthorization(); return deployment;
    }
    internal DateTimeOffset? StartAcceptance()
    {
        lock (gate)
        {
            if (acceptanceStarted) return deadline;
            var document = CheckAuthorization(); var now = DateTimeOffset.UtcNow;
            document["startedUtc"] = now.ToString("o"); document["expiresUtc"] = Continuous ? null : now.AddHours(1).ToString("o"); document["acceptanceMode"] = AcceptanceMode; document["ownerPid"] = Environment.ProcessId; using var owner = System.Diagnostics.Process.GetCurrentProcess(); document["ownerCreated"] = owner.StartTime.ToFileTimeUtc().ToString();
            StateStore.AtomicWrite(Permit, document.ToJsonString(), null); deadline = Continuous ? null : now.AddHours(1); acceptanceStarted = true; return deadline;
        }
    }
    private JsonObject CheckAuthorization()
    {
        if (Installed) return InstalledDesktop.Authorization(Root);
        using var doc = Read(Permit, 8192); var value = doc.RootElement; using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (value.GetProperty("protocol").GetString() != Protocol || value.GetProperty("nonce").GetString() != Nonce || value.GetProperty("userSid").GetString() != identity.User!.Value) throw new InvalidDataException("Native acceptance authorization differs.");
        NativeAcceptancePolicy.Validate(value, Continuous, DateTimeOffset.UtcNow);
        if (Continuous && value.TryGetProperty("ownerPid", out var priorPid) && priorPid.TryGetInt32(out int pid) && pid != Environment.ProcessId && value.TryGetProperty("ownerCreated", out var created))
        {
            try { using var prior = System.Diagnostics.Process.GetProcessById(pid); if (!prior.HasExited && prior.StartTime.ToFileTimeUtc().ToString() == created.GetString()) throw new InvalidDataException("Continuous native acceptance already has a live owner."); }
            catch (ArgumentException) { }
        }
        return JsonNode.Parse(value.GetRawText())!.AsObject();
    }
    private static JsonDocument Read(string path, int maximum) { if (new FileInfo(path).Length > maximum || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Native document exceeds bound or is a link."); return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 24 }); }
    private static void VerifyHash(string path, string expected) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Native payload cannot be a link."); using var file = File.OpenRead(path); if (expected.Length != 64 || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(file)), expected)) throw new InvalidDataException("Native file hash differs: " + Path.GetFileName(path)); }
    private delegate int PackageQuery(ref uint length, StringBuilder? name);
    private static string PackageText(PackageQuery query) { uint n = 0; int status = query(ref n, null); if (status != 122 || n > 32768 || n == 0) throw new InvalidDataException("Native packaged identity is unavailable."); var text = new StringBuilder((int)n); status = query(ref n, text); if (status != 0) throw new System.ComponentModel.Win32Exception(status); return text.ToString(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackagePath(ref uint length, StringBuilder? value);
}
