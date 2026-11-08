using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace MagicCommunity;

public sealed class FeedbackImage
{
    public string Name { get; }
    public byte[] Bytes { get; }
    public string Extension { get; }
    public BitmapSource Preview { get; }
    public int Width { get; }
    public int Height { get; }
    public string Caption { get; set; } = "";
    internal FeedbackImage(string name, byte[] bytes, string extension, BitmapSource preview, int width, int height)
    { Name=name; Bytes=bytes; Extension=extension; Preview=preview; Width=width; Height=height; }
}

public static class FeedbackStore
{
    public const int MaxImages=6, MaxImageBytes=10*1024*1024, MaxTotalBytes=30*1024*1024;
    public static FeedbackImage ReadFile(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <1 or >MaxImageBytes)throw new InvalidDataException("每张图片须在 10 MB 以内。");
        var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
        return FromBytes(Path.GetFileName(path),bytes);
    }
    public static FeedbackImage FromBytes(string name, byte[] bytes)
    {
        if(bytes.Length is <1 or >MaxImageBytes)throw new InvalidDataException("每张图片须在 10 MB 以内。");
        using var stream=new MemoryStream(bytes,false);
        var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
        string extension=decoder is PngBitmapDecoder?".png":decoder is JpegBitmapDecoder?".jpg":decoder is BmpBitmapDecoder?".bmp":throw new InvalidDataException("请选择 PNG、JPG 或 BMP 图片。");
        var frame=decoder.Frames[0];int width=frame.PixelWidth,height=frame.PixelHeight;
        if(width<1||height<1||(long)width*height>40_000_000)throw new InvalidDataException("图片像素过大，请裁剪到 4000 万像素以内。");
        stream.Position=0;
        var preview=new BitmapImage();preview.BeginInit();preview.CacheOption=BitmapCacheOption.OnLoad;preview.DecodePixelWidth=180;preview.StreamSource=stream;preview.EndInit();preview.Freeze();
        return new FeedbackImage(Path.GetFileName(name),bytes.ToArray(),extension,preview,width,height);
    }
    public static void Validate(IReadOnlyList<FeedbackImage> images)
    {
        if(images.Count>MaxImages)throw new InvalidDataException("最多添加 6 张图片。");
        if(images.Sum(x=>(long)x.Bytes.Length)>MaxTotalBytes)throw new InvalidDataException("图片总大小须在 30 MB 以内。");
        if(images.Any(x=>x.Caption.Length>1000))throw new InvalidDataException("每张图片的说明最多 1000 字。");
    }
    public static string Save(string directory, object metadata, IReadOnlyList<FeedbackImage> images)
    {
        Validate(images);
        // Revalidate the exact bytes before committing; no source files are moved or modified.
        foreach(var image in images)FromBytes(image.Name,image.Bytes);
        Directory.CreateDirectory(directory);
        string id="feedback-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..8];
        string staging=Path.Combine(directory,".pending-"+Guid.NewGuid().ToString("N")),final=Path.Combine(directory,id);
        Directory.CreateDirectory(staging);
        try
        {
            var attachments=new List<object>();
            if(images.Count>0)Directory.CreateDirectory(Path.Combine(staging,"images"));
            for(int i=0;i<images.Count;i++)
            {
                var image=images[i];string relative=$"images/{i+1:00}{image.Extension}";
                using(var output=new FileStream(Path.Combine(staging,relative),FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {output.Write(image.Bytes);output.Flush(true);}
                attachments.Add(new{file=relative,originalName=image.Name,caption=image.Caption,width=image.Width,height=image.Height,bytes=image.Bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant()});
            }
            var document=JsonSerializer.SerializeToNode(metadata,StateStore.Json)!.AsObject();
            document["feedbackSchemaVersion"]=2;document["attachments"]=JsonSerializer.SerializeToNode(attachments,StateStore.Json);
            StateStore.AtomicWrite(Path.Combine(staging,"feedback.json"),document.ToJsonString(StateStore.Json),null);
            Directory.Move(staging,final);
            return Path.Combine(final,"feedback.json");
        }
        catch
        {
            // Keep failed staging for diagnosis. It is never exposed as a submitted report.
            throw;
        }
    }
}
