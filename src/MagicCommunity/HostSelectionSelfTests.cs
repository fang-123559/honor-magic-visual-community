using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagicCommunity;
internal static class HostSelectionSelfTests
{
    internal static void Run(string folder,Action<string,Action> check,Action<bool,string> expect)
    {
        string root=Path.Combine(folder,"host-selection"),runtime=Path.GetFullPath(Path.Combine(root,"runtime/desktop")),profile=Path.GetFullPath(Path.Combine(root,"profiles/native-host/test"));
        Directory.CreateDirectory(runtime);Directory.CreateDirectory(profile);
        string original=Path.Combine(root,"original"),exe=Path.Combine(profile,"seelen-ui.exe"),pointer=Path.Combine(runtime,"host.current.json");
        File.WriteAllText(Path.Combine(runtime,"install-state.json"),JsonSerializer.Serialize(new{installLocation=original,packageFullName="fixed-test-package"}));
        File.WriteAllText(exe,"inert fixture; never executable");
        string hash;using(var stream=File.OpenRead(exe))hash=Convert.ToHexString(SHA256.HashData(stream));
        Dictionary<string,object> Valid()=>new(){{"schemaVersion",1},{"enabled",true},{"owner",root},{"package","fixed-test-package"},{"executable",exe},{"sha256",hash}};
        void Write(Dictionary<string,object> value)=>File.WriteAllText(pointer,JsonSerializer.Serialize(value));
        check("host-without-pointer-preserves-original",()=>expect(NativeHostSelection.ReadVerified(root)==Path.Combine(original,"seelen-ui.exe"),"original host changed"));
        check("host-disabled-pointer-preserves-original",()=>{File.WriteAllText(pointer,"{\"enabled\":false}");expect(NativeHostSelection.ReadVerified(root)==Path.Combine(original,"seelen-ui.exe"),"disabled candidate selected");});
        check("host-candidate-requires-correct-content",()=>{Write(Valid());expect(NativeHostSelection.ReadVerified(root)==exe,"verified candidate rejected");});
        foreach(string field in new[]{"owner","package","executable","sha256"})
        {
            check("host-rejects-changed-"+field,()=>{var value=Valid();value[field]="invalid";Write(value);try{NativeHostSelection.ReadVerified(root);throw new Exception("untrusted host accepted");}catch(InvalidDataException){}});
        }
        check("host-validates-file-bytes",()=>{Write(Valid());File.AppendAllText(exe,"changed");try{NativeHostSelection.ReadVerified(root);throw new Exception("changed binary accepted");}catch(InvalidDataException){}finally{File.WriteAllText(exe,"inert fixture; never executable");}});
        check("host-trust-snapshot-stable-for-adapter-lifetime",()=>{Write(Valid());expect(NativeHostSelection.Resolve(root)==exe,"session identity missing");File.WriteAllText(pointer,"{\"enabled\":false}");expect(NativeHostSelection.Resolve(root)==exe,"live session identity silently rebound");});
    }
}
