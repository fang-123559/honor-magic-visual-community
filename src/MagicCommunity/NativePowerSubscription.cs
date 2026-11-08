using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MagicCommunity;
// One subscription per on-demand WPF window, disposed with that window.
// Windows owns the rendering policy; no application-level opt-out is offered.
internal sealed class NativePowerSubscription : IDisposable
{
    internal static readonly Guid Legacy=new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5"),Energy=new("550E8400-E29B-41D4-A716-446655440000");
    private readonly Window window;private readonly Action changed;private readonly HwndSource? source;
    private readonly List<IntPtr> tokens=[];private bool disposed;
    internal NativePowerSubscription(Window window,Action changed){
        this.window=window;this.changed=changed;var handle=new WindowInteropHelper(window).Handle;source=HwndSource.FromHwnd(handle);source?.AddHook(Message);
        foreach(var value in new[]{Legacy,Energy}){var id=value;var token=RegisterPowerSettingNotification(handle,ref id,0);if(token!=IntPtr.Zero)tokens.Add(token);}
        window.Closed+=Closed;
    }
    private IntPtr Message(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){
        if(message==0x218&&w.ToInt64()==0x8013&&l!=IntPtr.Zero){var id=Marshal.PtrToStructure<Guid>(l);int bytes=Marshal.ReadInt32(l,16);if(bytes==4&&CommunityPreferences.AcceptPowerSetting(id,Marshal.ReadInt32(l,20)))changed();}
        return IntPtr.Zero;
    }
    private void Closed(object? sender,EventArgs e)=>Dispose();
    public void Dispose(){if(disposed)return;disposed=true;window.Closed-=Closed;source?.RemoveHook(Message);foreach(var token in tokens)UnregisterPowerSettingNotification(token);tokens.Clear();}
    [DllImport("user32.dll")]private static extern IntPtr RegisterPowerSettingNotification(IntPtr window,ref Guid setting,uint flags);
    [DllImport("user32.dll")]private static extern bool UnregisterPowerSettingNotification(IntPtr token);
}
