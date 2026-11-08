using System.Runtime.InteropServices;

namespace MagicCommunity;

// The Shell New handler reports the exact created PIDL through this service.
// Attach only for New commands; other extensions keep their original site.
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class ShellNewItemSite:ShellNewItemSite.INewMenuClient,ShellNewItemSite.IServiceProvider
{
    private IServiceProvider? fallback;
    public string? CreatedPath{get;private set;}
    public static ShellNewItemSite? Attach(object menu)
    {
        if(menu is not IObjectWithSite site)return null;
        var result=new ShellNewItemSite();IntPtr pointer=IntPtr.Zero;
        try
        {
            var iid=typeof(IServiceProvider).GUID;
            if(site.GetSite(ref iid,out pointer)>=0&&pointer!=IntPtr.Zero)result.fallback=(IServiceProvider)Marshal.GetObjectForIUnknown(pointer);
            return site.SetSite(result)>=0?result:null;
        }
        finally{if(pointer!=IntPtr.Zero)Marshal.Release(pointer);}
    }
    public int QueryService(ref Guid service,ref Guid iid,out IntPtr value)
    {
        value=IntPtr.Zero;
        if(service==typeof(INewMenuClient).GUID&&iid==typeof(INewMenuClient).GUID){value=Marshal.GetComInterfaceForObject(this,typeof(INewMenuClient));return 0;}
        return fallback?.QueryService(ref service,ref iid,out value)??unchecked((int)0x80004002);
    }
    public int IncludeItems(out uint flags){flags=3;return 0;}
    public int SelectAndEditItem(IntPtr pidl,uint flags)
    {
        IntPtr value=IntPtr.Zero;
        try{if(SHGetNameFromIDList(pidl,0x80058000,out value)>=0)CreatedPath=Marshal.PtrToStringUni(value);}
        finally{if(value!=IntPtr.Zero)Marshal.FreeCoTaskMem(value);}
        return 0;
    }
    [DllImport("shell32.dll")]private static extern int SHGetNameFromIDList(IntPtr pidl,uint name,out IntPtr path);
    [ComImport,Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        [PreserveSig]int SetSite([MarshalAs(UnmanagedType.IUnknown)]object site);
        [PreserveSig]int GetSite(ref Guid iid,out IntPtr value);
    }
    [ComVisible(true),Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProvider{[PreserveSig]int QueryService(ref Guid service,ref Guid iid,out IntPtr value);}
    [ComVisible(true),Guid("dcb07fdc-3bb5-451c-90be-966644fed7b0"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INewMenuClient
    {
        [PreserveSig]int IncludeItems(out uint flags);
        [PreserveSig]int SelectAndEditItem(IntPtr pidl,uint flags);
    }
}
