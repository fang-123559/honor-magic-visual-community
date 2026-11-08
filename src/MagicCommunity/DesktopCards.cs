using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagicCommunity;

public sealed class DesktopCard
{
    public string Id{get;set;}=Guid.NewGuid().ToString("N");
    public string Type{get;set;}="";
    public int Columns{get;set;}=2;
    public int Rows{get;set;}=2;
    public JsonElement Settings{get;set;}=JsonSerializer.SerializeToElement(new{});
}
public sealed record DesktopCardSize(int Columns,int Rows);
public sealed class DesktopCardDependency
{
    public string Title{get;set;}="";
    public string Description{get;set;}="";
    public string Url{get;set;}="";
    public Uri ReleaseUri()
    {
        if(!Uri.TryCreate(Url,UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.Host!="github.com"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||!uri.AbsolutePath.Contains("/releases/",StringComparison.Ordinal))throw new InvalidDataException("卡片下载入口必须是 GitHub Releases 的 HTTPS 地址。");
        return uri;
    }
}
public sealed class DesktopCardDefinition
{
    public string Type{get;set;}="";
    public string Title{get;set;}="";
    public List<DesktopCardSize> Sizes{get;set;}=[];
    public List<string> DataSources{get;set;}=[];
    public CustomCardManifest? Custom{get;set;}
    public DesktopCardDependency? Dependency{get;set;}
}

// Build-owned declarative metadata. Links open only after an explicit user action;
// no code, dependency downloads or installers are executed by the catalog.
public sealed class DesktopCardCatalog
{
    public const int ApiVersion=1;
    // Built-in types must not consume the existing allowance of 64 custom types.
    public const int MaximumTypes=128;
    public IReadOnlyList<DesktopCardDefinition> Types{get;}
    public DesktopCardCatalog(IEnumerable<DesktopCardDefinition> definitions)
    {
        Types=definitions.ToArray();if(Types.Count>MaximumTypes||Types.Any(value=>value is null)||Types.Select(value=>value.Type).Distinct(StringComparer.Ordinal).Count()!=Types.Count)throw new InvalidDataException("卡片目录无效。");
        foreach(var value in Types)
        {
            if(!DesktopCards.ValidType(value.Type)||string.IsNullOrWhiteSpace(value.Title)||value.Title.Length>80||value.Sizes is null||value.Sizes.Count is <1 or >16||value.Sizes.Any(size=>size is null||!DesktopCards.ValidSize(size.Columns,size.Rows))||value.DataSources is null||value.DataSources.Count>16||value.DataSources.Any(source=>!DesktopCards.ValidType(source)))throw new InvalidDataException("卡片声明无效。");
            if(value.Dependency is {} dependency){
                if(value.Custom is not null||!value.Type.StartsWith("system.",StringComparison.Ordinal)||string.IsNullOrWhiteSpace(dependency.Title)||dependency.Title.Length>80||string.IsNullOrWhiteSpace(dependency.Description)||dependency.Description.Length>400||dependency.Url.Length>512)throw new InvalidDataException("卡片依赖声明无效。");
                dependency.ReleaseUri();
            }
        }
    }
    public static DesktopCardCatalog Load(string path)
    {
        if(!File.Exists(path))return new([]);
        var bytes=File.ReadAllBytes(path);if(bytes.Length>65536)throw new InvalidDataException("卡片目录过大。");using var json=JsonDocument.Parse(bytes);
        if(json.RootElement.GetProperty("apiVersion").GetInt32()!=ApiVersion)throw new InvalidDataException("不支持的卡片接口版本。");
        return new(JsonSerializer.Deserialize<DesktopCardDefinition[]>(json.RootElement.GetProperty("types"),StateStore.Json)??throw new InvalidDataException());
    }
    public DesktopCardDefinition Require(string type)=>Types.SingleOrDefault(value=>value.Type==type)??throw new LayoutEditException("该卡片类型尚未安装。");
    public Uri DependencyUri(string type)=>Require(type).Dependency?.ReleaseUri()??throw new LayoutEditException("该卡片没有外部下载入口。");
}

public static class DesktopCards
{
    public static IEnumerable<DesktopCard> Items(Board board)=>board.Cards is null?Enumerable.Empty<DesktopCard>():board.Cards;
    public static string Key(DesktopCard card)=>"card:"+card.Id;
    public static bool ValidType(string? type)=>type is {Length:>0 and <=96}&&type.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_' or '/');
    public static bool ValidSize(int columns,int rows)=>columns is >=1 and <=8&&rows is >=1 and <=8;
    public static void ValidateSettings(JsonElement settings)
    {
        if(settings.ValueKind!=JsonValueKind.Object||System.Text.Encoding.UTF8.GetByteCount(settings.GetRawText())>16384)throw new LayoutEditException("卡片设置必须是至多16KiB的JSON对象。");
        void Walk(JsonElement value,int depth){if(depth>8)throw new LayoutEditException("卡片设置层级过深。");if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(p.Name.Length is <1 or >128||!names.Add(p.Name))throw new LayoutEditException("卡片设置键名无效或重复。");Walk(p.Value,depth+1);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Walk(child,depth+1);}
        Walk(settings,0);
    }
    public static void Validate(Board board,HashSet<string> ids)
    {
        if(board.Cards is null)return;if(board.Cards.Count>64)throw new InvalidDataException("每屏卡片数量超过上限。");
        foreach(var card in board.Cards)
        {
            if(card is null||string.IsNullOrEmpty(card.Id)||card.Id.Length!=32||!card.Id.All(Uri.IsHexDigit)||!ids.Add(card.Id)||!ValidType(card.Type)||!ValidSize(card.Columns,card.Rows)||board.Grid is null||board.Grid.FoldersOnly)throw new InvalidDataException("卡片结构无效。");
            try{ValidateSettings(card.Settings);}catch(LayoutEditException error){throw new InvalidDataException("卡片设置无效。",error);}
        }
    }
    public static DesktopCard Require(Board board,string id)=>Items(board).SingleOrDefault(card=>card.Id==id)??throw new LayoutEditException("该卡片已移除。");
    public static DesktopCard Add(LayoutState state,Board board,DesktopCardCatalog catalog,string type,int columns,int rows,JsonElement settings,int? x=null,int? y=null)
    {
        var definition=catalog.Require(type);if(!definition.Sizes.Contains(new(columns,rows)))throw new LayoutEditException("该卡片不支持此尺寸。");ValidateSettings(settings);
        if((board.Cards?.Count??0)>=64||board.Grid is null||!board.Grid.NativeInitialized||board.Grid.FoldersOnly)throw new LayoutEditException("桌面尚未就绪或卡片已达上限。");
        var rect=DesktopGridActions.ReserveRect(board,columns,rows,x,y);var card=new DesktopCard{Type=type,Columns=columns,Rows=rows,Settings=settings.Clone()};
        if(type=="system.clock")card.Settings=ClockSettings(card,card.Settings,true);
        (board.Cards??=[]).Add(card);board.Grid.Placements.Add(Key(card),rect);state.SchemaVersion=Math.Max(5,state.SchemaVersion);return card;
    }
    internal static JsonElement ClockSettings(DesktopCard card,JsonElement settings,bool resizing=false)
    {
        var next=JsonNode.Parse(settings.GetRawText())!.AsObject();
        int font=settings.TryGetProperty("fontSize",out var f)&&f.ValueKind==JsonValueKind.Number&&f.TryGetInt32(out var n)&&n is 56 or 72 or 82 or 92 or 108?n:82;
        int weight=settings.TryGetProperty("fontWeight",out var w)&&w.ValueKind==JsonValueKind.Number&&w.TryGetInt32(out var v)&&v==300?300:900;
        if(card.Columns==2&&card.Rows==1&&font>56){if(!resizing)throw new LayoutEditException($"「1 行 × 2 列」只放得下 56 号时间，无法使用 {font} 号。");font=56;}
        next["fontSize"]=font;next["fontWeight"]=weight;return JsonSerializer.SerializeToElement(next);
    }
    public static void Configure(Board board,string id,JsonElement settings){ValidateSettings(settings);var card=Require(board,id);card.Settings=card.Type=="system.clock"?ClockSettings(card,settings):settings.Clone();}
    public static void Resize(Board board,DesktopCardCatalog catalog,string id,int columns,int rows)
    {
        var card=Require(board,id);if(!catalog.Require(card.Type).Sizes.Contains(new(columns,rows)))throw new LayoutEditException("该卡片不支持此尺寸。");
        var grid=board.Grid!;string key=Key(card);var rect=grid.Placements[key] with{W=columns,H=rows};if(!DesktopGridActions.CanFit(grid,rect,key))throw new LayoutEditException("卡片周围空间不足，原位置保留。");
        card.Columns=columns;card.Rows=rows;if(card.Type=="system.clock")card.Settings=ClockSettings(card,card.Settings,true);grid.Placements[key]=rect;
    }
    public static void Remove(Board board,string id){var card=Require(board,id);board.Cards!.Remove(card);DesktopPages.Forget(board,Key(card));}
    public static void Transfer(Board source,Board destination,string id,int? x=null,int? y=null)
    {
        if(source.Id==destination.Id)throw new LayoutEditException("请选择另一块屏幕。");var card=Require(source,id);
        if((destination.Cards?.Count??0)>=64||destination.Grid is null||destination.Grid.FoldersOnly)throw new LayoutEditException("目标桌面尚未就绪。");
        var rect=DesktopGridActions.ReserveRect(destination,card.Columns,card.Rows,x,y);source.Cards!.Remove(card);DesktopPages.Forget(source,Key(card));(destination.Cards??=[]).Add(card);destination.Grid.Placements.Add(Key(card),rect);
    }
}
