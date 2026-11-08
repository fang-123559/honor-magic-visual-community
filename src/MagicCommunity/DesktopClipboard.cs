using System.Runtime.InteropServices;

namespace MagicCommunity;

// A format-only check on menu opening. Never opens, reads or changes clipboard
// contents; no listener, timer or worker is kept while the menu is closed.
public static class DesktopClipboard
{
    private static readonly uint ShellIdList=RegisterClipboardFormat("Shell IDList Array"),Descriptors=RegisterClipboardFormat("FileGroupDescriptorW");
    public static object Inspect()=>new{canPaste=IsClipboardFormatAvailable(15)||IsClipboardFormatAvailable(ShellIdList)||IsClipboardFormatAvailable(Descriptors),sequence=GetClipboardSequenceNumber()};
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
}
