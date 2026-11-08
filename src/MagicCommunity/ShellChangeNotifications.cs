using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace MagicCommunity;

// Explorer batches FLUSHNOWAIT updates for roughly half a second on this machine.
// Wait for delivery on one background thread; never block the desktop/UI transaction.
public static class ShellChangeNotifications
{
    private const int RenameItem=1,CreateItem=2,DeleteItem=4,MakeDirectory=8,RemoveDirectory=0x10,UpdateDirectory=0x1000,RenameDirectory=0x20000;
    private const uint PathWide=5,Flush=0x1000,FlushNoWait=0x2000;
    private sealed record MoveChange(string From,string To,bool IsFolder);
    private static readonly BlockingCollection<MoveChange> pending=new(256);
    static ShellChangeNotifications()
    {
        var thread=new Thread(()=>
        {
            foreach(var change in pending.GetConsumingEnumerable())
                try{Send(change,PathWide|Flush);}catch(Exception ex)when(ex is not OutOfMemoryException){/* File-system state is authoritative; Explorer can still observe its normal directory change event. */}
        }){IsBackground=true,Name="Magic Shell notifications"};
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public static void Moved(string from,string to,bool isFolder)
    {
        var change=new MoveChange(from,to,isFolder);
        // A slow external Shell listener must not accumulate unbounded work or stall input.
        if(!pending.TryAdd(change))Send(change,PathWide|FlushNoWait);
    }
    private static void Send(MoveChange change,uint flags)
    {
        if(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(change.From),Path.GetDirectoryName(change.To)))
        {
            if(change.From.Length<260&&change.To.Length<260)SHChangeNotify(change.IsFolder?RenameDirectory:RenameItem,flags,change.From,change.To);
        }
        else
        {
            // A move across folders changes membership of each view. Send precise
            // removal/creation events, so the merged user/Public Desktop need not re-enumerate first.
            if(change.From.Length<260)SHChangeNotify(change.IsFolder?RemoveDirectory:DeleteItem,flags,change.From,null);
            if(change.To.Length<260)SHChangeNotify(change.IsFolder?MakeDirectory:CreateItem,flags,change.To,null);
        }
        foreach(string parent in new[]{Path.GetDirectoryName(change.From)!,Path.GetDirectoryName(change.To)!}.Distinct(StringComparer.OrdinalIgnoreCase))
            if(parent.Length<260)SHChangeNotify(UpdateDirectory,flags,parent,null);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern void SHChangeNotify(int change,uint flags,string? first,string? second);
}
