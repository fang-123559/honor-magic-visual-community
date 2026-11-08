using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MagicCommunity;

public sealed record FileIdentity(string Volume,uint Serial,ulong Index);
public sealed class CollectedFile
{
    public string EntryId{get;set;}="";
    public string Original{get;set;}="";
    public string Stored{get;set;}="";
    public string Name{get;set;}="";
    public bool IsFolder{get;set;}
    public FileIdentity Identity{get;set;}=new("",0,0);
    public DesktopItem Item()=>new(){Name=Name,Target=Stored,IsFolder=IsFolder};
}
public sealed class CollectionOperation
{
    public int SchemaVersion{get;set;}=1;
    public string Id{get;set;}=Guid.NewGuid().ToString("N");
    public string Kind{get;set;}="collect";
    public string Status{get;set;}="prepared";
    public string? Error{get;set;}
    public List<CollectedFile> Files{get;set;}=[];
    public DateTimeOffset At{get;set;}=DateTimeOffset.Now;
}

// Only same-volume renames of direct desktop entries. Journal precedes physical changes;
// a durable layout is the commit point. No overwrite, recursive copy or delete.
// Public Desktop restoration may request Windows consent without elevating this process.
public sealed class ManagedDesktopFiles
{
    private readonly string directory;
    private readonly string[] roots;
    private readonly List<CollectionOperation> operations=[];
    public string? Problem{get;private set;}
    public bool IsBusy{get;private set;}
    public event Action? BecameIdle;
    public ManagedDesktopFiles(string dataDirectory,IEnumerable<string> desktopRoots)
    {
        directory=Path.Combine(dataDirectory,"collection-operations");
        roots=desktopRoots.Where(value=>!string.IsNullOrWhiteSpace(value)).Select(value=>Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static IEnumerable<string> DesktopRoots()=>new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
    private static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
    private string RootFor(string original)=>roots.SingleOrDefault(root=>StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(original)),root))??throw new LayoutEditException("移动收纳只接受 Windows 桌面的直接项目；其它位置请在整理入口添加引用。");
    private string Storage(string original)=>Path.Combine(Path.GetDirectoryName(RootFor(original))!,"Magic 视界收纳");
    private string Marker(string original)=>"MagicCommunityManagedStorage/v1\n"+RootFor(original).ToUpperInvariant();
    private void CheckStorage(string original,bool create)
    {
        string storage=Storage(original),marker=Path.Combine(storage,".magic-community-storage");
        for(string? path=Path.GetDirectoryName(storage);path is not null;path=Path.GetDirectoryName(path))if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new LayoutEditException("桌面收纳目录的上级包含链接或云占位目录，暂不执行移动。");
        if(!Directory.Exists(storage))
        {
            if(!create)throw new LayoutEditException("专用收纳目录已不存在，请保留操作记录并检查。");
            Directory.CreateDirectory(storage);using var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);var bytes=Encoding.UTF8.GetBytes(Marker(original));stream.Write(bytes);stream.Flush(true);
        }
        if((File.GetAttributes(storage)&FileAttributes.ReparsePoint)!=0||!File.Exists(marker)||(File.GetAttributes(marker)&FileAttributes.ReparsePoint)!=0||new FileInfo(marker).Length>4096||File.ReadAllText(marker)!=Marker(original))throw new LayoutEditException("同名收纳目录没有匹配的归属标记，拒绝移动。");
    }
    private void Validate(CollectionOperation operation)
    {
        if(operation.SchemaVersion!=1||!Guid.TryParseExact(operation.Id,"N",out _)||operation.Kind is not ("collect" or "restore")||operation.Status is not ("prepared" or "committed" or "rolled_back" or "needs_review")||operation.Files.Count is <1 or >100)throw new InvalidDataException("收纳操作记录无效。");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in operation.Files)
        {
            if(!Guid.TryParseExact(file.EntryId,"N",out _)||!seen.Add(file.Original)||!Path.IsPathFullyQualified(file.Original)||file.Original!=Path.GetFullPath(file.Original)||string.IsNullOrWhiteSpace(file.Name)||file.Name.Length>260||string.IsNullOrWhiteSpace(file.Identity.Volume))throw new InvalidDataException("收纳项目记录无效。");
            string expected=Path.Combine(Storage(file.Original),file.EntryId,Path.GetFileName(file.Original));
            if(!StringComparer.OrdinalIgnoreCase.Equals(expected,file.Stored))throw new InvalidDataException("收纳路径超出专用目录。");
        }
    }
    private void Save(CollectionOperation operation)=>StateStore.AtomicWrite(Path.Combine(directory,operation.Id+".json"),JsonSerializer.Serialize(operation,StateStore.Json),null);
    private static HashSet<string> Targets(LayoutState state)=>state.Boards.SelectMany(board=>board.RootItems.Concat(board.Boxes.SelectMany(box=>box.Items))).Select(item=>item.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool ForwardSaved(CollectionOperation operation,LayoutState state){var targets=Targets(state);return operation.Kind=="collect"?operation.Files.All(file=>targets.Contains(file.Stored)):operation.Files.All(file=>!targets.Contains(file.Stored));}
    private static (string Before,string After) Paths(CollectionOperation operation,CollectedFile file)=>operation.Kind=="collect"?(file.Original,file.Stored):(file.Stored,file.Original);
    private void Move(string from,string to,CollectedFile file)
    {
        CheckStorage(file.Original,false);
        if(Exists(to))throw new LayoutEditException("原位置或目标位置已有同名项目，未覆盖任何文件。");
        if(!Exists(from)||NativeIdentity(from)!=file.Identity)throw new LayoutEditException("文件身份已变化，移动已停止。");
        if((File.GetAttributes(from)&FileAttributes.ReparsePoint)!=0)throw new LayoutEditException("暂不移动链接目录或云占位文件。");
        string parent=Path.GetDirectoryName(to)!;
        if(!Directory.Exists(parent)||(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0||NativeIdentity(parent).Volume!=file.Identity.Volume)throw new LayoutEditException("目标不是同一磁盘的普通目录，移动已停止。");
        try{if(file.IsFolder)Directory.Move(from,to);else File.Move(from,to,false);}
        catch(UnauthorizedAccessException)when(StringComparer.OrdinalIgnoreCase.Equals(to,file.Original)&&StringComparer.OrdinalIgnoreCase.Equals(RootFor(file.Original),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)))
        {
            // Public Desktop can permit deleting existing files but deny creating them.
            // Ask the Windows Shell for this exact restoration, preserving normal UAC.
            ShellFileMove.Move(from,to);
        }
        if(NativeIdentity(to)!=file.Identity)throw new IOException("移动后的文件身份核对失败。");
        ShellChangeNotifications.Moved(from,to,file.IsFolder);
    }
    private bool Undo(CollectionOperation operation)
    {
        bool success=true;
        foreach(var file in operation.Files.AsEnumerable().Reverse())
        {
            var pair=Paths(operation,file);
            try
            {
                if(Exists(pair.Before)&&NativeIdentity(pair.Before)==file.Identity&&!Exists(pair.After))continue;
                if(Exists(pair.After)&&NativeIdentity(pair.After)==file.Identity&&!Exists(pair.Before))Move(pair.After,pair.Before,file);
                else success=false;
            }
            catch{success=false;}
        }
        operation.Status=success?"rolled_back":"needs_review";try{Save(operation);}catch{success=false;}
        if(!success)Problem="有未完成的移动需要核对；文件和操作记录均已保留，新的移动暂时停用。";return success;
    }
    public void Recover(LayoutState state,bool readOnly=false)
    {
        try
        {
            if(!Directory.Exists(directory))return;
            var files=Directory.GetFiles(directory,"*.json");if(files.Length>10000)throw new InvalidDataException("收纳记录数量超限。");
            foreach(string path in files)
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||new FileInfo(path).Length>262144)throw new InvalidDataException("收纳记录文件无效。");
                var operation=JsonSerializer.Deserialize<CollectionOperation>(File.ReadAllBytes(path),StateStore.Json)??throw new InvalidDataException();Validate(operation);
                if(Path.GetFileNameWithoutExtension(path)!=operation.Id)throw new InvalidDataException();operations.Add(operation);
            }
            foreach(var operation in operations.Where(value=>value.Status is "prepared" or "needs_review"))
            {
                if(readOnly)throw new InvalidOperationException("布局只读，暂不自动处理未完成的文件移动。");
                if(operation.Status=="needs_review")throw new InvalidOperationException("有移动需要人工核对。");
                if(ForwardSaved(operation,state))
                {
                    if(operation.Files.Any(file=>!Exists(Paths(operation,file).After)||NativeIdentity(Paths(operation,file).After)!=file.Identity))throw new InvalidDataException("已保存布局与文件状态不一致。");
                    operation.Status="committed";Save(operation);
                }
                else if(!Undo(operation))break;
            }
        }
        catch(Exception ex){Problem="收纳恢复需要核对，移动暂时停用："+ex.Message;}
    }
    private void Execute(CollectionOperation operation,Action<IReadOnlyList<CollectedFile>> commit,Func<LayoutState> state)
    {
        if(IsBusy)throw new LayoutEditException("另一次文件移动正在处理，请等待完成。");
        if(Problem is not null)throw new LayoutEditException(Problem);Validate(operation);Directory.CreateDirectory(directory);Save(operation);operations.Add(operation);IsBusy=true;
        try
        {
            foreach(var file in operation.Files){var pair=Paths(operation,file);Move(pair.Before,pair.After,file);}
            commit(operation.Files);operation.Status="committed";Save(operation);
        }
        catch(Exception ex)
        {
            operation.Error=ex.ToString();if(operation.Error.Length>2048)operation.Error=operation.Error[..2048];
            if(ForwardSaved(operation,state()))
            {
                operation.Status="committed";try{Save(operation);}catch{Problem="布局及文件已保存，但操作完成标记写入失败；重新进入后会核对。";}return;
            }
            bool reverted=Undo(operation);throw new LayoutEditException(reverted?"移动未完成，已恢复原位置，布局未保存："+ex.Message:Problem!);
        }
        finally{IsBusy=false;BecameIdle?.Invoke();}
    }
    public void Collect(IReadOnlyList<DesktopItem> items,Action<IReadOnlyList<CollectedFile>> commit,Func<LayoutState> state)
    {
        if(IsBusy)throw new LayoutEditException("另一次文件移动正在处理，请等待完成。");if(Problem is not null)throw new LayoutEditException(Problem);
        var distinct=items.DistinctBy(item=>item.Target,StringComparer.OrdinalIgnoreCase).ToArray();if(distinct.Length is <1 or >100)throw new LayoutEditException("一次请移动 1–100 个桌面项目。");
        foreach(var item in distinct)
        {
            if(item.IsVirtual||!Exists(item.Target)||(File.GetAttributes(item.Target)&FileAttributes.ReparsePoint)!=0)throw new LayoutEditException("系统虚拟图标、链接目录或云占位文件暂不支持移动，请保留引用。");
            RootFor(item.Target);
        }
        var operation=new CollectionOperation();
        foreach(var item in distinct)
        {
            CheckStorage(item.Target,true);string entry=Guid.NewGuid().ToString("N"),container=Path.Combine(Storage(item.Target),entry);Directory.CreateDirectory(container);
            operation.Files.Add(new(){EntryId=entry,Original=Path.GetFullPath(item.Target),Stored=Path.Combine(container,Path.GetFileName(item.Target)),Name=item.Name,IsFolder=Directory.Exists(item.Target),Identity=NativeIdentity(item.Target)});
        }
        Execute(operation,commit,state);
    }
    public CollectedFile? Find(string target)=>operations.Where(operation=>operation.Kind=="collect"&&operation.Status!="rolled_back").SelectMany(operation=>operation.Files).LastOrDefault(file=>StringComparer.OrdinalIgnoreCase.Equals(file.Stored,target));
    public bool IsManaged(string target)=>Find(target)is not null;
    public bool HasStoredFiles=>operations.Where(operation=>operation.Kind=="collect"&&operation.Status!="rolled_back").SelectMany(operation=>operation.Files).Any(file=>Exists(file.Stored));
    public void Restore(IReadOnlyList<DesktopItem> items,Action<IReadOnlyList<CollectedFile>> commit,Func<LayoutState> state)
    {
        if(IsBusy)throw new LayoutEditException("另一次文件移动正在处理，请等待完成。");
        var files=items.Select(item=>Find(item.Target)).OfType<CollectedFile>().DistinctBy(file=>file.Stored,StringComparer.OrdinalIgnoreCase).ToList();
        if(files.Count==0){commit([]);return;}
        if(files.Count>100)throw new LayoutEditException("一次最多恢复 100 个移动项目，请先分批移出后再解散文件夹。");
        if(files.Any(file=>Exists(file.Original)))throw new LayoutEditException("原桌面位置已有同名项目；未覆盖，收纳内容保留。请先处理同名项目后再移出。");
        Execute(new(){Kind="restore",Files=files},commit,state);
    }
    public void ProtectReferences(LayoutState before,LayoutState after)
    {
        var targets=Targets(after);
        foreach(string target in Targets(before))if(!targets.Contains(target)&&IsManaged(target)&&Exists(target))throw new LayoutEditException("此项目已移动收纳，请先在文件夹内选择“移出并恢复原位置”，再移除引用或收纳盒。");
    }
    public static FileIdentity NativeIdentity(string path)
    {
        using var handle=CreateFile(path,0x80,7,IntPtr.Zero,3,0x02000000|0x00200000,IntPtr.Zero);
        if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
        var name=new StringBuilder(32768);uint count=GetFinalPathNameByHandle(handle,name,(uint)name.Capacity,1);
        int end=name.ToString().IndexOf('}');if(count==0||count>=name.Capacity||end<0||!name.ToString().StartsWith("\\\\?\\Volume{",StringComparison.OrdinalIgnoreCase))throw new LayoutEditException("无法核对本地磁盘身份，移动已停止。");
        return new(name.ToString()[..(end+1)].ToUpperInvariant(),info.VolumeSerialNumber,((ulong)info.FileIndexHigh<<32)|info.FileIndexLow);
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInfoNative{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint VolumeSerialNumber,SizeHigh,SizeLow,Links,FileIndexHigh,FileIndexLow;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInfoNative info);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder name,uint size,uint flags);
}
