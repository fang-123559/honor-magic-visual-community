using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MagicCommunity;
internal static class CompositionParityTests
{
 internal static void Run(string folder,Action<string,Action> check,Action<bool,string> expect)
 {
  check("native-drag-lease-rejects-stale-and-altered-payload",()=>{
   var original=new NativeDesktopTrial.DragPayload("session",40,"board",null,["item:a","item:b"],"item:a");
   var lease=new CompositionDragLease(original,"p1");var offered=original with {Keys=original.Keys.ToArray(),Lease=lease.Token,Page="p1"};
   expect(lease.Accept(offered,40),"offered lease rejected");
   expect(!lease.Accept(offered with {Keys=["item:a"]},40),"altered membership accepted");
   expect(!lease.Accept(offered with {Board="other"},40),"altered board accepted");
   expect(!lease.Accept(offered with {Lease="wrong"},40),"foreign token accepted");
   expect(!lease.Accept(offered,41),"unrelated revision accepted");
   lease.Advance(40,41);expect(lease.Accept(offered,41),"own page revision rejected");
   expect(!lease.Accept(offered with {Page="p2"},41),"altered source page accepted");
   try{lease.Advance(41,43);throw new Exception("skipped revision accepted");}catch(LayoutEditException){}
   expect(lease.Revision==41,"invalid advance mutated lease");
   original.Keys[0]="mutated";expect(lease.Accept(offered,41),"captured source was not copied");
  });
  check("native-drag-lease-unpaged-fixture",()=>{
   var d=new NativeDesktopTrial.DragPayload("s",1,"b",null,["folder:a"],"folder:a");var lease=new CompositionDragLease(d,null);
   expect(lease.Accept(d with {Lease=lease.Token,Page=""},1),"unpaged source null/empty mismatch");
  });
  check("native-source-page-scope-restores-on-failure",()=>{
   var b=new Board{Id="b",Pages=[new(){Id="p1"},new(){Id="p2"}],ActivePageId="p2"};
   try{DesktopPages.OnSourcePage(b,"p1",()=>{expect(b.ActivePageId=="p1","source page not selected");throw new IOException("fixture");});}catch(IOException){}
   expect(b.ActivePageId=="p2","temporary source selection leaked");
  });
  check("native-panel-icon-reads-image-content-and-bounds-dimensions",()=>{
   string path=Path.Combine(folder,"native-tray-image.png");
   var image=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255,0,255,0,255},8);
   void Save(BitmapSource bitmap){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);}
   Save(image);string uri=DesktopIcons.Read(new(){Name="tray",Target=path},true)??throw new Exception("image missing");
   using(var stream=new MemoryStream(Convert.FromBase64String(uri[22..]))){var decoded=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];expect(decoded.PixelWidth==2&&decoded.PixelHeight==1,"returned associated application icon");}
   Save(BitmapSource.Create(257,1,96,96,PixelFormats.Bgra32,null,new byte[257*4],257*4));expect(DesktopIcons.Read(new(){Target=path},true) is null,"oversized decoded dimensions accepted");
  });
 }
}
