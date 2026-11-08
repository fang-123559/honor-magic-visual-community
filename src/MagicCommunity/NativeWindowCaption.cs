using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MagicCommunity;

internal static class NativeWindowCaption
{
    // Keep the native caption buttons, resize border and system menu.
    internal static void HideIcon(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
        SetWindowLongPtr(handle,-20,new IntPtr(GetWindowLongPtr(handle,-20).ToInt64()|1));
        SendMessage(handle,0x80,IntPtr.Zero,IntPtr.Zero);
        SendMessage(handle,0x80,new IntPtr(1),IntPtr.Zero);
        SetWindowPos(handle,IntPtr.Zero,0,0,0,0,0x37);
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr handle,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
}
