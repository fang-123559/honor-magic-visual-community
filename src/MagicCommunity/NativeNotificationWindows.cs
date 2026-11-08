using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static MagicCommunity.NativeSystemPanel;

namespace MagicCommunity;

// Bounded event history; notification clicks do not start an app when an
// existing identified window merely failed to activate.
internal sealed class NativeNotificationWindows
{
    private readonly Dictionary<string,long> recent = new(StringComparer.Ordinal);
    private long sequence;
    private readonly object gate = new();
    internal void Observe(JsonElement snapshot)
    {
        var windows = Array(snapshot,"windows").Take(512).ToArray();
        lock(gate)
        {
            var alive = windows.Select(w=>Text(w,"id")).ToHashSet(StringComparer.Ordinal);
            foreach(string id in recent.Keys.Where(id=>!alive.Contains(id)).ToArray())recent.Remove(id);
            foreach(var window in windows.Where(w=>Flag(w,"focused")))recent[Text(window,"id")]=++sequence;
        }
    }
    private static string PathKey(string path)=>path.Replace('/','\\').TrimEnd('\\').ToUpperInvariant();
    internal JsonElement Find(string appId,JsonElement snapshot,IEnumerable<string> executableTargets,Func<JsonElement,string>? windowAppId=null)
    {
        var targets=executableTargets.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(PathKey).ToHashSet(StringComparer.Ordinal);
        bool Matches(JsonElement window)
        {
            string path=Text(window,"executable"),name=System.IO.Path.GetFileName(path);
            return appId.Equals(Text(window,"aumid"),StringComparison.OrdinalIgnoreCase)||
                path.Length>0&&(targets.Contains(PathKey(path))||appId.Equals(name,StringComparison.OrdinalIgnoreCase)||appId.Equals(System.IO.Path.GetFileNameWithoutExtension(name),StringComparison.OrdinalIgnoreCase))||
                windowAppId is not null&&appId.Equals(windowAppId(window),StringComparison.OrdinalIgnoreCase);
        }
        Dictionary<string,long> order;lock(gate)order=new(recent,StringComparer.Ordinal);
        return Array(snapshot,"windows").Take(512).Where(Matches)
            .OrderByDescending(w=>Flag(w,"focused")).ThenByDescending(w=>order.GetValueOrDefault(Text(w,"id")))
            .ThenBy(w=>Flag(w,"minimized")).ThenBy(w=>Text(w,"id"),StringComparer.Ordinal).FirstOrDefault();
    }
    internal static string? QqTrayIcon(JsonElement tray,IEnumerable<string> executableTargets)
    {
        var paths=executableTargets.Where(p=>System.IO.Path.IsPathFullyQualified(p)&&
            System.IO.Path.GetFileName(p).Equals("QQ.exe",StringComparison.OrdinalIgnoreCase)).Select(PathKey).ToHashSet(StringComparer.Ordinal);
        var ids=Array(tray,"icons").Where(icon=>{
            string exe=Text(icon,"executable");
            return System.IO.Path.IsPathFullyQualified(exe)&&System.IO.Path.GetFileName(exe).Equals("QQ.exe",StringComparison.OrdinalIgnoreCase)&&
                (paths.Count==0||paths.Contains(PathKey(exe)));
        }).Select(icon=>Text(icon,"id")).Where(id=>id.Length>0).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return ids.Length==1?ids[0]:null;
    }
    internal async Task Open(string appId,JsonElement snapshot,IEnumerable<string> executableTargets,Func<JsonElement,Task> activate,Action<string> launch,Func<JsonElement,string>? windowAppId=null,Func<Task<bool>>? activateResidentQq=null)
    {
        if(appId.Length is <1 or >512||appId.Contains('"')||appId.Contains('\0'))throw new System.IO.InvalidDataException("Application identity differs.");
        var target=Find(appId,snapshot,executableTargets,windowAppId);
        if(target.ValueKind==JsonValueKind.Object){await activate(target);return;}
        if(appId.Equals("QQ",StringComparison.OrdinalIgnoreCase)&&activateResidentQq is not null&&await activateResidentQq())return;
        launch(appId);
    }
    // Unpackaged apps can assign an AUMID to a window rather than to the process.
    // Query only on a notification click, never on the move/idle event path.
    internal static string WindowAppId(JsonElement window)
    {
        if(!long.TryParse(Text(window,"hwnd"),NumberStyles.None,CultureInfo.InvariantCulture,out long value))return "";
        var hwnd=new IntPtr(value);GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid==0||pid!=(uint)Number(window,"pid"))return "";
        IPropertyStore? store=null;PropertyValue property=default;
        try
        {
            var iid=typeof(IPropertyStore).GUID;
            if(SHGetPropertyStoreForWindow(hwnd,ref iid,out store)<0||store is null)return "";
            var key=new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),5);
            if(store.GetValue(ref key,out property)<0||property.Type!=31)return "";
            string id=Marshal.PtrToStringUni(property.Pointer,Math.Min(513,Length(property.Pointer)))??"";
            return id.Length<=512?id:"";
        }
        catch(COMException){return "";}
        finally{PropVariantClear(ref property);if(store is not null&&Marshal.IsComObject(store))Marshal.ReleaseComObject(store);}
    }
    private static int Length(IntPtr value){if(value==IntPtr.Zero)return 0;int count=0;while(count<513&&Marshal.ReadInt16(value,count*2)!=0)count++;return count;}
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey(Guid format,uint id){public Guid Format=format;public uint Id=id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct PropertyValue{[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Pointer;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]int GetCount(out uint count);
        [PreserveSig]int GetAt(uint index,out PropertyKey key);
        [PreserveSig]int GetValue(ref PropertyKey key,out PropertyValue value);
        [PreserveSig]int SetValue(ref PropertyKey key,ref PropertyValue value);
        [PreserveSig]int Commit();
    }
    [DllImport("shell32.dll")]private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd,ref Guid iid,out IPropertyStore store);
    [DllImport("ole32.dll")]private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
}
