using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace MagicCommunity;
// Reads only the current bitmap formats. No file-drop decoding, history or disk cache.
internal sealed class NativeClipboardImage : IDisposable
{
    private readonly HwndSource window;
    private readonly DispatcherTimer retry=new(){Interval=TimeSpan.FromMilliseconds(30)};
    private readonly Action<object> emit;
    private readonly uint png=RegisterClipboardFormat("PNG");
    private byte[]? original;
    private uint format,sequence;
    private int attempts,generation;
    private bool demanded,disposed,decoding,pending;
    private const int Limit=32*1024*1024;
    public NativeClipboardImage(Action<object> emit){this.emit=emit;window=new(new HwndSourceParameters("Magic current clipboard image"){ParentWindow=new IntPtr(-3)});window.AddHook(Hook);retry.Tick+=(_,_)=>{retry.Stop();Read();};}
    private IntPtr Hook(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled){if(m==0x31d&&demanded){attempts=0;Read();}return IntPtr.Zero;}
    public void Demand(bool visible){if(disposed||visible==demanded)return;demanded=visible;generation++;retry.Stop();if(visible){AddClipboardFormatListener(window.Handle);sequence=0;Read();}else{RemoveClipboardFormatListener(window.Handle);original=null;format=0;sequence=0;emit(new{available=false});}}
    private async void Read()
    {
        if(!demanded||disposed)return;if(decoding){pending=true;return;}uint next=GetClipboardSequenceNumber();if(next==sequence)return;
        if(!OpenClipboard(window.Handle)){if(++attempts<=3)retry.Start();return;}
        byte[]? bytes=null;uint selected=0;IntPtr bitmap=IntPtr.Zero;
        try{foreach(uint f in new[]{png,17u,8u}){if(!IsClipboardFormatAvailable(f))continue;IntPtr handle=GetClipboardData(f);nuint size=GlobalSize(handle);if(size is 0 or >Limit)continue;IntPtr p=GlobalLock(handle);if(p==IntPtr.Zero)continue;try{bytes=new byte[(int)size];Marshal.Copy(p,bytes,0,bytes.Length);selected=f;break;}finally{GlobalUnlock(handle);}}
            if(bytes is null&&IsClipboardFormatAvailable(2)){IntPtr h=GetClipboardData(2);if(GetObject(h,Marshal.SizeOf<BitmapInfo>(),out var info)>0&&info.Width>0&&info.Height>0&&(long)info.Width*info.Height<=16*1024*1024)bitmap=CopyImage(h,0,0,0,0x2000);}
        }finally{CloseClipboard();}
        // A new clipboard sequence invalidates the previous image immediately.
        original=null;format=0;sequence=next;emit(new{available=false,sequence});int owner=generation;decoding=true;
        try{
            var result=await Task.Run(()=>{
                byte[]? payload=bytes;
                if(bitmap!=IntPtr.Zero){try{var src=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,System.Windows.Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(src));using var ms=new MemoryStream();enc.Save(ms);payload=ms.Length<=Limit?ms.ToArray():null;}finally{DeleteObject(bitmap);}}
                if(payload is null)return (Full:(byte[]?)null,Thumb:(byte[]?)null,Format:0u);
                bool isPng=selected==png||bitmap!=IntPtr.Zero;byte[] input=isPng?payload:DibToBmp(payload);using var metadataStream=new MemoryStream(input,false);var metadata=BitmapDecoder.Create(metadataStream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];if(metadata.PixelWidth<=0||metadata.PixelHeight<=0||(long)metadata.PixelWidth*metadata.PixelHeight>16*1024*1024)throw new InvalidDataException();var image=new BitmapImage();image.BeginInit();image.StreamSource=new MemoryStream(input,false);image.CacheOption=BitmapCacheOption.OnLoad;if(metadata.PixelWidth/(double)metadata.PixelHeight>752d/368)image.DecodePixelWidth=Math.Min(752,metadata.PixelWidth);else image.DecodePixelHeight=Math.Min(368,metadata.PixelHeight);image.EndInit();image.Freeze();
                if(image.PixelWidth==0||image.PixelHeight>4096)return (Full:(byte[]?)null,Thumb:(byte[]?)null,Format:0u);
                BitmapSource thumb=image;if(image.PixelHeight>368){var small=new System.Windows.Media.ScaleTransform(368d/image.PixelHeight,368d/image.PixelHeight);thumb=new TransformedBitmap(image,small);thumb.Freeze();}
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(thumb));using var output=new MemoryStream();encoder.Save(output);return (Full:(byte[]?)payload,Thumb:output.Length<=512*1024?output.ToArray():null,Format:isPng?png:selected);
            });
            if(disposed||!demanded||owner!=generation||GetClipboardSequenceNumber()!=next){pending=true;return;}
            if(result.Thumb is not null){original=result.Full;format=result.Format;emit(new{available=true,sequence,png=Convert.ToBase64String(result.Thumb)});}
        }catch(Exception e)when(e is not OutOfMemoryException){/* Unsupported content is an empty card; never log image bytes. */}
        finally{decoding=false;if(pending&&!disposed){pending=false;Read();}}
    }
    private static byte[] DibToBmp(byte[] dib)
    {
        if(dib.Length<40)throw new InvalidDataException();int header=BitConverter.ToInt32(dib,0),width=BitConverter.ToInt32(dib,4),height=BitConverter.ToInt32(dib,8);ushort bits=BitConverter.ToUInt16(dib,14);uint compression=BitConverter.ToUInt32(dib,16),used=BitConverter.ToUInt32(dib,32);
        if(header<40||header>dib.Length||width<=0||height==int.MinValue||height==0||(long)width*Math.Abs((long)height)>64*1024*1024||bits is not (1 or 4 or 8 or 16 or 24 or 32))throw new InvalidDataException();
        long offset=14L+header+(bits<=8?(used==0?1L<<bits:used)*4:0)+(header==40&&compression==3?12:header==40&&compression==6?16:0);if(offset>dib.Length+14||offset>int.MaxValue)throw new InvalidDataException();
        var bmp=new byte[dib.Length+14];bmp[0]=66;bmp[1]=77;BitConverter.GetBytes(bmp.Length).CopyTo(bmp,2);BitConverter.GetBytes((int)offset).CopyTo(bmp,10);dib.CopyTo(bmp,14);return bmp;
    }
    public bool Action(string action)
    {
        if(!demanded||disposed)throw new InvalidOperationException("卡片当前不可见。");
        if(GetClipboardSequenceNumber()!=sequence){Read();return false;}
        if(action=="image-copy"){
            if(original is null){emit(new{available=false,sequence});return false;}
            // It is already the exact current image; preserve all companion formats.
            return true;
        }
        if(action!="image-clear")throw new InvalidOperationException();
        if(!OpenClipboard(window.Handle))throw new InvalidOperationException("剪贴板正忙，请重试。");try{if(GetClipboardSequenceNumber()!=sequence)throw new InvalidOperationException("剪贴板已变化，请重试。");if(!EmptyClipboard())throw new InvalidOperationException("未能清除剪贴板。");}finally{CloseClipboard();}Read();return true;
    }
    public async Task Save()
    {
        if(original is null||GetClipboardSequenceNumber()!=sequence){emit(new{available=false,sequence});return;}
        byte[] source=original;uint sourceFormat=format;var dialog=new Microsoft.Win32.SaveFileDialog{Title="另存图片",Filter="PNG 图片|*.png",FileName="剪贴板图片.png"};if(dialog.ShowDialog()!=true)return;string path=dialog.FileName;
        await Task.Run(()=>{
            string temporary=path+".magic-save-"+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                    if(sourceFormat==png)output.Write(source);else{using var input=new MemoryStream(DibToBmp(source));var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);var encoder=new PngBitmapEncoder();encoder.Frames.Add(decoder.Frames[0]);encoder.Save(output);}output.Flush(true);
                }
                File.Move(temporary,path,true);
            }catch(Exception error)when(error is not OutOfMemoryException){throw new IOException("保存未完成，目标原文件保持不变。临时图像（如已生成）位于："+temporary,error);}
        });
    }
    public void Dispose(){if(disposed)return;Demand(false);disposed=true;retry.Stop();window.Dispose();original=null;}
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public int Type,Width,Height,WidthBytes;public ushort Planes,Bits;public IntPtr Data;}
    [DllImport("user32.dll")]private static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")]private static extern bool RemoveClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll")]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("user32.dll")]private static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string f);
    [DllImport("kernel32.dll")]private static extern nuint GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetObject(IntPtr h,int bytes,out BitmapInfo value);
    [DllImport("user32.dll")]private static extern IntPtr CopyImage(IntPtr h,uint type,int x,int y,uint flags);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr h);
}
