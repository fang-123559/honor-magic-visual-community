using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MagicCommunity;

// Stock file transfer uses native Shell data/drop objects, not a context-menu
// aggregate. Windows retains collision UI, permissions, undo and move semantics.
public static class ShellClipboardCommands
{
    private static readonly Guid DataId=new("0000010E-0000-0000-C000-000000000046"),DropId=new("00000122-0000-0000-C000-000000000046");
    public static object Execute(string action,string? source,bool prepareOnly=false,string[]? sources=null)
    {
        if(action is not ("copy" or "cut" or "paste"))throw new InvalidDataException();
        var watch=Stopwatch.StartNew();object? native=null;IDataObject? clipboard=null;
        try
        {
            string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            native=action=="paste"?(prepareOnly?ShellDesktop.ContextObject(desktop,DropId,unowned:true):null):ShellDesktop.ContextObjects(sources??[source??throw new InvalidDataException()],DataId,unowned:true);
            double bindMs=watch.Elapsed.TotalMilliseconds,invokeMs=0,flushMs=0;uint? requestedEffect=null;string? pasteMode=null;string[] createdPaths=[];
            if(!prepareOnly)
            {
                if(action=="paste")
                {
                    Marshal.ThrowExceptionForHR(OleGetClipboard(out clipboard));uint effect=ReadEffect(clipboard);requestedEffect=effect;
                    pasteMode=effect==2?"file-operation-move":"file-operation-copy";
                    createdPaths=ShellFileMove.MoveClipboardItems(clipboard,desktop,move:effect==2);
                    if(effect==2){TryEffect(clipboard,"Performed DropEffect",0);TryEffect(clipboard,"Logical Performed DropEffect",2);}
                    TryEffect(clipboard,"Paste Succeeded",effect==2?2u:1u);
                }
                else
                {
                    var data=(IDataObject)(native??throw new InvalidDataException());SetEffect(data,"Preferred DropEffect",action=="cut"?2u:1u);Marshal.ThrowExceptionForHR(OleSetClipboard(data));
                }
                invokeMs=watch.Elapsed.TotalMilliseconds-bindMs;
                if(action is "copy" or "cut")
                {
                    int status;for(int retry=0;;retry++){status=OleFlushClipboard();if(status!=unchecked((int)0x800401D0)||retry==9)break;Thread.Sleep(20);}
                    Marshal.ThrowExceptionForHR(status);
                }
                flushMs=watch.Elapsed.TotalMilliseconds-bindMs-invokeMs;
            }
            return new{ok=true,bindMs,invokeMs,flushMs,prepareOnly,requestedEffect,pasteMode,createdPaths};
        }
        finally{if(clipboard is not null)Marshal.ReleaseComObject(clipboard);if(native is not null)Marshal.ReleaseComObject(native);}
    }
    internal static void MoveClipboardData(IDataObject data,string destination)
    {
        ShellFileMove.MoveClipboardItems(data,destination);
        // MoveItems has already physically moved the source. NONE prevents the
        // cut owner deleting it again; logical MOVE and Paste Succeeded finish
        // its UI state. Never acknowledge cancellation/partial failure.
        TryEffect(data,"Performed DropEffect",0);
        TryEffect(data,"Logical Performed DropEffect",2);
        TryEffect(data,"Paste Succeeded",2);
    }
    private sealed class DropNotAcceptedException():InvalidOperationException("Windows 不接受该目标的拖放操作。");
    private static uint Drop(IDropTarget target,IDataObject data,uint effect,uint? keyState=null,Action? committing=null)
    {
        uint preferred=effect,keys=keyState??(1u|(effect==2?4u:8u));var point=new Point();bool entered=false;
        try
        {
            Marshal.ThrowExceptionForHR(target.DragEnter(data,keys,point,ref effect));entered=true;
            if((effect&preferred)==0)throw new DropNotAcceptedException();
            effect=preferred;entered=false;committing?.Invoke();Marshal.ThrowExceptionForHR(target.Drop(data,keys,point,ref effect));
            // A native optimized move may return NONE even after moving the
            // source. Do not misreport failure or notify source deletion twice.
            if(effect!=0)return effect;
            try{return ReadValue(data,"Logical Performed DropEffect")??0;}catch(Exception error)when(error is COMException or NotImplementedException or NotSupportedException){return 0;}
        }
        finally{if(entered)target.DragLeave();}
    }
    // Incoming Tauri file paths are converted to Shell objects in a temporary
    // STA worker. This is independent of, and never writes, the clipboard.
    public static object TransferFiles(string[] paths,string destination,bool control,bool shift)
    {
        using var ole=new OleScope();
        string[] sources=ValidateIncoming(paths,destination);int handled=0;
        foreach(string source in sources)
        {
            if(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(source),destination))continue;
            object? data=null,target=null;
            try
            {
                data=ShellDesktop.ContextObject(source,DataId,unowned:true);
                target=ShellDesktop.ContextObject(destination,DropId,unowned:true);
                uint effect=IncomingEffect(source,destination,control,shift);
                SetEffect((IDataObject)data,"Preferred DropEffect",effect);
                using(var lifetime=new ShellCommandLifetime())Drop((IDropTarget)target,(IDataObject)data,effect);
                handled++;
            }
            finally{if(target is not null)Marshal.ReleaseComObject(target);if(data is not null)Marshal.ReleaseComObject(data);}
        }
        return new{ok=true,handled};
    }
    public static uint IncomingEffect(string source,string destination,bool control,bool shift)
        =>shift?2u:control?1u:StringComparer.OrdinalIgnoreCase.Equals(Path.GetPathRoot(source),Path.GetPathRoot(destination))?2u:1u;
    public const string RecycleBinTarget="::{645FF040-5081-101B-9F08-00AA002F954E}";
    public static bool IsRecycleDestination(string destination)=>StringComparer.OrdinalIgnoreCase.Equals(destination,RecycleBinTarget);
    public static uint SelectionEffect(IEnumerable<string> sources,string destination,bool control,bool shift)
        =>IsRecycleDestination(destination)||shift?2u:control?1u:sources.All(source=>StringComparer.OrdinalIgnoreCase.Equals(Path.GetPathRoot(source),Path.GetPathRoot(destination)))?2u:1u;
    public static string[] ValidateSelectionTransfer(string[] paths,string destination)
    {
        string[] sources=ShellDesktop.ValidateSelection(paths).Select(source=>Path.TrimEndingDirectorySeparator(source)).ToArray();
        bool recycle=IsRecycleDestination(destination);
        if(!recycle&&(!Path.IsPathFullyQualified(destination)||!Directory.Exists(destination)))throw new LayoutEditException("拖放目标不是有效文件夹。");
        string normalized=recycle?destination:Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        foreach(string source in sources)
        {
            if(source.StartsWith("::",StringComparison.Ordinal)||(!File.Exists(source)&&!Directory.Exists(source)))throw new LayoutEditException("所选项目含系统入口或已不存在，未开始文件操作。");
            if(!Directory.Exists(source))continue;
            string folder=Path.TrimEndingDirectorySeparator(source);
            string prefix=Path.EndsInDirectorySeparator(folder)?folder:folder+Path.DirectorySeparatorChar;
            if(!recycle&&(StringComparer.OrdinalIgnoreCase.Equals(folder,normalized)||normalized.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new LayoutEditException("不能把文件夹移入自身或子目录。");
            if(sources.Any(other=>!StringComparer.OrdinalIgnoreCase.Equals(other,source)&&other.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))throw new LayoutEditException("选择同时包含文件夹和其内部项目，请分开拖动。");
        }
        return sources;
    }
    // One native selection and one destination. No global clipboard writes,
    // no per-item drop loop, and normal Windows conflict/permission handling.
    public static object TransferSelection(string[] paths,string destination,bool control,bool shift)
    {
        using var ole=new OleScope();string[] sources=ValidateSelectionTransfer(paths,destination);
        bool recycle=IsRecycleDestination(destination);destination=recycle?RecycleBinTarget:Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        uint effect=SelectionEffect(sources,destination,control,shift);
        if(!recycle&&effect==2)sources=sources.Where(source=>!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(source),destination)).ToArray();
        if(sources.Length==0)return new{ok=true,handled=0,effect,performed=0u,recycled=false};
        // Recycle Bin's drop target can return before directories disappear.
        // Use the explicit batch's completion/aborted result, never an immediate
        // post-Drop existence check or a delayed retry of the same operation.
        if(recycle)return RecycleSelection(sources);
        object? data=null,target=null;
        try
        {
            data=ShellDesktop.ContextObjects(sources,DataId,unowned:true);SetEffect((IDataObject)data,"Preferred DropEffect",effect);
            target=ShellDesktop.ContextObject(destination,DropId,unowned:true);
            uint performed;using(var lifetime=new ShellCommandLifetime())performed=Drop((IDropTarget)target,(IDataObject)data,effect);
            if(effect==2&&sources.Any(source=>File.Exists(source)||Directory.Exists(source)))throw new OperationCanceledException("Windows 未移动全部选中项目；未完成的项目仍在原处。");
            return new{ok=true,handled=sources.Length,effect,performed,recycled=recycle};
        }
        finally{if(target is not null)Marshal.ReleaseComObject(target);if(data is not null)Marshal.ReleaseComObject(data);}
    }
    private static object RecycleSelection(string[] sources)
    {
        // One explicitly configured IFileOperation batch owns completion and
        // avoids a third-party menu interpreting physical Shift as delete.
        using(var lifetime=new ShellCommandLifetime())ShellFileMove.RecycleMany(sources);
        if(sources.Any(source=>File.Exists(source)||Directory.Exists(source)))throw new OperationCanceledException("Windows 未回收全部选中项目；未完成的项目仍在原处。");
        return new{ok=true,handled=sources.Length,effect=2u,performed=2u,recycled=true};
    }
    public static string[] ValidateIncoming(string[] paths,string destination)
    {
        if(!Path.IsPathFullyQualified(destination)||!Directory.Exists(destination)||paths.Length is <1 or >100)throw new InvalidDataException();
        destination=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var sources=paths.Select(path=>Path.IsPathFullyQualified(path)?Path.GetFullPath(path):throw new InvalidDataException()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(string path in sources)
        {
            if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("拖入的源项目已不存在。");
            if(Directory.Exists(path)&&(StringComparer.OrdinalIgnoreCase.Equals(path.TrimEnd('\\'),destination)||destination.StartsWith(path.TrimEnd('\\')+'\\',StringComparison.OrdinalIgnoreCase)))throw new LayoutEditException("不能把文件夹移入自身或子目录。");
        }
        return sources;
    }
    private static uint ReadEffect(IDataObject data)
        =>((ReadValue(data,"Preferred DropEffect")??1)&2)!=0?2u:1u;
    private static uint? ReadValue(IDataObject data,string name)
    {
        var format=Format(name);if(data.QueryGetData(ref format)<0)return null;
        data.GetData(ref format,out var medium);
        try
        {
            if(medium.tymed!=TYMED.TYMED_HGLOBAL||GlobalSize(medium.unionmember).ToUInt64()<4)return null;
            IntPtr bits=GlobalLock(medium.unionmember);if(bits==IntPtr.Zero)throw new Win32Exception();
            try{return (uint)Marshal.ReadInt32(bits);}finally{GlobalUnlock(medium.unionmember);}
        }
        finally{ReleaseStgMedium(ref medium);}
    }
    private static FORMATETC Format(string name)=>new(){cfFormat=unchecked((short)RegisterClipboardFormat(name)),dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
    private static void TryEffect(IDataObject data,string name,uint effect){try{SetEffect(data,name,effect);}catch(Exception error)when(error is COMException or NotImplementedException or NotSupportedException or ArgumentException){/* Some external clipboard wrappers are read-only. */}}
    private static void SetEffect(IDataObject data,string name,uint effect)
    {
        var format=Format(name);IntPtr memory=GlobalAlloc(0x42,new UIntPtr(4));if(memory==IntPtr.Zero)throw new OutOfMemoryException();
        var medium=new STGMEDIUM{tymed=TYMED.TYMED_HGLOBAL,unionmember=memory};bool transferred=false;
        try
        {
            IntPtr bits=GlobalLock(memory);if(bits==IntPtr.Zero)throw new Win32Exception();
            try{Marshal.WriteInt32(bits,(int)effect);}finally{GlobalUnlock(memory);}
            data.SetData(ref format,ref medium,true);transferred=true;
        }
        finally{if(!transferred)ReleaseStgMedium(ref medium);}
    }
    // Native payload tests never call OleSetClipboard/OleGetClipboard/Flush.
    public static object TestPayloads(string root)
    {
        using var ole=new OleScope();
        string directory=Path.GetFullPath(Path.Combine(root,"evidence/desktop-feedback/2026-10-17","drop-payload-"+Guid.NewGuid().ToString("N"))),destination=Path.Combine(directory,"B");Directory.CreateDirectory(destination);
        var checks=new List<object>();
        foreach(bool move in new[]{false,true})
        {
            string source=Path.Combine(directory,(move?"move":"copy")+".txt");File.WriteAllText(source,"Owned native payload fixture");
            object? data=null,target=null;var watch=Stopwatch.StartNew();
            try
            {
                data=ShellDesktop.ContextObject(source,DataId,unowned:true);target=ShellDesktop.ContextObject(destination,DropId,unowned:true);SetEffect((IDataObject)data,"Preferred DropEffect",move?2u:1u);
                uint effect=ReadEffect((IDataObject)data);using(var lifetime=new ShellCommandLifetime())Drop((IDropTarget)target,(IDataObject)data,effect);
                bool pass=File.Exists(Path.Combine(destination,Path.GetFileName(source)))&&File.ReadAllText(Path.Combine(destination,Path.GetFileName(source)))=="Owned native payload fixture"&&File.Exists(source)!=move;
                checks.Add(new{name=move?"native move payload":"native copy payload",pass,milliseconds=watch.Elapsed.TotalMilliseconds});if(!pass)throw new InvalidDataException("Native data payload transfer failed.");
            }
            finally{if(target is not null)Marshal.ReleaseComObject(target);if(data is not null)Marshal.ReleaseComObject(data);}
        }
        string same=Path.Combine(directory,"same-folder.txt");File.WriteAllText(same,"Same folder owned fixture");int before=Directory.GetFiles(directory).Length;
        object? sameData=null,sameTarget=null;
        try
        {
            sameData=ShellDesktop.ContextObject(same,DataId,unowned:true);sameTarget=ShellDesktop.ContextObject(directory,DropId,unowned:true);SetEffect((IDataObject)sameData,"Preferred DropEffect",1);
            using(var lifetime=new ShellCommandLifetime())Drop((IDropTarget)sameTarget,(IDataObject)sameData,1);
            bool pass=File.ReadAllText(same)=="Same folder owned fixture"&&Directory.GetFiles(directory).Length==before+1;
            checks.Add(new{name="copy into original folder creates a separate sibling and preserves source",pass});if(!pass)throw new InvalidDataException("Same-folder native copy failed.");
        }
        finally{if(sameTarget is not null)Marshal.ReleaseComObject(sameTarget);if(sameData is not null)Marshal.ReleaseComObject(sameData);}
        return new{allPass=true,checks,systemClipboardWritten=false,scope="Real Shell drop on in-memory data objects for owned project files; does not touch the system clipboard"};
    }
    internal static object TestSelectionPayloads(string directory)
    {
        using var ole=new OleScope();var checks=new List<object>();
        foreach(bool sameParent in new[]{true,false})foreach(bool move in new[]{false,true})
        {
            string scope=Path.Combine(directory,(sameParent?"flat-":"cross-parent-")+(move?"move":"copy")),left=Path.Combine(scope,"left"),right=sameParent?left:Path.Combine(scope,"right"),destination=Path.Combine(scope,"destination");
            Directory.CreateDirectory(left);Directory.CreateDirectory(right);Directory.CreateDirectory(destination);
            string first=Path.Combine(left,"first.txt"),second=Path.Combine(right,"second.txt");File.WriteAllText(first,"Owned selection first");File.WriteAllText(second,"Owned selection second");
            object? data=null,target=null;
            try
            {
                data=ShellDesktop.ContextObjects([first,second],DataId,unowned:true);target=ShellDesktop.ContextObject(destination,DropId,unowned:true);
                var native=(IDataObject)data;SetEffect(native,"Preferred DropEffect",move?2u:1u);
                // Query the real CF_HDROP payload before any transfer. This
                // catches a menu/data aggregate silently retaining only item 1.
                var format=new FORMATETC{cfFormat=15,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
                native.GetData(ref format,out var medium);var payload=new List<string>();
                try
                {
                    uint count=DragQueryFile(medium.unionmember,uint.MaxValue,null,0);
                    for(uint index=0;index<count;index++){var value=new System.Text.StringBuilder(32768);if(DragQueryFile(medium.unionmember,index,value,(uint)value.Capacity)==0)throw new InvalidDataException();payload.Add(value.ToString());}
                }
                finally{ReleaseStgMedium(ref medium);}
                if(!payload.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[]{first,second}))throw new InvalidDataException("多选 Shell 数据对象未保留完整项目。");
                using(var lifetime=new ShellCommandLifetime())Drop((IDropTarget)target,native,move?2u:1u);
                bool pass=File.ReadAllText(Path.Combine(destination,"first.txt"))=="Owned selection first"&&File.ReadAllText(Path.Combine(destination,"second.txt"))=="Owned selection second"&&File.Exists(first)!=move&&File.Exists(second)!=move;
                checks.Add(new{name=(sameParent?"same-parent":"cross-parent")+" two-item native "+(move?"move":"copy"),pass,payloadCount=payload.Count});if(!pass)throw new InvalidDataException("多选 Shell 数据对象传输失败。");
            }
            finally{if(target is not null)Marshal.ReleaseComObject(target);if(data is not null)Marshal.ReleaseComObject(data);}
        }
        return new{allPass=true,checks,systemClipboardWritten=false};
    }
    private sealed class OleScope:IDisposable{public OleScope()=>Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));public void Dispose()=>OleUninitialize();}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [ComImport,Guid("00000122-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IDropTarget
    {
        [PreserveSig]int DragEnter([MarshalAs(UnmanagedType.Interface)]IDataObject data,uint keys,Point point,ref uint effect);
        [PreserveSig]int DragOver(uint keys,Point point,ref uint effect);[PreserveSig]int DragLeave();
        [PreserveSig]int Drop([MarshalAs(UnmanagedType.Interface)]IDataObject data,uint keys,Point point,ref uint effect);
    }
    [DllImport("ole32.dll")]private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)]out IDataObject data);
    [DllImport("ole32.dll")]private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)]IDataObject data);
    [DllImport("ole32.dll")]private static extern int OleFlushClipboard();
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern uint DragQueryFile(IntPtr drop,uint index,System.Text.StringBuilder? path,uint length);
}
