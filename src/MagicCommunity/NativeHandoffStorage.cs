using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MagicCommunity;
internal static class NativeHandoffStorage{
    internal const int InlineLimit=2048,TextLimit=1024*1024,ImageLimit=20*1024*1024,AssetLimit=200*1024*1024,CopyTextLimit=8*1024*1024;
    internal sealed record Node(string Type,string Text="",string Path="",string Name="",string Extension="",string Kind="file",long Bytes=0,string Reference="",int Chars=0,JsonElement? Original=null);
    internal sealed record Flow(Node[] Nodes,int Skipped);
    internal static string Owner(string id)=>Guid.TryParseExact(id,"N",out _)?id:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    internal static string DirectoryFor(string root,string id)=>System.IO.Path.Combine(System.IO.Path.GetFullPath(root),"handoff",Owner(id));
    internal static bool LocalFile(string path)=>path.Length is >3 and <=260&&char.IsAsciiLetter(path[0])&&path[1]==':'&&(path[2]=='\\'||path[2]=='/')&&!path.StartsWith(@"\\",StringComparison.Ordinal)&&path.IndexOf(':',2)<0&&!path.Any(char.IsControl)&&path.IndexOfAny(System.IO.Path.GetInvalidPathChars())<0;
    internal static void NoLinks(string root,string path){
        string full=System.IO.Path.GetFullPath(path),basePath=System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if(full!=basePath&&!full.StartsWith(basePath+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("交接资源路径越界。");
        for(string? at=full;at is not null&&at.Length>=basePath.Length;at=System.IO.Path.GetDirectoryName(at))if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("交接资源不能经过目录链接。");
    }
    internal static string ReferencePath(string root,string id,string reference){
        string prefix="handoff/"+Owner(id)+"/";string normal=reference.Replace('\\','/');if(normal.Length>260||!normal.StartsWith(prefix,StringComparison.Ordinal)||normal.Contains("..",StringComparison.Ordinal)||normal.Contains(':')||normal[prefix.Length..].Contains('/'))throw new InvalidDataException("外置文本引用无效。");
        string path=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,normal));NoLinks(root,path);return path;
    }
    internal static Flow Read(JsonElement settings,string root,string id){
        DesktopCards.ValidateSettings(settings);if(settings.EnumerateObject().Count()==0)return new([],0);
        if(!settings.TryGetProperty("version",out var version)||!version.TryGetInt32(out int v)||v!=1||!settings.TryGetProperty("flow",out var flow)||flow.ValueKind!=JsonValueKind.Array||flow.GetArrayLength()>512)throw new LayoutEditException("无法识别交接数据版本，原设置已保留。");
        var nodes=new List<Node>();int skipped=0;foreach(var item in flow.EnumerateArray()){
            try{
                if(item.ValueKind!=JsonValueKind.Object)throw new InvalidDataException();string type=Text(item,"t");
                if(type=="text"){
                    bool inline=item.TryGetProperty("v",out var value),external=item.TryGetProperty("ref",out var reference);if(inline==external)throw new InvalidDataException();
                    if(inline){if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException();string text=value.GetString()!;if(new UTF8Encoding(false,true).GetByteCount(text)>InlineLimit)throw new InvalidDataException();nodes.Add(new("text",text,Chars:text.Length,Original:item));}
                    else{if(reference.ValueKind!=JsonValueKind.String)throw new InvalidDataException();string path=reference.GetString()!;ReferencePath(root,id,path);string head=Text(item,"head");if(head.Length>200||!item.TryGetProperty("chars",out var chars)||!chars.TryGetInt32(out int count)||count<0||count>TextLimit)throw new InvalidDataException();nodes.Add(new("text",head,Reference:path,Chars:count,Original:item));}
                }else if(type=="file"){
                    string path=Text(item,"path"),name=Text(item,"name"),extension=Text(item,"ext"),kind=Text(item,"kind");if(!LocalFile(path)||name.Length>120||name.Any(char.IsControl)||extension.Length>8||extension.Any(char.IsControl)||kind is not ("image" or "file")||!item.TryGetProperty("bytes",out var bytes)||!bytes.TryGetInt64(out long count)||count<0)throw new InvalidDataException();nodes.Add(new("file",Path:path,Name:name,Extension:extension,Kind:kind,Bytes:count,Original:item));
                }else throw new InvalidDataException();
            }catch(Exception e)when(e is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException){skipped++;}
        }return new(nodes.ToArray(),skipped);
    }
    internal static string FullText(Node node,string root,string id){
        if(node.Reference.Length==0)return node.Text;string path=ReferencePath(root,id,node.Reference);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(input.Length>TextLimit)throw new InvalidDataException("外置文本超过 1 MiB，原内容保留。");using var reader=new StreamReader(input,new UTF8Encoding(false,true),false);return reader.ReadToEnd();
    }
    private static void EnsureOwner(string root,string id){
        string directory=DirectoryFor(root,id);NoLinks(root,directory);Directory.CreateDirectory(directory);NoLinks(root,directory);string marker=System.IO.Path.Combine(directory,".owner.json");NoLinks(root,marker);
        if(File.Exists(marker)){if(new FileInfo(marker).Length>4096)throw new InvalidDataException("交接资源归属文件过大。");using var doc=JsonDocument.Parse(File.ReadAllText(marker));if(doc.RootElement.GetProperty("owner").GetString()!=Owner(id)||doc.RootElement.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("交接资源归属不匹配。");}
        else StateStore.AtomicWrite(marker,JsonSerializer.Serialize(new{schemaVersion=1,owner=Owner(id)}),null);
    }
    internal static JsonElement Write(JsonElement original,IEnumerable<Node> nodes,string root,string id,ISet<string>? created=null){
        var flow=new JsonArray();int count=0;long copiedText=0;foreach(var node in nodes){
            if(++count>512)throw new LayoutEditException("交接条目最多 512 项。");var item=node.Original is {} old?JsonNode.Parse(old.GetRawText())!.AsObject():new JsonObject();item["t"]=node.Type;
            if(node.Type=="text"){
                byte[] bytes=new UTF8Encoding(false,true).GetBytes(node.Text);if(bytes.Length>TextLimit)throw new LayoutEditException("这段超过 1 MiB，建议存成文件再插入。");copiedText+=bytes.Length;if(copiedText>CopyTextLimit)throw new LayoutEditException("单次交接的文字最多 8 MiB；原内容保留，可将较长内容存成附件。");
                if(bytes.Length<=InlineLimit){item["v"]=node.Text;item.Remove("ref");item.Remove("chars");item.Remove("head");}
                else{EnsureOwner(root,id);string reference="handoff/"+Owner(id)+"/text-"+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()+".txt",path=ReferencePath(root,id,reference);
                    if(!File.Exists(path)){using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(true);created?.Add(path);}else if(new FileInfo(path).Length!=bytes.Length||!File.ReadAllBytes(path).SequenceEqual(bytes))throw new InvalidDataException("交接资源内容发生变化，未覆盖原文件。");
                    item.Remove("v");item["ref"]=reference;item["chars"]=node.Text.Length;item["head"]=node.Text[..Math.Min(200,node.Text.Length)];
                }
            }else{if(!LocalFile(node.Path))throw new InvalidDataException("附件不是有效的本地文件。");item["path"]=node.Path;item["name"]=node.Name;item["ext"]=node.Extension;item["kind"]=node.Kind;item["bytes"]=node.Bytes;}
            flow.Add(item);
        }
        var document=JsonNode.Parse(original.GetRawText())!.AsObject();document["version"]=1;document["flow"]=flow;document["updatedAt"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var result=JsonSerializer.SerializeToElement(document);DesktopCards.ValidateSettings(result);if(Read(result,root,id).Skipped!=0)throw new LayoutEditException("交接条目格式无效，未保存。");
        string own=DirectoryFor(root,id);if(Directory.Exists(own)&&(Directory.EnumerateFiles(own).Take(1025).Count()>1024||Directory.EnumerateFiles(own).Take(1025).Sum(p=>new FileInfo(p).Length)>AssetLimit))throw new LayoutEditException("交接外置资源已超过 200 MiB，原内容保留。");return result;
    }
    internal static string SaveImage(string root,string id,byte[] png){
        if(png.Length is 0 or >ImageLimit)throw new LayoutEditException("单张截图最多 20 MiB。");EnsureOwner(root,id);string directory=DirectoryFor(root,id);
        if(Directory.EnumerateFiles(directory).Take(1025).Sum(p=>new FileInfo(p).Length)+png.Length>AssetLimit)throw new LayoutEditException("交接资源最多 200 MiB。");
        string path=System.IO.Path.Combine(directory,"paste-"+Guid.NewGuid().ToString("N")+".png");using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Write(png);file.Flush(true);return path;
    }
    internal static bool Owned(string root,string id,string path){
        try{string directory=DirectoryFor(root,id);if(!System.IO.Path.GetFullPath(path).StartsWith(directory+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return false;NoLinks(root,path);if(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!=directory)return false;string name=System.IO.Path.GetFileName(path);if(!(name.Length==73&&name.StartsWith("text-",StringComparison.Ordinal)&&name.EndsWith(".txt",StringComparison.Ordinal)&&name.AsSpan(5,64).ToArray().All(char.IsAsciiHexDigit)||name.Length==42&&name.StartsWith("paste-",StringComparison.Ordinal)&&name.EndsWith(".png",StringComparison.Ordinal)&&Guid.TryParseExact(name.Substring(6,32),"N",out _)))return false;
            NoLinks(root,System.IO.Path.Combine(directory,".owner.json"));if(new FileInfo(System.IO.Path.Combine(directory,".owner.json")).Length>4096)return false;using var doc=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(directory,".owner.json")));return doc.RootElement.GetProperty("owner").GetString()==Owner(id)&&doc.RootElement.GetProperty("schemaVersion").GetInt32()==1;
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException){return false;}
    }
    private static string Text(JsonElement node,string key)=>node.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
}
