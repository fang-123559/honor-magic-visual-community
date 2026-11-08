using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MagicCommunity;

// Shell events coalesce into one background query; no idle polling.
internal sealed class RecycleBinWatch : IDisposable
{
    private const int Changed = 0x8000 + 181;
    private readonly HwndSource source;
    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Action notify;
    private IntPtr pidl;
    private uint registration;
    private bool disposed, reading, again;
    private bool? full;
    internal static bool? CurrentFull{get;private set;}

    internal RecycleBinWatch(Action notify)
    {
        this.notify = notify;
        source = new(new HwndSourceParameters("Magic recycle change listener") { ParentWindow = new(-3) });
        source.AddHook(Hook);
        if (SHParseDisplayName(ShellClipboardCommands.RecycleBinTarget, IntPtr.Zero, out pidl, 0, out _) >= 0)
        {
            var entry = new Entry { Pidl = pidl, Recursive = true };
            registration = SHChangeNotifyRegister(source.Handle, 0x8003, 0x7fffffff, Changed, 1, ref entry);
        }
        if (registration == 0) Debug.WriteLine("Recycle Bin Shell notification registration unavailable.");
        debounce.Tick += (_, _) => { debounce.Stop(); Query(); };
        Query();
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != Changed || disposed) return IntPtr.Zero;
        IntPtr locked = SHChangeNotification_Lock(w, unchecked((uint)l.ToInt64()), out _, out _);
        if (locked != IntPtr.Zero) SHChangeNotification_Unlock(locked);
        debounce.Stop(); debounce.Start();
        handled = true;
        return IntPtr.Zero;
    }
    private async void Query()
    {
        if (disposed) return;
        if (reading) { again = true; return; }
        reading = true;
        try
        {
            bool? next = await Task.Run(QueryFull);
            if (!disposed && next.HasValue && next != full) { full = next; CurrentFull=next; notify(); }
        }
        catch (Exception error) { Debug.WriteLine(error); }
        finally { reading = false; if (again && !disposed && !ui.HasShutdownStarted) { again = false; Query(); } }
    }
    internal static bool? QueryFull()
    {
        var info = new Info { Size = (uint)Marshal.SizeOf<Info>() };
        return SHQueryRecycleBin(null, ref info) == 0 && info.Items >= 0 ? info.Items > 0 : null;
    }
    internal static void EmptyWithConfirmation()
    {
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0);
        if (hr != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(hr);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; debounce.Stop();
        if (registration != 0) SHChangeNotifyDeregister(registration);
        source.Dispose(); if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Entry { public IntPtr Pidl; [MarshalAs(UnmanagedType.Bool)] public bool Recursive; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(IntPtr window, int sources, int events, int message, int count, ref Entry entry);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("shell32.dll")] private static extern IntPtr SHChangeNotification_Lock(IntPtr change, uint process, out IntPtr pidls, out int events);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotification_Unlock(IntPtr change);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref Info info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr owner, string? root, uint flags);
}
