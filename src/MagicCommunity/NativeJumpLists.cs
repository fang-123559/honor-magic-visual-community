using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MagicCommunity;

// All Shell objects stay inside the disposable STA Shell worker.
internal static partial class NativeJumpLists{
    internal sealed record Entry(string Id,string Label,string Path);
    internal sealed record TaskEntry(string Id,string Label);
    internal sealed record Result(string Aumid,Entry[] Recent,double Milliseconds,string Source="Windows Jump List")
    { public TaskEntry[] Tasks {get;init;}=[]; public Entry[] Pinned {get;init;}=[]; public Entry[] Frequent {get;init;}=[]; }
    internal static bool LocalPath(string path)=>path.Length is >3 and <=260&&char.IsAsciiLetter(path[0])&&path[1]==':'&&(path[2]=='\\'||path[2]=='/')&&path.IndexOf(':',2)<0&&!path.Any(char.IsControl)&&path.IndexOfAny(Path.GetInvalidPathChars())<0;
    internal static bool ValidId(string value)=>value.Length is >0 and <=512&&!value.Any(char.IsControl)&&!value.Contains('"');
    internal static string Key(string aumid,string path)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(aumid+"\n"+path.ToUpperInvariant())))[..32];
    private static string Text(JsonElement value,string key)=>value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    internal static Result Read(JsonElement request){
        var clock=Stopwatch.StartNew();string aumid=Text(request,"aumid"),path=Text(request,"shortcut"),executable=Text(request,"executable");
        if(aumid.Length==0&&LocalPath(path)&&File.Exists(path)&&Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))aumid=PropertyId(path,IntPtr.Zero);
        if(aumid.Length==0&&request.TryGetProperty("window",out var handle)&&handle.TryGetInt64(out long hwnd)&&hwnd!=0&&LocalPath(executable)){
            GetWindowThreadProcessId(new(hwnd),out uint pid);IntPtr process=OpenProcess(0x1000,false,pid);
            if(process!=IntPtr.Zero)try{var name=new StringBuilder(32768);uint count=(uint)name.Capacity;if(QueryFullProcessImageName(process,0,name,ref count)&&string.Equals(Path.GetFullPath(name.ToString()),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))aumid=PropertyId(null,new(hwnd));}finally{CloseHandle(process);}
        }
        if(IsWindowsExplorer(executable)||aumid==ExplorerId)return ReadExplorer(clock);
        if(!ValidId(aumid))return new("",[],clock.Elapsed.TotalMilliseconds);
        object? instance=null;IObjectArray? items=null;var found=new List<Entry>();
        try{
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("86bec222-30f2-47e0-9f25-60d11cd75c28"))!);
            var lists=(IApplicationDocumentLists)instance!;Marshal.ThrowExceptionForHR(lists.SetAppID(aumid));var iid=typeof(IObjectArray).GUID;
            Marshal.ThrowExceptionForHR(lists.GetList(0,8,ref iid,out items));Marshal.ThrowExceptionForHR(items.GetCount(out uint total));
            for(uint n=0;n<Math.Min(total,8);n++){IShellItem? item=null;try{
                var itemIid=typeof(IShellItem).GUID;if(items.GetAt(n,ref itemIid,out item)<0||item is null)continue;
                if(item.GetDisplayName(0x80058000,out IntPtr name)<0)continue;string target;try{target=Marshal.PtrToStringUni(name)??"";}finally{Marshal.FreeCoTaskMem(name);}
                if(!LocalPath(target)||!File.Exists(target))continue;target=Path.GetFullPath(target);
                string label=Path.GetFileName(target);if(label.Any(char.IsControl)||label.Length==0)continue;if(label.Length>120)label=label[..117]+"…";string key=Key(aumid,target);
                if(found.All(x=>x.Id!=key))found.Add(new(key,label,target));
            }finally{if(item is not null)Marshal.ReleaseComObject(item);}}
        }catch(COMException){ /* Recent may be unavailable while user tasks exist. */ }
        finally{if(items is not null)Marshal.ReleaseComObject(items);if(instance is not null)Marshal.ReleaseComObject(instance);}
        var tasks=UserTasks(aumid);
        return new(aumid,found.ToArray(),clock.Elapsed.TotalMilliseconds){Tasks=tasks};
    }
    private static string PropertyId(string? path,IntPtr window){
        IPropertyStore? store=null;var iid=typeof(IPropertyStore).GUID;try{
            int hr=window!=IntPtr.Zero?SHGetPropertyStoreForWindow(window,ref iid,out store):SHGetPropertyStoreFromParsingName(path!,IntPtr.Zero,0,ref iid,out store);if(hr<0||store is null)return "";
            var key=new PropertyKey{Format=new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"),Id=5};PropVariant value=default;
            try{if(store.GetValue(ref key,out value)<0||value.Type!=31||value.Pointer==IntPtr.Zero)return "";return Marshal.PtrToStringUni(value.Pointer)??"";}finally{PropVariantClear(ref value);}
        }finally{if(store is not null)Marshal.ReleaseComObject(store);}
    }
    internal static void ActivateStartTile(string aumid, string arguments)
    {
        if (!ValidId(aumid) || arguments.Length > 8192) throw new InvalidDataException("Windows 固定项无效。");
        var manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new("45ba127d-10a8-46ea-8ab7-56ea9078943c"))!)!;
        try { Marshal.ThrowExceptionForHR(((IApplicationActivationManager)manager).ActivateApplication(aumid, arguments, 0, out _)); }
        finally { Marshal.ReleaseComObject(manager); }
    }
    internal static object OpenPackage(JsonElement request,string id){
        if(Text(request,"aumid")==ExplorerId||IsWindowsExplorer(Text(request,"executable")))return OpenExplorer(request,id);
        if(id.StartsWith("task-",StringComparison.Ordinal)){
            string aumid=Text(request,"aumid");
            if(!ValidId(aumid)||UserTasks(aumid,id).Length!=1)throw new InvalidDataException("此任务已不在应用的跳转列表中。");
            return new{accepted=true};
        }
        var result=Read(request);var document=result.Recent.SingleOrDefault(x=>x.Id==id)??throw new InvalidDataException("最近项目已变化，请重新打开菜单。");
        if(!result.Aumid.Contains('!'))throw new InvalidDataException("不是打包应用。");IShellItem? item=null;object? instance=null;IntPtr array=IntPtr.Zero;
        try{var iid=typeof(IShellItem).GUID;Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(document.Path,IntPtr.Zero,ref iid,out item));var arrayIid=new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item,ref arrayIid,out array));instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("45ba127d-10a8-46ea-8ab7-56ea9078943c"))!);Marshal.ThrowExceptionForHR(((IApplicationActivationManager)instance!).ActivateForFile(result.Aumid,array,"open",out uint pid));return new{accepted=true,processId=pid,scope="Windows accepted activation; application rendering not verified"};}
        finally{if(array!=IntPtr.Zero)Marshal.Release(array);if(item is not null)Marshal.ReleaseComObject(item);if(instance is not null)Marshal.ReleaseComObject(instance);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey{internal Guid Format;internal uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct PropVariant{[FieldOffset(0)]internal ushort Type;[FieldOffset(8)]internal IntPtr Pointer;}
    [ComImport,Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IPropertyStore{
        [PreserveSig]int GetCount(out uint count);[PreserveSig]int GetAt(uint index,out PropertyKey key);[PreserveSig]int GetValue(ref PropertyKey key,out PropVariant value);
        [PreserveSig]int SetValue(ref PropertyKey key,ref PropVariant value);[PreserveSig]int Commit();
    }
    [ComImport,Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IApplicationDocumentLists{
        [PreserveSig]int SetAppID([MarshalAs(UnmanagedType.LPWStr)]string appId);[PreserveSig]int GetList(int type,uint count,ref Guid iid,out IObjectArray value);
    }
    [ComImport,Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IObjectArray{
        [PreserveSig]int GetCount(out uint count);[PreserveSig]int GetAt(uint index,ref Guid iid,out IShellItem value);
    }
    [ComImport,Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IShellItem{
        [PreserveSig]int BindToHandler(IntPtr bind,ref Guid handler,ref Guid iid,out IntPtr value);[PreserveSig]int GetParent(out IShellItem parent);
        [PreserveSig]int GetDisplayName(uint kind,out IntPtr value);[PreserveSig]int GetAttributes(uint mask,out uint attributes);[PreserveSig]int Compare(IShellItem item,uint hint,out int order);
    }
    [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IApplicationActivationManager{
        [PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint pid);
        [PreserveSig]int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr items,[MarshalAs(UnmanagedType.LPWStr)]string verb,out uint pid);
        [PreserveSig]int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr items,out uint pid);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,IntPtr bind,uint flags,ref Guid iid,out IPropertyStore store);
    [DllImport("shell32.dll")]private static extern int SHGetPropertyStoreForWindow(IntPtr window,ref Guid iid,out IPropertyStore store);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,IntPtr bind,ref Guid iid,out IShellItem item);
    [DllImport("shell32.dll")]private static extern int SHCreateShellItemArrayFromShellItem(IShellItem item,ref Guid iid,out IntPtr array);
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("kernel32.dll")]private static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint count);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
