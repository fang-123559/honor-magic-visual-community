using System.IO;
using System.Text.Json;
namespace MagicCommunity;
internal static class NativeHonorServiceTests{
    internal static void Run(string fixture,Action<string,bool> check){
        string root=Path.Combine(fixture,"program-files"),fallback=Path.Combine(fixture,"program-files-x86");Directory.CreateDirectory(root);Directory.CreateDirectory(Path.Combine(fallback,"HONOR","PCManager"));
        string path=Path.Combine(fallback,"HONOR","PCManager","PCManager.exe");File.WriteAllText(path,"owned-not-executable");
        check("HONOR fixed catalog contains 16 distinct entries",NativeHonorServices.Catalog.Length==16&&NativeHonorServices.Catalog.Select(e=>e.Id).Distinct().Count()==16);
        check("HONOR resolves x86 fallback without running a supplied command file",NativeHonorServices.Resolve(NativeHonorServices.Catalog[0],[root,fallback])==path);
        check("HONOR absent executable stays absent",NativeHonorServices.Resolve(NativeHonorServices.Catalog[1],[root,fallback])=="");
        check("HONOR routes remain fixed and do not accept arbitrary arguments",NativeHonorServices.Catalog.Where(e=>e.Argument.Length>0).All(e=>e.Service=="PCManager Service Store"&&e.Candidates.SequenceEqual([@"PCManager\PCManager.exe"])));
        var catalog=new DesktopCardCatalog([new(){Type="system.honor-service",Title="荣耀服务中心",Sizes=[new(4,2),new(2,2)],DataSources=["system.honor-services"]}]);
        var state=new LayoutState{Boards=[new(){Id="honor",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}}]};var card=DesktopCards.Add(state,state.Boards[0],catalog,"system.honor-service",4,2,JsonSerializer.SerializeToElement(new{removed=new[]{"screenshot"},otherSetting="keep"}));
        var saved=JsonSerializer.SerializeToElement(card.Settings);
        check("HONOR card settings retain independently hidden entries and unknown settings",saved.GetProperty("removed")[0].GetString()=="screenshot"&&saved.GetProperty("otherSetting").GetString()=="keep");
    }
}
