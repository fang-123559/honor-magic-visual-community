using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MagicCommunity;

// One menu lease, on the menu worker's STA. No installed-app index or polling.
internal sealed class ShellAssociationMenu : IDisposable
{
    internal const int RootIndex=600000;
    private readonly List<(Handler Handler,string Label,bool Recommended)> handlers=[];
    private DesktopContextMenu.Entry[]? rows;
    internal static bool Supports(string[] targets)=>targets.Length==1 && Path.IsPathFullyQualified(targets[0])
        && !targets[0].StartsWith(@"\\",StringComparison.Ordinal) && File.Exists(targets[0])
        && !new[]{".lnk",".exe",".com",".bat",".cmd"}.Contains(Path.GetExtension(targets[0]),StringComparer.OrdinalIgnoreCase);
    internal DesktopContextMenu.Entry[] Children(string[] targets)
    {
        if(rows is not null)return rows;
        ShellOpenWith.Request(targets); // Revalidate before consulting Windows.
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string extension=Path.GetExtension(targets[0]);
        try{
            if(extension.Length>0){
                Marshal.ThrowExceptionForHR(SHAssocEnumHandlers(extension,0,out var iterator));
                try{
                    for(int count=0;count<64;count++){
                        int hr=iterator.Next(1,out var handler,out uint fetched);Marshal.ThrowExceptionForHR(hr);
                        if(fetched==0)break;
                        bool keep=false;
                        try{
                            string name=Text(handler.GetName(out var value),value);
                            string label=Text(handler.GetUIName(out value),value);
                            if(name.Length is 0 or >2048 || label.Length is 0 or >512 || !names.Add(name))continue;
                            handlers.Add((handler,label,handler.IsRecommended()==0));keep=true;
                        }catch(COMException){/* A broken registration must not hide other applications. */}
                        finally{if(!keep)Marshal.ReleaseComObject(handler);}
                    }
                }finally{Marshal.ReleaseComObject(iterator);}
            }
            var sorted=handlers.OrderByDescending(h=>h.Recommended).ThenBy(h=>h.Label,StringComparer.CurrentCultureIgnoreCase).ToArray();
            handlers.Clear();handlers.AddRange(sorted);
            rows=handlers.Select((h,i)=>new DesktopContextMenu.Entry(i,h.Label,false,false,false,false,Key:"application")).ToArray();
            return rows=rows.Concat(new[]{new DesktopContextMenu.Entry(handlers.Count,"选择其他应用…",false,false,false,false,Key:"openas",Verb:"openas")}).ToArray();
        }catch{Dispose();throw;}
    }
    internal void Invoke(string[] targets,int[] path)
    {
        if(path.Length!=2 || rows is null || path[1]<0 || path[1]>handlers.Count)throw new InvalidDataException("打开方式菜单已失效，请重新打开。");
        ShellOpenWith.Request(targets);
        if(path[1]==handlers.Count){ShellOpenWith.Show(targets);return;}
        object data=ShellDesktop.ContextObjects(targets,typeof(IDataObject).GUID,unowned:true);
        try{Marshal.ThrowExceptionForHR(handlers[path[1]].Handler.Invoke((IDataObject)data));}
        finally{Marshal.ReleaseComObject(data);}
    }
    private static string Text(int hr,IntPtr value){try{Marshal.ThrowExceptionForHR(hr);return Marshal.PtrToStringUni(value)??"";}finally{if(value!=IntPtr.Zero)Marshal.FreeCoTaskMem(value);}}
    public void Dispose(){rows=null;foreach(var h in handlers)Marshal.ReleaseComObject(h.Handler);handlers.Clear();}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHAssocEnumHandlers(string extension,uint filter,out Enumerator handlers);
    [ComImport,Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Enumerator{
        [PreserveSig]int Next(uint count,[MarshalAs(UnmanagedType.Interface)]out Handler handler,out uint fetched);
    }
    [ComImport,Guid("F04061AC-1659-4A3F-A954-775AA57FC083"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Handler{
        [PreserveSig]int GetName(out IntPtr value);[PreserveSig]int GetUIName(out IntPtr value);[PreserveSig]int GetIconLocation(out IntPtr value,out int index);[PreserveSig]int IsRecommended();
        [PreserveSig]int MakeDefault([MarshalAs(UnmanagedType.LPWStr)]string description);
        [PreserveSig]int Invoke([MarshalAs(UnmanagedType.Interface)]IDataObject data);
        [PreserveSig]int CreateInvoker([MarshalAs(UnmanagedType.Interface)]IDataObject data,out IntPtr invoker);
    }
}
