using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace MagicCommunity;

// Uses the existing adapter window, or one hidden broadcast HWND while settings
// remain lazy. No extra thread or idle polling. Construct/dispose on its Dispatcher. The owner handles comparison,
// error reporting and any geometry changes in onSignal.
public sealed class DesktopWorkAreaWatch : IDisposable
{
    private readonly HwndSource source;
    private readonly bool ownsSource;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer debounce;
    private readonly Action onSignal;
    private readonly Action<int,IntPtr,IntPtr>? onBroadcast;
    private readonly IntPtr hwnd;
    private bool disposed;
    private bool sessionRegistered;
    public bool SessionNotificationsRegistered => sessionRegistered;
    public int SessionNotificationError { get; }
    public int SessionUnregisterError { get; private set; }

    public DesktopWorkAreaWatch(Action onSignal,Action<int,IntPtr,IntPtr>? onBroadcast=null)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        var window = Application.Current?.MainWindow;
        dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.VerifyAccess();
        if (window is null)
        {
            // HWND_MESSAGE does not receive the work-area/display broadcasts.
            // A hidden top-level source has no visual tree or rendering surface.
            source = new HwndSource(new HwndSourceParameters("MagicDesktopWorkAreaObserver")
            {
                Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            });
            ownsSource = true;
            hwnd = source.Handle;
        }
        else
        {
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            source = HwndSource.FromHwnd(hwnd)
                ?? throw new InvalidOperationException("The adapter's native window source is unavailable.");
        }
        this.onSignal = onSignal;this.onBroadcast=onBroadcast;
        debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        debounce.Tick += Tick;
        source.AddHook(WindowMessage);
        source.Disposed += SourceDisposed;
        // WTS may be unavailable briefly at logon. Preserve setting/display
        // observation and expose the error instead of adding a retry poll.
        SessionNotificationError = RegisterSessionNotifications(hwnd);
        sessionRegistered = SessionNotificationError == 0;
    }

    internal static bool IsSignal(int message, IntPtr parameter) =>
        // SPI_SETWORKAREA and broadcasts with no detail share SETTINGCHANGE.
        message is 0x001A or 0x007E
        || (message == 0x0218 && parameter.ToInt64() is 0x0006 or 0x0007 or 0x0012)
        || (message == 0x02B1 && parameter.ToInt64() is 0x0001 or 0x0003 or 0x0005 or 0x0008);

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        if (!disposed && IsSignal(message, parameter))
        {
            // Invalidate pending wallpaper writes before the geometry debounce.
            onBroadcast?.Invoke(message,parameter,data);
            // A trailing event debounce, never a repeating idle timer.
            debounce.Stop();
            debounce.Start();
        }
        return IntPtr.Zero; // Leave the original window's message handling intact.
    }

    private void Tick(object? sender, EventArgs args)
    {
        debounce.Stop();
        if (!disposed) onSignal();
    }

    private void SourceDisposed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        debounce.Stop();
        debounce.Tick -= Tick;
        if (sessionRegistered)
        {
            sessionRegistered = false;
            SessionUnregisterError = UnregisterSessionNotifications(hwnd);
        }
        source.Disposed -= SourceDisposed;
        if (!source.IsDisposed) source.RemoveHook(WindowMessage);
        if (ownsSource && !source.IsDisposed) source.Dispose();
    }

    internal static int RegisterSessionNotifications(IntPtr window) =>
        WTSRegisterSessionNotification(window, 0) ? 0 : Marshal.GetLastWin32Error(); // NOTIFY_FOR_THIS_SESSION
    private static int UnregisterSessionNotifications(IntPtr window) =>
        WTSUnRegisterSessionNotification(window) ? 0 : Marshal.GetLastWin32Error();
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
