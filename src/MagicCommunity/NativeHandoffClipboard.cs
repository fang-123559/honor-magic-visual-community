using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
namespace MagicCommunity;
internal static class NativeHandoffClipboard{
    internal sealed record Packet(string Text,string Html,string[] Files,byte[]? Png=null,byte[]? Dib=null);
    internal static string Html(string fragment){
        const string start="<html><body><!--StartFragment-->",end="<!--EndFragment--></body></html>";
        const string template="Version:1.0\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        int header=Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture,template,0,0,0,0)),a=header+Encoding.UTF8.GetByteCount(start),b=a+Encoding.UTF8.GetByteCount(fragment),last=b+Encoding.UTF8.GetByteCount(end);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,template,header,last,a,b)+start+fragment+end;
    }
    internal static Packet Prepare(NativeHandoffStorage.Node[] nodes,string root,string id,bool onlyText){
        var plain=new StringBuilder();var html=new StringBuilder();var files=new List<string>();int textBytes=0;
        foreach(var node in nodes){
            if(node.Type=="text"){string text=NativeHandoffStorage.FullText(node,root,id);textBytes+=Encoding.UTF8.GetByteCount(text);if(textBytes>NativeHandoffStorage.CopyTextLimit)throw new InvalidDataException("文字超过单次复制的 8 MiB 上限，请改为文件附件。");plain.Append(text);html.Append(WebUtility.HtmlEncode(text).Replace("\r\n","\n").Replace("\n","<br>"));}
            else{plain.Append('"').Append(node.Path).Append('"');bool exists=File.Exists(node.Path);if(exists)files.Add(node.Path);
                string name=WebUtility.HtmlEncode(node.Name);if(exists&&node.Kind=="image")html.Append("<img alt=\"").Append(name).Append("\" src=\"").Append(WebUtility.HtmlEncode(new Uri(node.Path).AbsoluteUri)).Append("\" />");else html.Append(name);}
        }
        byte[]? png=null,dib=null;if(!onlyText&&files.Count==1&&nodes.Count(n=>n.Type=="file"&&n.Kind=="image")==1){
            string path=files[0];try{if(string.Equals(Path.GetExtension(path),".png",StringComparison.OrdinalIgnoreCase)&&new FileInfo(path).Length<=NativeHandoffStorage.ImageLimit){
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);var image=decoder.Frames[0];if((long)image.PixelWidth*image.PixelHeight<=4*1024*1024){input.Position=0;png=new byte[input.Length];input.ReadExactly(png);var bitmap=new BmpBitmapEncoder();bitmap.Frames.Add(BitmapFrame.Create(image));using var stream=new MemoryStream();bitmap.Save(stream);byte[] bmp=stream.ToArray();if(bmp.Length>=54&&bmp.Length<=20*1024*1024)dib=bmp[14..];}
            }}catch(Exception e)when(e is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException){}
        }return new(plain.ToString(),Html(html.ToString()),onlyText?[]:files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),png,dib);
    }
    internal static byte[] FileDropBytes(string[] files){
        byte[] names=Encoding.Unicode.GetBytes(string.Join('\0',files)+"\0\0"),drop=new byte[20+names.Length];BitConverter.GetBytes(20).CopyTo(drop,0);BitConverter.GetBytes(1).CopyTo(drop,16);names.CopyTo(drop,20);return drop;
    }
    internal static void Put(IntPtr owner,Packet packet,bool onlyText){
        var formats=new Dictionary<uint,byte[]>{{13,Encoding.Unicode.GetBytes(packet.Text+"\0")}};
        if(!onlyText){formats[RegisterClipboardFormat("HTML Format")]=Encoding.UTF8.GetBytes(packet.Html+"\0");
            if(packet.Files.Length>0){byte[] drop=FileDropBytes(packet.Files);formats[15]=drop;}if(packet.Png is {} png)formats[RegisterClipboardFormat("PNG")]=png;if(packet.Dib is {} dib)formats[8]=dib;
        }
        if(formats.Values.Sum(v=>(long)v.Length)>64L*1024*1024)throw new InvalidDataException("交接输出超过 64 MiB，请仅复制文本或拆分附件。原内容已保留。");
        var handles=new Dictionary<uint,IntPtr>();try{
            foreach(var pair in formats){IntPtr handle=GlobalAlloc(2,(nuint)pair.Value.Length);if(handle==IntPtr.Zero)throw new OutOfMemoryException();handles.Add(pair.Key,handle);IntPtr memory=GlobalLock(handle);if(memory==IntPtr.Zero)throw new IOException("剪贴板分配失败。");try{Marshal.Copy(pair.Value,0,memory,pair.Value.Length);}finally{GlobalUnlock(handle);}}
            if(!OpenClipboard(owner))throw new IOException("剪贴板被占用，稍后重试。");
            try{if(!EmptyClipboard())throw new IOException("剪贴板未接受内容。");foreach(var pair in handles.ToArray()){if(SetClipboardData(pair.Key,pair.Value)==IntPtr.Zero)throw new IOException("剪贴板未能完整接受内容，请重试。");handles.Remove(pair.Key);}}finally{CloseClipboard();}
        }finally{foreach(var h in handles.Values)GlobalFree(h);}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")]private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll")]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern IntPtr SetClipboardData(uint format,IntPtr memory);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr handle);
}
