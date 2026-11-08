using MagicCommunity;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class PopupIdentityChecks
{
 internal static void Run(Dictionary<string,bool> checks,string root){
  void Check(string name,bool pass){checks[name]=pass;if(!pass)throw new InvalidDataException(name);}
  using var ledger=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"resources/native-popup-identities.json")));var entries=ledger.RootElement.GetProperty("entries").EnumerateArray().ToArray();
  var cpp=File.ReadAllText(Path.Combine(root,"src/composition-trial/native-popup-ids.h"));
  Check("popup-id-ledger-has-twelve-public-identities",entries.Count(e=>e.GetProperty("scope").GetString()=="public")==12&&NativePopupIds.Supported.Length==12);
  foreach(var entry in entries){string legacy=entry.GetProperty("legacy").GetString()!,canonical=entry.GetProperty("canonical").GetString()!;Check("popup-id-alias-"+canonical.Split('/')[1],NativePopupIds.Canonical(legacy)==canonical&&NativePopupIds.Canonical(canonical)==canonical&&cpp.Contains("{L\""+legacy+"\",L\""+canonical+"\"}"));}
  JsonObject Manifest(IEnumerable<string> ids)=>new(){["nativePopupProtocol"]=1,["desktopRenderer"]="composition",["nativePopups"]=new JsonArray(ids.Select(id=>(JsonNode?)JsonValue.Create(id)).ToArray())};
  string[] Selected(JsonObject manifest){using var json=JsonDocument.Parse(manifest.ToJsonString());return NativePopupIds.ReadSelection(json.RootElement);}
  var aliases=entries.Where(e=>e.GetProperty("scope").GetString()=="public").Select(e=>e.GetProperty("legacy").GetString()!).ToArray();
  Check("popup-id-old-and-new-manifests-select-identically",Selected(Manifest(aliases)).SequenceEqual(Selected(Manifest(NativePopupIds.Supported))));
  Check("popup-id-mixed-manifest-aliases-deduplicate",Selected(Manifest(aliases.Concat(NativePopupIds.Supported))).Length==12);
  Check("popup-id-external-plugin-name-is-not-rewritten",NativePopupIds.Canonical("@seelen/third-party-custom")=="@seelen/third-party-custom"&&Selected(Manifest(new[]{"@seelen/third-party-custom","@magic/context-menu","@seelen/flyouts"})).Length==0);
  var web=Manifest(aliases);web["desktopRenderer"]="web";Check("popup-id-web-manifest-does-not-enable-native-panels",Selected(web).Length==0);
  Check("popup-id-build-defaults-use-shared-ledger",File.ReadAllText(Path.Combine(root,"tools/build-preview.ps1")).Contains("resources/native-popup-identities.json"));
 }
}
