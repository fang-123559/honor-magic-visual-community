using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
namespace MagicCommunity;
public sealed partial class CompositionDesktopRuntime{
    private readonly SemaphoreSlim handoffInfo=new(1,1);
    private async Task<object> HandoffInfo(JsonElement request){
        var choice=await ui.InvokeAsync(()=>{var card=DesktopCards.Require(Board(Text(request,"board")),Text(request,"key")[5..]);if(card.Type!="system.handoff")throw new InvalidDataException();var nodes=NativeHandoffStorage.Read(card.Settings,controller.Store.DirectoryPath,card.Id).Nodes;int index=request.GetProperty("index").GetInt32();if(index<0||index>=Math.Min(16,nodes.Length)||nodes[index].Type!="file")throw new InvalidDataException();return nodes[index];});
        if(!await handoffInfo.WaitAsync(0,stop.Token))throw new InvalidOperationException("正在读取附件信息。");
        var read=Task.Run(()=>{
            try{
                string name=Path.GetFileName(choice.Path),detail=choice.Extension+" · "+choice.Bytes.ToString("N0")+" 字节";if(!File.Exists(choice.Path))return new{name,modified="文件已不在原位置\n"+choice.Path,present=false};
                var file=new FileInfo(choice.Path);detail=choice.Extension+" · "+file.Length.ToString("N0")+" 字节";
                if(choice.Kind=="image"&&file.Length<=NativeHandoffStorage.ImageLimit){try{using var stream=File.OpenRead(choice.Path);var image=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];detail+=" · "+image.PixelWidth+" × "+image.PixelHeight;}catch(Exception e)when(e is not OutOfMemoryException){}}
                else if(file.Extension.Equals(".zip",StringComparison.OrdinalIgnoreCase)&&file.Length>=22){
                    using var stream=File.OpenRead(choice.Path);int length=(int)Math.Min(stream.Length,65557);byte[] tail=new byte[length];stream.Position=stream.Length-length;stream.ReadExactly(tail);int end=-1;for(int i=tail.Length-22;i>=0;i--)if(BitConverter.ToUInt32(tail,i)==0x06054b50u){end=i;break;}
                    if(end>=0){int count=BitConverter.ToUInt16(tail,end+10);uint bytes=BitConverter.ToUInt32(tail,end+12);detail+=" · "+count+" 个条目";if(count<=1024&&bytes<=1024*1024&&file.Length<=20*1024*1024){stream.Position=0;using var zip=new ZipArchive(stream,ZipArchiveMode.Read);foreach(var item in zip.Entries.Take(3)){string entry=item.FullName;detail+="\n"+entry[..Math.Min(100,entry.Length)];}}}
                }return new{name,modified=detail+"\n"+choice.Path,present=true};
            }finally{handoffInfo.Release();}
        });
        var result=await read.WaitAsync(TimeSpan.FromMilliseconds(800),stop.Token);
        await ui.InvokeAsync(()=>handoff.ObservePresence(choice.Path,result.present));
        string? uri=choice.Kind=="image"&&result.present?await ui.InvokeAsync(()=>bridge?.NativeIcon(new DesktopItem{Name=choice.Name,Target=choice.Path})):null;
        return new{result.name,result.modified,png=uri is not null&&uri.StartsWith("data:image/png;base64,",StringComparison.Ordinal)&&uri.Length<22000?uri[22..]:null};
    }
}
