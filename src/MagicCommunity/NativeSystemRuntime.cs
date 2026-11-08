using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// Own system protocol and immutable install selection. A schema/feature flag does
// not prove readiness: production selection also needs reviewed capability evidence.
internal sealed class NativeSystemRuntime
{
    internal const string Protocol = "Magic.System/1";
    internal static readonly string[] Capabilities = ["system-data", "window-tracking", "tray", "notifications", "taskbar", "configuration"];
    internal required string Folder { get; init; }
    internal required string InstanceId { get; init; }
    internal required string DataDirectory { get; init; }
    internal required Dictionary<string,string> Files { get; init; }
    internal bool ValidationOnly { get; private set; }
    internal string Host => Path.Combine(Folder, "magic-system-host.exe");

    private static JsonDocument Read(string path, int max)
    {
        using var input=File.OpenRead(path);
        if(input.Length>max)throw new InvalidDataException("System runtime document exceeds bound.");
        return JsonDocument.Parse(input, new JsonDocumentOptions{MaxDepth=32});
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Lazy<NativeSystemRuntime?>> Selections=new(StringComparer.OrdinalIgnoreCase);
    internal static NativeSystemRuntime? Selected(string root)=>Selections.GetOrAdd(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),key=>new Lazy<NativeSystemRuntime?>(()=>ReadSelection(key))).Value;
    private static NativeSystemRuntime? ReadSelection(string root)
    {
        if(NativeSystemDeployment.IsSelected(root))return null;
        string path=Path.Combine(root,"runtime/system/selection.json");
        if(!File.Exists(path))return null;
        using var doc=Read(path,8192);var selection=doc.RootElement;
        if(selection.GetProperty("enabled").ValueKind!=JsonValueKind.True)return null;
        if(selection.GetProperty("schemaVersion").GetInt32()!=1 || selection.GetProperty("protocol").GetString()!=Protocol ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(selection.GetProperty("owner").GetString()!),Path.GetFullPath(root)))
            throw new InvalidDataException("Independent system selection ownership differs.");
        bool validation=selection.TryGetProperty("mode",out var mode)&&mode.GetString()=="validation";
        var runtime=Load(root,selection.GetProperty("manifest").GetString()!,selection.GetProperty("manifestSha256").GetString()!,!validation);
        if(validation){
            string permit=SystemRuntimeIdentity.Payload(runtime.Folder,"validation-permit.json",selection.GetProperty("validationPermitSha256").GetString()!);
            using var authorization=Read(permit,8192);var value=authorization.RootElement;
            using var current=System.Security.Principal.WindowsIdentity.GetCurrent();
            if(value.GetProperty("protocol").GetString()!=Protocol||value.GetProperty("instanceId").GetString()!=runtime.InstanceId||value.GetProperty("purpose").GetString()!="interactive-validation"||value.GetProperty("manifestSha256").GetString()!=selection.GetProperty("manifestSha256").GetString()||value.GetProperty("userSid").GetString()!=current.User!.Value||!DateTimeOffset.TryParse(value.GetProperty("expiresUtc").GetString(),out var expiry)||expiry<=DateTimeOffset.UtcNow||expiry>DateTimeOffset.UtcNow.AddHours(2))
                throw new InvalidDataException("Temporary system validation authorization is absent, expired or differs.");
            runtime.ValidationOnly=true;
        }
        return runtime;
    }
    internal static NativeSystemRuntime Load(string root,string manifest,string expected,bool requireAccepted)
    {
        root=SystemRuntimeIdentity.Plain(root);
        string folder=SystemRuntimeIdentity.Installation(Path.GetDirectoryName(Path.GetFullPath(manifest))!);
        if(Path.GetFileName(manifest)!="magic-system-runtime.json")throw new InvalidDataException("Invalid runtime manifest name.");
        manifest=SystemRuntimeIdentity.Payload(folder,"magic-system-runtime.json",expected);
        using var doc=Read(manifest,524288);var value=doc.RootElement;
        string instance=value.GetProperty("instanceId").GetString()!;
        if(value.GetProperty("schemaVersion").GetInt32()!=1||value.GetProperty("protocol").GetString()!=Protocol||
           instance.Length!=32||instance.Any(c=>!Uri.IsHexDigit(c))||
           !StringComparer.OrdinalIgnoreCase.Equals(SystemRuntimeIdentity.Plain(value.GetProperty("projectRoot").GetString()!),root))
            throw new InvalidDataException("Independent runtime identity differs.");
        string data=SystemRuntimeIdentity.Plain(value.GetProperty("dataDirectory").GetString()!);
        if(!StringComparer.OrdinalIgnoreCase.Equals(data,Path.Combine(root,"data","system")))throw new InvalidDataException("Independent runtime data namespace differs.");
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in value.GetProperty("files").EnumerateObject())
        {
            if(files.Count>=4096)throw new InvalidDataException("Too many runtime files.");
            string hash=file.Value.GetString()!;files.Add(file.Name,hash);
            SystemRuntimeIdentity.Payload(folder,file.Name,hash);
        }
        foreach(string file in new[]{"magic-system-host.exe","magic-system-service.exe","magic-system-ctl.exe","sluhk.dll","community-boot.json","SHA256SUMS","SHA256SUMS.sig"})
            if(!files.ContainsKey(file))throw new InvalidDataException("Incomplete independent system runtime: "+file);
        if(requireAccepted)
            foreach(string capability in Capabilities)
            {
                var entry=value.GetProperty("capabilities").GetProperty(capability);
                if(entry.GetProperty("status").GetString()!="accepted"||!StringComparer.OrdinalIgnoreCase.Equals(entry.GetProperty("providerSha256").GetString(),files["magic-system-host.exe"]))
                    throw new InvalidDataException("Independent capability has no matching acceptance: "+capability);
                string evidence=SystemRuntimeIdentity.Payload(folder,entry.GetProperty("evidencePath").GetString()!,entry.GetProperty("evidenceSha256").GetString()!);
                using var receipt=Read(evidence,65536);var accepted=receipt.RootElement;
                if(accepted.GetProperty("protocol").GetString()!=Protocol||accepted.GetProperty("instanceId").GetString()!=instance||accepted.GetProperty("capability").GetString()!=capability||accepted.GetProperty("reviewedBy").GetString() is not { Length:>0 })
                    throw new InvalidDataException("Capability evidence identity differs: "+capability);
                foreach(string file in new[]{"magic-system-host.exe","magic-system-service.exe","magic-system-ctl.exe","sluhk.dll"})
                    if(!StringComparer.OrdinalIgnoreCase.Equals(accepted.GetProperty("runtimeFiles").GetProperty(file).GetString(),files[file]))throw new InvalidDataException("Capability evidence is stale: "+capability);
                string[] checks=capability switch {
                    "system-data"=>["snapshot-actions","idle-resources"],
                    "window-tracking"=>["open-close-focus","mixed-dpi","elevated-window"],
                    "tray"=>["icons-actions","explorer-restart"],
                    "notifications"=>["authorize-revoke","arrive-dismiss","exit-restore"],
                    "taskbar"=>["dock-actions","uac-entry","crash-restore"],
                    _=>["import-checksums","plugin-order","startup-rollback"]};
                foreach(string check in checks)
                    if(accepted.GetProperty("checks").GetProperty(check).GetProperty("passed").ValueKind!=JsonValueKind.True||accepted.GetProperty("checks").GetProperty(check).GetProperty("evidence").GetString() is not {Length:>0})
                        throw new InvalidDataException("Capability evidence is incomplete: "+capability+"/"+check);
            }
        return new(){Folder=folder,InstanceId=instance,DataDirectory=data,Files=files};
    }
    internal void ValidateSnapshot(JsonElement data)
    {
        var backend=data.GetProperty("systemBackend");
        if(backend.GetProperty("protocol").GetString()!=Protocol||backend.GetProperty("instanceId").GetString()!=InstanceId)
            throw new InvalidDataException("System snapshot provider differs from the selected runtime.");
    }
}