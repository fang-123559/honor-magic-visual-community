using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace MagicCommunity;

internal static partial class NativeJumpLists
{
    // IApplicationDocumentLists exposes only Recent/Frequent. Probe the Windows
    // 10+ read interface on the same Shell destination list. Unsupported builds
    // simply omit Tasks. Never BeginList, CommitList or alter an app's list.
    [ComImport,Guid("507101CD-F6AD-46C8-8E20-EEB9E6BAC47F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationReader
    {
        void Unused0();
        [PreserveSig]int SetAppId([MarshalAs(UnmanagedType.LPWStr)]string id);
        void Unused2();
        [PreserveSig]int Count(out uint count);
        [PreserveSig]int Category(uint index,int flags,out CategoryDescriptor category);
        void Unused5();
        [PreserveSig]int Entries(uint index,ref Guid iid,out ITaskArray items);
    }
    [StructLayout(LayoutKind.Sequential,Size=96)]private struct CategoryDescriptor { public int Type;public IntPtr Name; }
    [ComImport,Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskArray { [PreserveSig]int GetCount(out uint count); [PreserveSig]int GetAt(uint index,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object value); }
    [ComImport,Guid("00000109-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistedTask
    {
        void GetClassID(out Guid id);[PreserveSig]int IsDirty();void Load(IStream stream);
        void Save(IStream stream,[MarshalAs(UnmanagedType.Bool)]bool clearDirty);void GetSizeMax(out long length);
    }
    [DllImport("ole32.dll")]private static extern int CreateStreamOnHGlobal(IntPtr data,bool deleteOnRelease,out IStream stream);
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int SHLoadIndirectString(string source,StringBuilder result,uint capacity,IntPtr reserved);
    private static string TaskTitle(object link)
    {
        if(link is not IPropertyStore store)return "";
        var key=new PropertyKey{Format=new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),Id=2}; // System.Title
        PropVariant value=default;
        try {
            if(store.GetValue(ref key,out value)<0||value.Type!=31)return "";
            var label=Marshal.PtrToStringUni(value.Pointer)??"";
            if(label.StartsWith('@')){var resolved=new StringBuilder(512);if(SHLoadIndirectString(label,resolved,512,IntPtr.Zero)<0)return "";label=resolved.ToString();}
            return label.Length is >0 and <=512&&!label.Any(char.IsControl)?label:"";
        } finally {PropVariantClear(ref value);}
    }
    private static string TaskId(string app,IPersistedTask link)
    {
        link.GetSizeMax(out long max);if(max<0||max>262144)return "";
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero,true,out var stream));
        try {
            link.Save(stream,false);stream.Stat(out var stat,1);
            if(stat.cbSize is <=0 or >262144)return "";
            byte[] bytes=new byte[checked((int)stat.cbSize)];
            stream.Seek(0,0,IntPtr.Zero);stream.Read(bytes,bytes.Length,IntPtr.Zero);
            return "task-"+Key(app,Convert.ToHexString(SHA256.HashData(bytes)));
        } finally {Marshal.ReleaseComObject(stream);}
    }
    private static TaskEntry[] UserTasks(string app,string? invoke=null)
    {
        var result=new List<TaskEntry>();object? list=null;ITaskArray? tasks=null;
        try {
            list=Activator.CreateInstance(Type.GetTypeFromCLSID(new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6"))!);
            if(list is not IDestinationReader reader||reader.SetAppId(app)<0||reader.Count(out uint categories)<0)return [];
            for(uint i=0;i<Math.Min(32u,categories);i++){
                if(reader.Category(i,1,out var category)<0)continue;
                if(category.Type==0&&category.Name!=IntPtr.Zero)Marshal.FreeCoTaskMem(category.Name);
                if(category.Type!=2)continue;
                var arrayGuid=new Guid("5632B1A4-E38A-400A-928A-D4CD63230295"); // IObjectCollection
                if(reader.Entries(i,ref arrayGuid,out tasks)<0||tasks.GetCount(out uint count)<0)break;
                var linkGuid=new Guid("000214F9-0000-0000-C000-000000000046");
                for(uint j=0;j<Math.Min(32u,count)&&result.Count<8;j++){
                    if(tasks.GetAt(j,ref linkGuid,out var link)<0)continue;
                    try {
                        var label=TaskTitle(link);
                        if(label.Length==0||link is not IPersistedTask saved||!DesktopContextMenu.HasDefaultAction(link))continue;
                        string key=TaskId(app,saved);
                        if(key.Length==0||result.Any(x=>x.Id==key))continue;
                        var entry=new TaskEntry(key,label.Length>120?label[..117]+"…":label);
                        if(invoke is null)result.Add(entry);
                        else if(key==invoke){DesktopContextMenu.InvokeDefaultAction(link);return [entry];}
                    } finally {Marshal.ReleaseComObject(link);}
                }
                break;
            }
        } catch(COMException) when(invoke is null) { }
        finally {if(tasks is not null)Marshal.ReleaseComObject(tasks);if(list is not null)Marshal.ReleaseComObject(list);}
        return result.ToArray();
    }
}
