using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MagicCommunity;

// Same on-demand DWM Acrylic path and downgrade policy as settings/weather.
internal static class NativeWindowMaterial
{
    internal static bool Apply(Window window, Border surface, NativeWpfTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        bool glass = false;
        if (handle != IntPtr.Zero)
        {
            bool enabled = !SystemParameters.HighContrast && !SystemParameters.IsRemoteSession &&
                !CommunityPreferences.Power().Saver && NativeWpfTheme.Personalization("EnableTransparency", 0) != 0;
            int dark = theme.Dark ? 1 : 0, corner = 2, backdrop = enabled ? 3 : 1;
            DwmSetWindowAttribute(handle, 20, ref dark, 4);
            DwmSetWindowAttribute(handle, 33, ref corner, 4);
            int applied = DwmSetWindowAttribute(handle, 38, ref backdrop, 4);
            var margin = new Margins { Left = enabled ? -1 : 0, Right = enabled ? -1 : 0, Top = enabled ? -1 : 0, Bottom = enabled ? -1 : 0 };
            int frame = DwmExtendFrameIntoClientArea(handle, ref margin);
            glass = enabled && applied == 0 && frame == 0;
            if (enabled && !glass) { backdrop = 1; DwmSetWindowAttribute(handle, 38, ref backdrop, 4); margin = new(); DwmExtendFrameIntoClientArea(handle, ref margin); }
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is {} target) target.BackgroundColor = Colors.Transparent;
        }
        window.Background = Brushes.Transparent;
        surface.Background = theme.MaterialBackground(glass);
        return glass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int bytes);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins value);
}
