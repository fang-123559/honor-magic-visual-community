using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MagicCommunity;

// Real registered Explorer commands, retained in the same transient STA lease
// as IContextMenu. No command string execution, registry mutation or new host.
internal sealed class ShellExplorerCommands : IDisposable
{
    // Only optional cross-process metadata calls are cancellable. File command
    // execution is deliberately outside this scope. The outer owned-process
    // budget still covers non-cancellable in-process extensions.
    internal sealed class QueryCallBudget : IDisposable
    {
        private readonly object gate=new();private readonly uint thread=GetCurrentThreadId();private readonly Timer timer;private bool closed;
        internal QueryCallBudget(int milliseconds=600){Marshal.ThrowExceptionForHR(CoEnableCallCancellation(IntPtr.Zero));timer=new Timer(_=>{lock(gate)if(!closed)_=CoCancelCall(thread,0);},null,milliseconds,50);}
        public void Dispose(){lock(gate){closed=true;timer.Dispose();}CoDisableCallCancellation(IntPtr.Zero);}
    }
    internal static readonly ShellMenuFailureBackoff Failures=new();
    private sealed record Node(Command Value,int Parent,string Key);
    private readonly Dictionary<int,Node> nodes=[];
    private readonly Dictionary<int,DesktopContextMenu.Entry[]> children=[];
    private IntPtr selection;
    private int sequence=1000000;
    private sealed record Packaged(Guid Clsid,string[] Types);
    private static readonly Lazy<Packaged[]> packaged=new(ReadPackaged);
    private static Packaged[] ReadPackaged(){
        var result=new List<Packaged>();var packages=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine}){
            using var registrations=hive.OpenSubKey(@"Software\Classes\PackagedCom\Package");
            foreach(string name in registrations?.GetSubKeyNames().Take(128)??[]){
                if(!packages.Add(name))continue;uint length=0;if(GetPackagePathByFullName(name,ref length,null)!=122||length is 0 or >32768)continue;
                var buffer=new StringBuilder((int)length);if(GetPackagePathByFullName(name,ref length,buffer)!=0)continue;
                if(buffer.ToString().StartsWith(@"\\",StringComparison.Ordinal))continue;
                string path=Path.Combine(buffer.ToString(),"AppxManifest.xml");
                try{
                    using var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024});
                    var document=XDocument.Load(reader);
                    foreach(var extension in document.Descendants().Where(e=>e.Name.LocalName=="FileExplorerContextMenus").Take(16))
                    foreach(var verb in extension.Descendants().Where(e=>e.Name.LocalName=="Verb").Take(64)){
                        if(Guid.TryParse((string?)verb.Attribute("Clsid"),out var id)&&id!=Guid.Empty&&verb.Parent is {} item){
                            var type=(string?)item.Attribute("Type");if(!string.IsNullOrWhiteSpace(type))result.Add(new(id,[type]));
                        }
                    }
                }catch(Exception error)when(error is IOException or UnauthorizedAccessException or XmlException){DesktopContextMenu.Trace?.Invoke("Packaged menu metadata unavailable: "+error.Message);}
            }
        }
        return result.GroupBy(p=>p.Clsid).Select(group=>new Packaged(group.Key,group.SelectMany(p=>p.Types).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).Take(128).ToArray();
    }
    private static bool Matches(string path,string type)=>Directory.Exists(path)?type is "Directory" or "Folder"||(type=="Drive"&&StringComparer.OrdinalIgnoreCase.Equals(Path.GetPathRoot(path),path.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar)):type=="*"||StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path),type);

    // Optional registered components can be left behind after an app update.
    // Activation maps some HRESULTs to IO/Win32 exceptions, not COMException.
    internal static bool ProviderUnavailable(Exception error)=>error is COMException or InvalidCastException or TypeLoadException
        or NotImplementedException
        or IOException or UnauthorizedAccessException or System.Security.SecurityException
        or System.ComponentModel.Win32Exception or DllNotFoundException or BadImageFormatException;
    internal const int FirstIndex=1000000;
    public DesktopContextMenu.Entry[] Roots {get;private set;}=[];
    public void Load(string[] paths,IReadOnlyList<DesktopContextMenu.Entry> classic,bool includePackaged=true)
    {
        if(paths.Length==0||paths.Any(p=>p.StartsWith("::",StringComparison.Ordinal)))return;
        var pidls=new List<IntPtr>();
        try{
            foreach(string path in paths){Marshal.ThrowExceptionForHR(SHParseDisplayName(path,IntPtr.Zero,out var pidl,0,out _));pidls.Add(pidl);}
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Count,pidls.ToArray(),out selection));
            var roots=new List<DesktopContextMenu.Entry>();
            var stock=new List<(string Name,string Key)>{("Windows.CompressTo","compress"),("Windows.PinToHome","pintohome")};
            foreach(var pair in stock){
                if(classic.Any(r=>r.Key==pair.Key))continue;
                using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\"+pair.Name);
                if(!Guid.TryParse(key?.GetValue("ExplorerCommandHandler") as string??key?.GetValue("VerbHandler") as string,out var clsid))continue;
                Command? command=null;
                try{using var budget=new QueryCallBudget();DesktopContextMenu.Trace?.Invoke("stock-activate "+pair.Name);command=(Command)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!)!;DesktopContextMenu.Trace?.Invoke("stock-state "+pair.Name);var row=Add(command,0,pair.Key,CommandTitle(key),allowSlow:includePackaged);if(row is not null){roots.Add(row);command=null;}}
                catch(Exception error)when(ProviderUnavailable(error)){DesktopContextMenu.Trace?.Invoke("Explorer command unavailable: "+error.Message);}
                finally{if(command is not null)Marshal.ReleaseComObject(command);}
            }
            DesktopContextMenu.Trace?.Invoke("packaged-discovery");
            var providerClock=System.Diagnostics.Stopwatch.StartNew();
            foreach(var provider in includePackaged?packaged.Value.Where(p=>paths.All(path=>p.Types.Any(type=>Matches(path,type)))):Enumerable.Empty<Packaged>()){
                if(providerClock.ElapsedMilliseconds>=1200){DesktopContextMenu.Trace?.Invoke("packaged-query-budget-reached");break;}
                if(!Failures.Allows(provider.Clsid)){DesktopContextMenu.Trace?.Invoke("packaged-activation-backoff "+provider.Clsid);continue;}
                Command? command=null;bool activated=false;
                try{using var budget=new QueryCallBudget();DesktopContextMenu.Trace?.Invoke("packaged-activate "+provider.Clsid);command=(Command)Activator.CreateInstance(Type.GetTypeFromCLSID(provider.Clsid)!)!;activated=true;Failures.Succeeded(provider.Clsid);DesktopContextMenu.Trace?.Invoke("packaged-state "+provider.Clsid);var row=Add(command,0,"modern");if(row is not null){roots.Add(row);command=null;}}
                catch(Exception error)when(ProviderUnavailable(error)){
                    // A state/title error can depend on this selection. Only a
                    // failed activation is eligible for cross-selection backoff.
                    if(!activated)Failures.Failed(provider.Clsid,error.HResult);
                    DesktopContextMenu.Trace?.Invoke("Packaged Explorer command unavailable: "+error.Message);
                }
                finally{if(command is not null)Marshal.ReleaseComObject(command);}
            }
            Roots=roots.ToArray();
        }catch(Exception error)when(ProviderUnavailable(error)){DesktopContextMenu.Trace?.Invoke("Explorer selection unavailable: "+error.Message);}
        finally{foreach(var pidl in pidls)Marshal.FreeCoTaskMem(pidl);}
    }
    private static string? CommandTitle(RegistryKey? key){
        string? value=key?.GetValue("MUIVerb") as string??key?.GetValue(null) as string;if(string.IsNullOrWhiteSpace(value))return null;
        if(!value.StartsWith('@'))return value;var text=new StringBuilder(1024);return SHLoadIndirectString(value,text,(uint)text.Capacity,IntPtr.Zero)>=0?text.ToString():null;
    }
    private DesktopContextMenu.Entry? Add(Command command,int parent,string key,string? registeredTitle=null,bool allowSlow=true)
    {
        if(nodes.Count>=128)return null;
        int status=command.GetState(selection,false,out uint state);
        if(allowSlow&&status==unchecked((int)0x8000000A))status=command.GetState(selection,true,out state);
        if(status<0||(state&2)!=0)return null;
        int titleStatus=command.GetTitle(selection,out var title);string label;
        try{if(titleStatus<0&&!(titleStatus==unchecked((int)0x80004001)&&registeredTitle is not null))Marshal.ThrowExceptionForHR(titleStatus);label=titleStatus>=0?Marshal.PtrToStringUni(title)??"":registeredTitle!;}finally{if(title!=IntPtr.Zero)Marshal.FreeCoTaskMem(title);}
        if(string.IsNullOrWhiteSpace(label))return null;
        Marshal.ThrowExceptionForHR(command.GetFlags(out uint flags));
        string? icon=ShellMenuIcons.Stock(key);
        if(icon is null&&command.GetIcon(selection,out var location)>=0){try{icon=ShellMenuIcons.Extract(Marshal.PtrToStringUni(location)??"");}finally{if(location!=IntPtr.Zero)Marshal.FreeCoTaskMem(location);}}
        int id=sequence++;nodes.Add(id,new(command,parent,key));return new(id,label,(flags&1)!=0,false,(state&1)!=0,(state&8)!=0,Key:key,Icon:icon);
    }
    private Node Validate(int[] path)
    {
        int parent=0;Node? node=null;
        foreach(int id in path){if(!nodes.TryGetValue(id,out node)||node.Parent!=parent)throw new InvalidDataException("Explorer command path expired.");parent=id;}
        return node??throw new InvalidDataException();
    }
    public DesktopContextMenu.Entry[] Children(int[] path)
    {
        var node=Validate(path);int id=path[^1];if(children.TryGetValue(id,out var cached))return cached;
        using var budget=new QueryCallBudget();
        var rows=new List<DesktopContextMenu.Entry>();
        if(node.Value.EnumSubCommands(out var iterator)>=0&&iterator is not null){
            try{int visited=0;while(visited++<128&&rows.Count<64&&iterator.Next(1,out var command,out uint fetched)>=0&&fetched==1){
                try{var row=Add(command,id,node.Key);if(row is null)Marshal.ReleaseComObject(command);else rows.Add(row);}
                catch(Exception error)when(ProviderUnavailable(error)){Marshal.ReleaseComObject(command);DesktopContextMenu.Trace?.Invoke("Explorer child unavailable: "+error.Message);}
            }}
            finally{Marshal.ReleaseComObject(iterator);}
        }
        return children[id]=rows.ToArray();
    }
    public bool IsCompression(int[] path)=>Validate(path).Key=="compress";
    public void Invoke(int[] path)
    {
        var node=Validate(path);Marshal.ThrowExceptionForHR(node.Value.GetState(selection,true,out uint state));if((state&3)!=0)throw new InvalidOperationException("Windows 命令当前不可用。");
        Marshal.ThrowExceptionForHR(node.Value.Invoke(selection,IntPtr.Zero));
    }
    public void Dispose(){foreach(var node in nodes.Values)Marshal.ReleaseComObject(node.Value);nodes.Clear();children.Clear();Roots=[];if(selection!=IntPtr.Zero){Marshal.Release(selection);selection=IntPtr.Zero;}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetPackagePathByFullName(string name,ref uint length,StringBuilder? path);
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]private static extern int SHLoadIndirectString(string source,StringBuilder destination,uint size,IntPtr reserved);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("ole32.dll")]private static extern int CoEnableCallCancellation(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern int CoDisableCallCancellation(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern int CoCancelCall(uint thread,uint timeout);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint flags,out uint attributes);
    [DllImport("shell32.dll")]private static extern int SHCreateShellItemArrayFromIDLists(uint count,[In]IntPtr[] pidls,out IntPtr result);
    [ComImport,Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface Command{
        [PreserveSig]int GetTitle(IntPtr items,out IntPtr text);[PreserveSig]int GetIcon(IntPtr items,out IntPtr text);[PreserveSig]int GetToolTip(IntPtr items,out IntPtr text);
        [PreserveSig]int GetCanonicalName(out Guid name);[PreserveSig]int GetState(IntPtr items,[MarshalAs(UnmanagedType.Bool)]bool slow,out uint state);[PreserveSig]int Invoke(IntPtr items,IntPtr bind);
        [PreserveSig]int GetFlags(out uint flags);[PreserveSig]int EnumSubCommands(out EnumCommands enumerator);
    }
    [ComImport,Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface EnumCommands{
        [PreserveSig]int Next(uint count,[MarshalAs(UnmanagedType.Interface)]out Command command,out uint fetched);[PreserveSig]int Skip(uint count);[PreserveSig]int Reset();[PreserveSig]int Clone(out EnumCommands clone);
    }
}
