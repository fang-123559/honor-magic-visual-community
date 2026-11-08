using System;
namespace MagicCommunity;
internal static class NativeAppsLayout
{
 internal static (int Columns,int Rows,int Capacity) Grid(double width,double height){
  if(!double.IsFinite(width)||!double.IsFinite(height)||width<240||height<240||width>1920||height>1440)throw new ArgumentOutOfRangeException();
  int columns=Math.Clamp((int)Math.Floor((width-28)/108),2,6);
  int rows=Math.Clamp((int)Math.Floor((height-150)/112),1,3);return(columns,rows,columns*rows);
 }
}
