using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicCommunity;

public sealed partial class DesktopContextMenu
{
    // Windows keeps several ordinary commands in legacy handlers too. Query
    // these individually using the public default-folder menu restriction API,
    // retaining their initialized IContextMenu (state, IDs and invocation).
    // Never manufacture a verb or enable a command from a label alone.
    private sealed record Supplemental(IContextMenu Context,IntPtr Menu,int Position,Entry Row);
    private readonly Dictionary<int,Supplemental> supplemental=[];
    private readonly List<(IContextMenu Context,IntPtr Menu)> supplementalSources=[];
    private const int SupplementalFirst=500000; // Below modern IDs, above all classic menu positions.

    private Entry[] CompactWindowsCommands(string[] selection,Entry[] primary,bool icons)
    {
        var requests=new List<(Guid Class,string[] Verbs)>();
        using var copy=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\Windows.copyaspath");
        using var open=Registry.ClassesRoot.OpenSubKey(@"*\shellex\ContextMenuHandlers\Open With");
        if(!Guid.TryParse(copy?.GetValue("VerbHandler") as string,out var copyClass)||!Guid.TryParse(open?.GetValue(null) as string,out var openClass))throw new InvalidOperationException("Windows command registration unavailable.");
        if(!primary.Any(r=>r.Key=="copyaspath"))requests.Add((copyClass,["copyaspath"]));
        if(selection.Length==1&&!Directory.Exists(selection[0])&&!Path.GetExtension(selection[0]).Equals(".lnk",StringComparison.OrdinalIgnoreCase)&&!primary.Any(r=>r.Key=="openas"))requests.Add((openClass,["openas"]));
        if(selection.Any(p=>Path.GetExtension(p).Equals(".lnk",StringComparison.OrdinalIgnoreCase)))requests.Add((new("00021401-0000-0000-C000-000000000046"),["open","runas","opennewwindow"]));
        var added=new List<Entry>();
        foreach(var request in requests)
        {
            IContextMenu? source=null;IntPtr handle=IntPtr.Zero;
            try
            {
                source=(IContextMenu)ShellDesktop.ContextObjects(selection,typeof(IContextMenu).GUID);
                if(source is not IDefaultFolderMenuInitialize options||ReferenceEquals(source,context)||supplementalSources.Any(s=>ReferenceEquals(s.Context,source)))throw new InvalidOperationException("Windows isolated command menu unavailable.");
                Marshal.ThrowExceptionForHR(options.GetMenuRestrictions(0xffff,out uint current));
                var clsid=request.Class;Marshal.ThrowExceptionForHR(options.SetHandlerClsid(ref clsid));
                Marshal.ThrowExceptionForHR(options.SetMenuRestrictions(current|0x100));
                handle=CreatePopupMenu();if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                using(var budget=new ShellExplorerCommands.QueryCallBudget(600))Marshal.ThrowExceptionForHR(source.QueryContextMenu(handle,0,1,0x6fff,selection.Length==1?0x890u:0x810u));
                var rows=Read(handle,icons,false,source).Where(r=>r.Verb is not null&&request.Verbs.Contains(r.Verb,StringComparer.OrdinalIgnoreCase)&&!primary.Any(p=>p.Key==r.Key)&&!added.Any(p=>p.Key==r.Key)).ToArray();
                if(rows.Any(r=>r.Submenu))throw new InvalidOperationException("Windows command uses a cascade; retain its full menu.");
                if(rows.Length==0)continue;
                foreach(var row in rows){int id=SupplementalFirst+supplemental.Count;var visible=row with{Index=id};supplemental.Add(id,new(source,handle,row.Index,visible));added.Add(visible);}
                supplementalSources.Add((source,handle));source=null;handle=IntPtr.Zero;
            }
            finally{if(handle!=IntPtr.Zero)DestroyMenu(handle);if(source is not null&&Marshal.IsComObject(source))Marshal.ReleaseComObject(source);}
        }
        if(!primary.Concat(added).Any(r=>r.Key=="copyaspath"))throw new InvalidOperationException("Windows path-copy command unavailable.");
        if(selection.Any(p=>Path.GetExtension(p).Equals(".lnk",StringComparison.OrdinalIgnoreCase))&&!primary.Concat(added).Any(r=>r.Key=="open"))throw new InvalidOperationException("Windows shortcut default command unavailable.");
        return added.ToArray();
    }
    private void InvokeSupplement(int[] path)
    {
        if(path.Length!=1||!supplemental.TryGetValue(path[0],out var offered))throw new InvalidDataException("Menu command expired.");
        var row=Read(offered.Menu,false,false,offered.Context).Single(r=>r.Index==offered.Position);
        if(row.Disabled||row.Separator||row.Submenu||row.Verb!=offered.Row.Verb)throw new InvalidDataException("Menu command changed.");
        if(targets.Length==1&&ShellOpenWith.IsChooser(row)){ShellOpenWith.Show(targets);return;}
        uint id=GetMenuItemID(offered.Menu,offered.Position);if(id is 0 or >0x6fff)throw new InvalidDataException();
        var info=new InvokeInfo{Size=Marshal.SizeOf<InvokeInfo>(),Mask=0x100,Window=NativeDesktop.FindDesktopListView(),Verb=new IntPtr(id-1),Show=1};
        Marshal.ThrowExceptionForHR(offered.Context.InvokeCommand(ref info));
        if(row.Verb=="copyaspath")FlushClipboard();
    }
    private void ClearSupplements()
    {
        supplemental.Clear();
        foreach(var source in supplementalSources){DestroyMenu(source.Menu);if(Marshal.IsComObject(source.Context))Marshal.ReleaseComObject(source.Context);}
        supplementalSources.Clear();
    }
}
