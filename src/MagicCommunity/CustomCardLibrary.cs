using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace MagicCommunity;

public sealed record CustomCardManifest(string Id,string Kind,JsonElement Content,string Tone,string Align,string Fingerprint);

// User-writable packages are data, never imported modules. Scanning is explicit.
public sealed class CustomCardLibrary
{
    private static readonly Regex IdPattern=new("^[a-z0-9][a-z0-9._-]{2,63}$",RegexOptions.CultureInvariant);
    public string Root{get;}
    private readonly Action<string,object> log;
    public CustomCardLibrary(string root,Action<string,object> log){Root=Path.GetFullPath(root);this.log=log;}
    public static string DefaultRoot=>InstalledDesktop.Root is { } installed?Path.Combine(installed,"data","desktop-cards"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MagicCommunity","desktop-cards");
    private static string Text(JsonElement e,string key,int max,bool required=false,string fallback="")
    {
        if(!e.TryGetProperty(key,out var v)){if(required)throw new InvalidDataException("缺少 "+key);return fallback;}
        if(v.ValueKind!=JsonValueKind.String)throw new InvalidDataException("字段类型无效："+key);
        string s=v.GetString()!;if(s.Length>max||(required&&string.IsNullOrWhiteSpace(s)))throw new InvalidDataException("字段长度无效："+key);return s;
    }
    private static string Choice(JsonElement e,string key,string fallback,params string[] choices){string v=Text(e,key,32,false,fallback);if(!choices.Contains(v))throw new InvalidDataException("字段值无效："+key);return v;}
    private static bool Flag(JsonElement e,string key){if(!e.TryGetProperty(key,out var v))return false;if(v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("布尔值无效："+key);return v.GetBoolean();}
    public static bool IsLocalPath(string value)=>value.Length is >=3 and <=32760&&char.IsAsciiLetter(value[0])&&value[1]==':'&&(value[2]=='\\'||value[2]=='/')&&!value[2..].Contains(':')&&value.All(c=>!char.IsControl(c))&&value.IndexOfAny(['"','<','>','|','*','?'])<0;
    public static bool IsWebUrl(string value)=>value.Length<=4096&&value.All(c=>!char.IsControl(c))&&Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&string.IsNullOrEmpty(uri.UserInfo);
    private static void PlainTree(JsonElement value){if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("重复字段");PlainTree(p.Value);}}else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())PlainTree(child);}
    public static DesktopCardDefinition Parse(string directoryId,byte[] bytes)
    {
        if(!IdPattern.IsMatch(directoryId)||bytes.Length>16384)throw new InvalidDataException("卡片目录或文件大小无效");
        using var doc=JsonDocument.Parse(bytes,new(){MaxDepth=12});var e=doc.RootElement;PlainTree(e);
        if(e.ValueKind!=JsonValueKind.Object||e.GetProperty("schemaVersion").GetInt32()!=1||Text(e,"id",64,true)!=directoryId)throw new InvalidDataException("卡片版本或目录id不匹配");
        string kind=Choice(e,"kind","","text","countdown","shortcut","clock"),title=Text(e,"title",24,true);
        var sizes=e.GetProperty("sizes").Deserialize<List<DesktopCardSize>>(StateStore.Json)??throw new InvalidDataException("缺少尺寸");
        if(sizes.Count is <1 or >8||sizes.Any(s=>s is null||!DesktopCards.ValidSize(s.Columns,s.Rows))||sizes.Distinct().Count()!=sizes.Count)throw new InvalidDataException("卡片尺寸无效");
        var c=e.GetProperty("content");if(c.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("卡片内容无效");object content;
        if(kind=="text")content=new{title=Text(c,"title",20),body=Text(c,"body",200,true),bodySize=Choice(c,"bodySize","m","s","m","l")};
        else if(kind=="countdown")
        {
            string target=Text(c,"target",16,true);if(!DateTime.TryParseExact(target,["HH:mm","yyyy-MM-dd","yyyy-MM-dd HH:mm"],CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new InvalidDataException("倒计时日期无效");
            content=new{label=Text(c,"label",20),target,format=Choice(c,"format","d","d","hm","hms"),doneText=Text(c,"doneText",20,false,"已到时间")};
        }
        else if(kind=="clock")content=new{hour12=Flag(c,"hour12"),showSeconds=Flag(c,"showSeconds")};
        else
        {
            var items=c.GetProperty("items");if(items.ValueKind!=JsonValueKind.Array||items.GetArrayLength() is <1 or >6)throw new InvalidDataException("快捷入口数量无效");
            content=new{items=items.EnumerateArray().Select(row=>{string type=Choice(row,"type","","path","url"),target=Text(row,"target",32760,true),label=Text(row,"label",24,true);if(type=="path"?!IsLocalPath(target):!IsWebUrl(target))throw new InvalidDataException("快捷入口目标无效");return new{label,type,target};}).ToArray()};
        }
        string tone="light",align="center";if(e.TryGetProperty("style",out var style)){if(style.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("卡片样式无效");tone=Choice(style,"tone","light","light","dark");align=Choice(style,"align","center","left","center","right");}
        return new(){Type="custom."+directoryId,Title=title,Sizes=sizes,DataSources=[],Custom=new(directoryId,kind,JsonSerializer.SerializeToElement(content),tone,align,Convert.ToHexString(SHA256.HashData(bytes)))};
    }
    private static void NoLink(string path){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("卡片路径不允许链接或联接点");}
    public void CheckRoot()
    {
        for(var d=new DirectoryInfo(Root);d is not null;d=d.Parent)if(d.Exists)NoLink(d.FullName);
    }
    private static void PackageTree(string path)
    {
        NoLink(path);int count=0;foreach(string entry in Directory.EnumerateFileSystemEntries(path)){if(++count>128)throw new InvalidDataException("卡片资源数量过多");NoLink(entry);if(Directory.Exists(entry)){if(Path.GetFileName(entry)!="assets")throw new InvalidDataException("只允许assets资源目录");foreach(string asset in Directory.EnumerateFileSystemEntries(entry)){if(++count>128||Directory.Exists(asset))throw new InvalidDataException("资源目录层级或数量无效");NoLink(asset);}}}
    }
    public IReadOnlyList<DesktopCardDefinition> Scan()
    {
        var result=new List<DesktopCardDefinition>();try
        {
            CheckRoot();if(!Directory.Exists(Root))return result;
            int examined=0;foreach(string path in Directory.EnumerateDirectories(Root))
            {
                string id=Path.GetFileName(path);if(!IdPattern.IsMatch(id))continue;
                if(++examined>128){log("custom_card_scan_limit",new{limit=128});break;}
                try{PackageTree(path);string file=Path.Combine(path,"card.json");NoLink(file);using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);if(stream.Length>16384)throw new InvalidDataException("卡片声明过大");byte[] bytes=new byte[stream.Length];stream.ReadExactly(bytes);result.Add(Parse(id,bytes));if(result.Count>=64)break;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException){log("custom_card_skipped",new{id,reason=error.Message});}
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException){log("custom_card_library_unavailable",new{reason=error.Message});}
        return result.OrderBy(value=>value.Title,StringComparer.CurrentCulture).ToArray();
    }
    private sealed record Removal(string Id,string Stage,bool Committed);
    private string Package(string id){if(!IdPattern.IsMatch(id))throw new LayoutEditException("卡片id无效。");CheckRoot();return Path.Combine(Root,id);}
    public void Uninstall(DesktopCardDefinition definition,Action removeInstances,Action<string>? recycle=null)
    {
        var custom=definition.Custom??throw new LayoutEditException("系统卡片不能卸载。");string original=Package(custom.Id);PackageTree(original);
        if(Parse(custom.Id,File.ReadAllBytes(Path.Combine(original,"card.json"))).Custom!.Fingerprint!=custom.Fingerprint)throw new LayoutEditException("卡片包已变化，请重新打开面板确认。");
        string stage=".pending-"+Guid.NewGuid().ToString("N"),staged=Path.Combine(Root,stage),payload=Path.Combine(staged,custom.Id),journal=staged+".json";
        void Save(bool committed)=>StateStore.AtomicWrite(journal,JsonSerializer.Serialize(new Removal(custom.Id,stage,committed)),null);
        Save(false);Directory.CreateDirectory(staged);try{Directory.Move(original,payload);PackageTree(payload);}catch{if(Directory.Exists(payload)&&!Directory.Exists(original))Directory.Move(payload,original);Directory.Delete(staged,false);File.Delete(journal);throw;}
        try{removeInstances();}catch{Directory.Move(payload,original);Directory.Delete(staged,false);File.Delete(journal);throw;}
        // If the process stops before this write, recovery restores the package.
        // Once committed, failed recycling remains staged and is retried at startup.
        Save(true);try{PackageTree(payload);(recycle??Recycle)(payload);Directory.Delete(staged,false);File.Delete(journal);}
        catch(Exception error){log("custom_card_recycle_pending",new{id=custom.Id,reason=error.Message});throw new LayoutEditException("桌面实例已移除；卡片包暂存待回收，下次启动重试。未永久删除文件。");}
    }
    public static void Recycle(string path)=>FileSystem.DeleteDirectory(path,UIOption.OnlyErrorDialogs,RecycleOption.SendToRecycleBin,UICancelOption.ThrowException);
    public void Recover(Action<string>? recycle=null)
    {
        try
        {
            CheckRoot();if(!Directory.Exists(Root))return;foreach(string file in Directory.EnumerateFiles(Root,".pending-*.json").Take(128))try
            {
                NoLink(file);if(new FileInfo(file).Length>2048)throw new InvalidDataException();var item=JsonSerializer.Deserialize<Removal>(File.ReadAllText(file))??throw new InvalidDataException();
                if(!Regex.IsMatch(item.Stage,"^\\.pending-[a-f0-9]{32}$")||Path.GetFileName(file)!=item.Stage+".json")throw new InvalidDataException();
                string original=Package(item.Id),staged=Path.Combine(Root,item.Stage),payload=Path.Combine(staged,item.Id);
                if(Directory.Exists(staged)){NoLink(staged);if(Directory.Exists(payload)){PackageTree(payload);if(item.Committed)(recycle??Recycle)(payload);else{if(Directory.Exists(original))throw new IOException("恢复目标已存在");Directory.Move(payload,original);}}Directory.Delete(staged,false);}File.Delete(file);
            }catch(Exception error){log("custom_card_recovery_pending",new{reason=error.Message});}
        }catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException){log("custom_card_recovery_unavailable",new{reason=error.Message});}
    }
    public static int InstanceCount(LayoutState state,string type)=>state.Boards.Sum(board=>DesktopCards.Items(board).Count(card=>card.Type==type));
    public static void RemoveInstances(LayoutState state,string type){foreach(var board in state.Boards)foreach(var card in DesktopCards.Items(board).Where(card=>card.Type==type).ToArray())DesktopCards.Remove(board,card.Id);}
}
