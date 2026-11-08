using System.IO;
using System.Runtime.InteropServices;

namespace MagicCommunity;

// Windows owns the elevation UI and performs only this one requested move.
// Do not elevate the desktop process, suppress consent, or change destination ACLs.
public static class ShellFileMove
{
    private const uint RecycleFlags=0x40u|0x20000000u|0x80000u|0x4000u;
    // Only suppress Shell's progress window for a bounded, cheap local batch.
    // Keep its confirmation/error/elevation UI and undo/recycle semantics.
    internal static uint RecycleOperationFlags(IReadOnlyList<string> sources)
        =>RecycleFlags|(IsSmallLocalRecycle(sources)?0x4u:0u); // FOF_SILENT only
    private static bool IsSmallLocalRecycle(IReadOnlyList<string> sources)
    {
        if(sources.Count is <1 or >8)return false;
        long bytes=0;var checkedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedDrives=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach(string source in sources)
            {
                if(!Path.IsPathFullyQualified(source)||source.StartsWith(@"\",StringComparison.Ordinal))return false;
                string full=Path.GetFullPath(source),root=Path.GetPathRoot(full)!;
                if(checkedDrives.Add(root)&&new DriveInfo(root).DriveType!=DriveType.Fixed)return false;
                var attributes=File.GetAttributes(full);
                // Reparse/cloud/offline paths can perform remote work despite
                // a local-looking path. Do not walk inside them or hide progress.
                const FileAttributes remote=(FileAttributes)(0x400|0x1000|0x40000|0x400000);
                if((attributes&remote)!=0)return false;
                for(string? parent=Path.GetDirectoryName(full);!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent))
                {
                    if(!checkedDirectories.Add(parent))break;
                    if((File.GetAttributes(parent)&remote)!=0)return false;
                    if(watch.ElapsedMilliseconds>20)return false;
                }
                if((attributes&FileAttributes.Directory)!=0)
                {
                    // One entry is enough to decline; never recurse/count.
                    using var entries=Directory.EnumerateFileSystemEntries(full).GetEnumerator();
                    if(entries.MoveNext())return false;
                }
                else if((bytes+=new FileInfo(full).Length)>1024*1024)return false;
                if(watch.ElapsedMilliseconds>20)return false;
            }
            return true;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException){return false;}
    }
    public static string? Create(string directory,bool folder)
    {
        object? instance=null;IntPtr parent=IntPtr.Zero,sinkPointer=IntPtr.Zero;var sink=new CreationProgressSink();
        try
        {
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!);var op=(IFileOperation)instance!;
            Marshal.ThrowExceptionForHR(op.SetOperationFlags(0x8|0x40|0x20000000));Marshal.ThrowExceptionForHR(op.SetOwnerWindow(NativeDesktop.FindDesktopListView()));
            var iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory,IntPtr.Zero,ref iid,out parent));
            sinkPointer=Marshal.GetComInterfaceForObject(sink,typeof(ICreationProgressSink));
            Marshal.ThrowExceptionForHR(op.NewItem(parent,folder?0x10u:0x80u,folder?"新建文件夹":"新建文本文档.txt",null!,sinkPointer));
            Marshal.ThrowExceptionForHR(op.PerformOperations());Marshal.ThrowExceptionForHR(op.GetAnyOperationsAborted(out bool cancelled));if(cancelled)throw new OperationCanceledException("Windows 新建操作已取消。");
            if(sink.Result is <0)Marshal.ThrowExceptionForHR(sink.Result.Value);
            return sink.CreatedPath;
        }
        finally{if(sinkPointer!=IntPtr.Zero)Marshal.Release(sinkPointer);if(parent!=IntPtr.Zero)Marshal.Release(parent);if(instance is not null)Marshal.ReleaseComObject(instance);}
    }
    public static void Recycle(string source)=>Perform(source,null,true);
    public static void RecycleMany(IEnumerable<string> values)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Shell 文件操作需要 STA 线程。");
        string[] sources=ShellDesktop.ValidateSelection(values);
        foreach(string source in sources)
        {
            if(source.StartsWith("::",StringComparison.Ordinal)||(!File.Exists(source)&&!Directory.Exists(source)))throw new LayoutEditException("所选项目含系统入口或已不存在，未开始回收操作。");
            if(Directory.Exists(source))
            {
                string folder=Path.TrimEndingDirectorySeparator(source),prefix=Path.EndsInDirectorySeparator(folder)?folder:folder+Path.DirectorySeparatorChar;
                if(sources.Any(other=>!StringComparer.OrdinalIgnoreCase.Equals(other,source)&&other.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new LayoutEditException("选择同时包含文件夹和其内部项目，请分开回收。");
            }
        }
        object? instance=null;var items=new List<IntPtr>();
        try
        {
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!);var operation=(IFileOperation)instance!;
            // ALLOWUNDO | ADDUNDORECORD | RECYCLEONDELETE | WANTNUKEWARNING.
            // Request recycling explicitly, without a context-menu extension
            // interpreting the current physical Shift state. Keep Windows'
            // confirmation when a source cannot be recycled.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(RecycleOperationFlags(sources)));
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(NativeDesktop.FindDesktopListView()));
            var iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            foreach(string source in sources){Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source,IntPtr.Zero,ref iid,out var item));items.Add(item);}
            // Queue the complete validated batch before one commit. A queue
            // failure releases it without starting any physical operation.
            foreach(IntPtr item in items)Marshal.ThrowExceptionForHR(operation.DeleteItem(item,IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if(aborted||sources.Any(source=>File.Exists(source)||Directory.Exists(source)))throw new OperationCanceledException("Windows 未回收全部选中项目；未完成的项目仍在原处。");
        }
        finally{foreach(IntPtr item in items)if(item!=IntPtr.Zero)Marshal.Release(item);if(instance is not null)Marshal.ReleaseComObject(instance);}
    }
    public static void Rename(string source,string name)=>Perform(source,name,false);
    public static string[] MoveClipboardItems(System.Runtime.InteropServices.ComTypes.IDataObject data,string destination,bool move=true)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Shell 文件操作需要 STA 线程。");
        if(!Path.IsPathFullyQualified(destination)||!Directory.Exists(destination))throw new LayoutEditException("粘贴目标不是有效文件夹。");
        object? instance=null;IntPtr selection=IntPtr.Zero,folder=IntPtr.Zero,sinkPointer=IntPtr.Zero;uint cookie=0;var sink=new CreationProgressSink();
        try
        {
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!);var operation=(IFileOperation)instance!;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x40u|0x20000000u));
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(NativeDesktop.FindDesktopListView()));
            var iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination,IntPtr.Zero,ref iid,out folder));
            // IFileOperation accepts the original IDataObject. Do not turn an
            // Explorer cut into a simulated drag or reconstruct only item 1.
            selection=Marshal.GetIUnknownForObject(data);
            sinkPointer=Marshal.GetComInterfaceForObject<CreationProgressSink,ICreationProgressSink>(sink);Marshal.ThrowExceptionForHR(operation.Advise(sinkPointer,out cookie));
            Marshal.ThrowExceptionForHR(move?operation.MoveItems(selection,folder):operation.CopyItems(selection,folder));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if(aborted)throw new OperationCanceledException("Windows 未完成全部粘贴；未完成的项目仍在原处。");
            return sink.CreatedPaths.ToArray();
        }
        finally{if(cookie!=0&&instance is not null)((IFileOperation)instance).Unadvise(cookie);if(sinkPointer!=IntPtr.Zero)Marshal.Release(sinkPointer);if(selection!=IntPtr.Zero)Marshal.Release(selection);if(folder!=IntPtr.Zero)Marshal.Release(folder);if(instance is not null)Marshal.ReleaseComObject(instance);}
    }
    private static void Perform(string source,string? name,bool recycle)
    {
        object? instance=null;IntPtr item=IntPtr.Zero;
        try
        {
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!);var op=(IFileOperation)instance!;
            // Normal Windows confirmations, undo records and elevation; recycle
            // instead of permanent deletion. Never suppress overwrite prompts.
            Marshal.ThrowExceptionForHR(op.SetOperationFlags(recycle?RecycleOperationFlags([source]):0x40u|0x20000000u));
            Marshal.ThrowExceptionForHR(op.SetOwnerWindow(NativeDesktop.FindDesktopListView()));
            var iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source,IntPtr.Zero,ref iid,out item));
            Marshal.ThrowExceptionForHR(recycle?op.DeleteItem(item,IntPtr.Zero):op.RenameItem(item,name!,IntPtr.Zero));
            Marshal.ThrowExceptionForHR(op.PerformOperations());Marshal.ThrowExceptionForHR(op.GetAnyOperationsAborted(out bool cancelled));
            if(cancelled)throw new OperationCanceledException("Windows 文件操作已取消。");
        }
        finally{if(item!=IntPtr.Zero)Marshal.Release(item);if(instance is not null)Marshal.ReleaseComObject(instance);}
    }
    public static void Move(string source,string destination,bool userAction=false)
    {
        if(File.Exists(destination)||Directory.Exists(destination))throw new LayoutEditException("目标位置已有同名项目，未覆盖。");
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Shell 文件操作需要 STA 线程。");
        object? instance=null;IntPtr sourceItem=IntPtr.Zero,folderItem=IntPtr.Zero;
        try
        {
            instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!);var operation=(IFileOperation)instance!;
            // SILENT | NOERRORUI | NO_CONNECTED_ELEMENTS | SHOWELEVATIONPROMPT | EARLYFAILURE.
            // No NOCONFIRMATION, no rename-on-collision, no ACL modification.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(userAction?0x40u|0x20000000u:0x4|0x400|0x2000|0x40000|0x100000));
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(NativeDesktop.FindDesktopListView()));
            var iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source,IntPtr.Zero,ref iid,out sourceItem));
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetDirectoryName(destination)!,IntPtr.Zero,ref iid,out folderItem));
            Marshal.ThrowExceptionForHR(operation.MoveItem(sourceItem,folderItem,Path.GetFileName(destination),IntPtr.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out bool aborted));
            if(aborted||File.Exists(source)||Directory.Exists(source)||(!File.Exists(destination)&&!Directory.Exists(destination)))throw new LayoutEditException("Windows 文件操作未完成或已取消，项目保留在收纳目录。");
        }
        finally
        {
            if(sourceItem!=IntPtr.Zero)Marshal.Release(sourceItem);if(folderItem!=IntPtr.Zero)Marshal.Release(folderItem);
            if(instance is not null&&Marshal.IsComObject(instance))Marshal.ReleaseComObject(instance);
        }
    }
    // PostNewItem identifies this operation's exact result, including Windows'
    // collision-renamed name. Never infer it from all newly visible files.
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class CreationProgressSink:ICreationProgressSink
    {
        public string? CreatedPath{get;private set;}
        public List<string> CreatedPaths{get;}=[];
        private void Capture(int result,IntPtr created){if(result<0||created==IntPtr.Zero)return;IntPtr pidl=IntPtr.Zero,path=IntPtr.Zero;try{if(SHGetIDListFromObject(created,out pidl)>=0&&SHGetNameFromIDList(pidl,0x80058000,out path)>=0&&Marshal.PtrToStringUni(path) is { } value)CreatedPaths.Add(value);}finally{if(path!=IntPtr.Zero)Marshal.FreeCoTaskMem(path);if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);}}
        public int? Result{get;private set;}
        public int StartOperations()=>0;public int FinishOperations(int result)=>0;
        public int PreRenameItem(uint flags,IntPtr item,IntPtr name)=>0;
        public int PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created)=>0;
        public int PreMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name)=>0;
        public int PostMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created){Capture(result,created);return 0;}
        public int PreCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name)=>0;
        public int PostCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created){Capture(result,created);return 0;}
        public int PreDeleteItem(uint flags,IntPtr item)=>0;
        public int PostDeleteItem(uint flags,IntPtr item,int result,IntPtr created)=>0;
        public int PreNewItem(uint flags,IntPtr destination,IntPtr name)=>0;
        public int PostNewItem(uint flags,IntPtr destination,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created)
        {
            Result=result;if(result<0||created==IntPtr.Zero)return 0;
            IntPtr pidl=IntPtr.Zero,path=IntPtr.Zero;
            try
            {
                if(SHGetIDListFromObject(created,out pidl)>=0&&SHGetNameFromIDList(pidl,0x80058000,out path)>=0)
                    CreatedPath=Marshal.PtrToStringUni(path);
            }
            finally{if(path!=IntPtr.Zero)Marshal.FreeCoTaskMem(path);if(pidl!=IntPtr.Zero)Marshal.FreeCoTaskMem(pidl);}
            return 0;
        }
        public int UpdateProgress(uint total,uint done)=>0;
        public int ResetTimer()=>0;public int PauseTimer()=>0;public int ResumeTimer()=>0;
    }
    [ComVisible(true),Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICreationProgressSink
    {
        [PreserveSig]int StartOperations();[PreserveSig]int FinishOperations(int result);
        [PreserveSig]int PreRenameItem(uint flags,IntPtr item,IntPtr name);
        [PreserveSig]int PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created);
        [PreserveSig]int PreMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name);
        [PreserveSig]int PostMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created);
        [PreserveSig]int PreCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name);
        [PreserveSig]int PostCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created);
        [PreserveSig]int PreDeleteItem(uint flags,IntPtr item);
        [PreserveSig]int PostDeleteItem(uint flags,IntPtr item,int result,IntPtr created);
        [PreserveSig]int PreNewItem(uint flags,IntPtr destination,IntPtr name);
        [PreserveSig]int PostNewItem(uint flags,IntPtr destination,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created);
        [PreserveSig]int UpdateProgress(uint total,uint done);
        [PreserveSig]int ResetTimer();[PreserveSig]int PauseTimer();[PreserveSig]int ResumeTimer();
    }
    [DllImport("shell32.dll")]private static extern int SHGetIDListFromObject(IntPtr item,out IntPtr pidl);
    [DllImport("shell32.dll")]private static extern int SHGetNameFromIDList(IntPtr pidl,uint name,out IntPtr path);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]private static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out IntPtr item);
    [ComImport,Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IFileOperation
    {
        [PreserveSig]int Advise(IntPtr sink,out uint cookie);[PreserveSig]int Unadvise(uint cookie);
        [PreserveSig]int SetOperationFlags(uint flags);[PreserveSig]int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)]string message);
        [PreserveSig]int SetProgressDialog(IntPtr dialog);[PreserveSig]int SetProperties(IntPtr properties);[PreserveSig]int SetOwnerWindow(IntPtr hwnd);
        [PreserveSig]int ApplyPropertiesToItem(IntPtr item);[PreserveSig]int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig]int RenameItem(IntPtr item,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);[PreserveSig]int RenameItems(IntPtr items,[MarshalAs(UnmanagedType.LPWStr)]string name);
        [PreserveSig]int MoveItem(IntPtr item,IntPtr folder,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);[PreserveSig]int MoveItems(IntPtr items,IntPtr folder);
        [PreserveSig]int CopyItem(IntPtr item,IntPtr folder,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr sink);[PreserveSig]int CopyItems(IntPtr items,IntPtr folder);
        [PreserveSig]int DeleteItem(IntPtr item,IntPtr sink);[PreserveSig]int DeleteItems(IntPtr items);
        [PreserveSig]int NewItem(IntPtr folder,uint attributes,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string template,IntPtr sink);
        [PreserveSig]int PerformOperations();[PreserveSig]int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
    }
}
