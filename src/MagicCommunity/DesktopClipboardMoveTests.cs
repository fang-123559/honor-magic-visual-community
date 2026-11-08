using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MagicCommunity;

// Explicit diagnostics only. The production paste core receives the same real
// Shell IDataObject here, wrapped only to record its completion notifications.
// No clipboard is opened, replaced, flushed, or cleared by this fixture.
public static class DesktopClipboardMoveTests
{
    private static readonly Guid DataId=new("0000010E-0000-0000-C000-000000000046");

    public static object Run(string root)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Clipboard move fixtures require an STA thread.");
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            string fixture=Path.Combine(Path.GetFullPath(root),"evidence","desktop-feedback",DateTime.Now.ToString("yyyy-MM-dd"),"clipboard-move-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var checks=new List<object>();var timings=new List<object>();
            void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
            string Write(string directory,string name,string content){Directory.CreateDirectory(directory);string path=Path.Combine(directory,name);File.WriteAllText(path,content);return path;}

            foreach(string kind in new[]{"two-files","nested-folders","mixed-cross-parent"})
            {
                string scope=Path.Combine(fixture,kind),left=Path.Combine(scope,"left"),right=kind=="mixed-cross-parent"?Path.Combine(scope,"right"):left,destination=Path.Combine(scope,"destination");
                Directory.CreateDirectory(left);Directory.CreateDirectory(right);Directory.CreateDirectory(destination);
                string first,second;
                if(kind=="nested-folders")
                {
                    first=Path.Combine(left,"first-folder");second=Path.Combine(left,"second-folder");
                    foreach(string folder in new[]{first,second})
                    {
                        Write(folder,"root.txt","Owned clipboard directory root "+Path.GetFileName(folder));
                        Write(Path.Combine(folder,"child","grandchild"),"leaf.txt","Owned clipboard nested leaf "+Path.GetFileName(folder));
                    }
                }
                else
                {
                    first=Write(left,"first.txt","Owned clipboard first "+kind);
                    if(kind=="mixed-cross-parent")
                    {
                        second=Path.Combine(right,"second-folder");
                        Write(Path.Combine(second,"nested"),"second.txt","Owned clipboard mixed nested folder");
                    }
                    else second=Write(left,"second.txt","Owned clipboard second "+kind);
                }
                string keepLeft=Write(left,"unselected-left.txt","Unselected left "+kind);
                string keepRight=left==right?keepLeft:Write(right,"unselected-right.txt","Unselected right "+kind);
                var before=new[]{first,second}.ToDictionary(path=>Path.GetFileName(path),Snapshot,StringComparer.OrdinalIgnoreCase);
                object? native=null;
                try
                {
                    native=ShellDesktop.ContextObjects([first,second],DataId,unowned:true);
                    var recorded=new RecordingDataObject((IDataObject)native);
                    SetValue(recorded,"Preferred DropEffect",2);
                    recorded.Writes.Clear();
                    var watch=Stopwatch.StartNew();
                    ShellClipboardCommands.MoveClipboardData(recorded,destination);
                    timings.Add(new{name=kind,milliseconds=watch.Elapsed.TotalMilliseconds,feedback=recorded.Writes.ToArray()});
                    Check(kind+": complete selected sources disappear",!Exists(first)&&!Exists(second));
                    Check(kind+": all destination contents are preserved",before.All(entry=>Snapshot(Path.Combine(destination,entry.Key)).SequenceEqual(entry.Value)));
                    Check(kind+": unselected files remain unchanged",File.ReadAllText(keepLeft)=="Unselected left "+kind&&File.ReadAllText(keepRight)==(left==right?"Unselected left ":"Unselected right ")+kind);
                    Check(kind+": completion ends with optimized-move feedback before paste success",HasCompletion(recorded.Writes));
                    Check(kind+": no source-side duplicate deletion is requested",recorded.Writes.Where(value=>value.Format=="Performed DropEffect").All(value=>value.Value==0));
                }
                finally{if(native is not null)Marshal.ReleaseComObject(native);}
            }

            {
                string scope=Path.Combine(fixture,"invalid-destination"),source=Write(scope,"first.txt","Invalid destination first"),second=Write(scope,"second.txt","Invalid destination second"),destination=Path.Combine(scope,"not-created");
                object? native=null;
                try
                {
                    native=ShellDesktop.ContextObjects([source,second],DataId,unowned:true);
                    var recorded=new RecordingDataObject((IDataObject)native);SetValue(recorded,"Preferred DropEffect",2);recorded.Writes.Clear();
                    Exception? failure=null;try{ShellClipboardCommands.MoveClipboardData(recorded,destination);}catch(Exception error){failure=error;}
                    Check("invalid destination: reports failure",failure is not null);
                    Check("invalid destination: both sources and contents remain",File.ReadAllText(source)=="Invalid destination first"&&File.ReadAllText(second)=="Invalid destination second"&&!Directory.Exists(destination));
                    Check("invalid destination: sends no paste-success or fabricated completion feedback",recorded.Writes.Count==0);
                }
                finally{if(native is not null)Marshal.ReleaseComObject(native);}
            }
            return new{allPass=true,checks,timings,fixture,systemClipboardRead=false,systemClipboardWritten=false,scope="Same-process real Shell IDataObject batch moves through the shared production paste core; owned project fixtures only",manualRequired=new[]{"Explorer cross-process cut/paste with its live clipboard data object","Windows collision, cancellation and partial-completion UI","Cloud/virtual/provider-backed clipboard items"}};
        }
        finally{OleUninitialize();}
    }

    private static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
    private static string[] Snapshot(string path)
    {
        if(File.Exists(path))return["file:"+File.ReadAllText(path)];
        if(!Directory.Exists(path))throw new InvalidDataException("An owned fixture path is missing: "+path);
        return Directory.GetDirectories(path,"*",SearchOption.AllDirectories).Select(directory=>"directory:"+Path.GetRelativePath(path,directory)).Concat(Directory.GetFiles(path,"*",SearchOption.AllDirectories).Select(file=>"file:"+Path.GetRelativePath(path,file)+":"+File.ReadAllText(file))).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
    }
    private static bool HasCompletion(List<Feedback> writes)
        =>writes.Count>=3&&writes[^3]==new Feedback("Performed DropEffect",0)&&writes[^2]==new Feedback("Logical Performed DropEffect",2)&&writes[^1]==new Feedback("Paste Succeeded",2);

    public sealed record Feedback(string Format,uint Value);

    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class RecordingDataObject:IDataObject
    {
        private readonly IDataObject inner;
        private readonly Dictionary<short,string> formats=new(){[unchecked((short)RegisterClipboardFormat("Performed DropEffect"))]="Performed DropEffect",[unchecked((short)RegisterClipboardFormat("Logical Performed DropEffect"))]="Logical Performed DropEffect",[unchecked((short)RegisterClipboardFormat("Paste Succeeded"))]="Paste Succeeded"};
        public List<Feedback> Writes{get;}=[];
        public RecordingDataObject(IDataObject inner)=>this.inner=inner;
        public void GetData(ref FORMATETC format,out STGMEDIUM medium)=>inner.GetData(ref format,out medium);
        public void GetDataHere(ref FORMATETC format,ref STGMEDIUM medium)=>inner.GetDataHere(ref format,ref medium);
        public int QueryGetData(ref FORMATETC format)=>inner.QueryGetData(ref format);
        public int GetCanonicalFormatEtc(ref FORMATETC format,out FORMATETC canonical)=>inner.GetCanonicalFormatEtc(ref format,out canonical);
        public void SetData(ref FORMATETC format,ref STGMEDIUM medium,bool release)
        {
            Feedback? entry=null;
            if(formats.TryGetValue(format.cfFormat,out string? name)&&medium.tymed==TYMED.TYMED_HGLOBAL&&GlobalSize(medium.unionmember).ToUInt64()>=4)
            {
                IntPtr bits=GlobalLock(medium.unionmember);if(bits==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                try{entry=new(name,unchecked((uint)Marshal.ReadInt32(bits)));}finally{GlobalUnlock(medium.unionmember);}
            }
            inner.SetData(ref format,ref medium,release);
            if(entry is not null)Writes.Add(entry);
        }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)=>inner.EnumFormatEtc(direction);
        public int DAdvise(ref FORMATETC format,ADVF flags,IAdviseSink sink,out int connection)=>inner.DAdvise(ref format,flags,sink,out connection);
        public void DUnadvise(int connection)=>inner.DUnadvise(connection);
        public int EnumDAdvise(out IEnumSTATDATA? enumerator)=>inner.EnumDAdvise(out enumerator);
    }

    private static void SetValue(IDataObject data,string name,uint value)
    {
        var format=new FORMATETC{cfFormat=unchecked((short)RegisterClipboardFormat(name)),dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
        IntPtr memory=GlobalAlloc(0x42,new UIntPtr(4));if(memory==IntPtr.Zero)throw new OutOfMemoryException();
        var medium=new STGMEDIUM{tymed=TYMED.TYMED_HGLOBAL,unionmember=memory};bool transferred=false;
        try
        {
            IntPtr bits=GlobalLock(memory);if(bits==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            try{Marshal.WriteInt32(bits,unchecked((int)value));}finally{GlobalUnlock(memory);}
            data.SetData(ref format,ref medium,true);transferred=true;
        }
        finally{if(!transferred)ReleaseStgMedium(ref medium);}
    }
    [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern UIntPtr GlobalSize(IntPtr memory);
}
