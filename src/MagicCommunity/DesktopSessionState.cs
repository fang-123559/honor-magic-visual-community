using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicCommunity;

internal static class DesktopSessionState
{
    internal static bool CanRefresh()
    {
        using var process=Process.GetCurrentProcess();
        if(!WTSQuerySessionInformationW(IntPtr.Zero,process.SessionId,8,out var buffer,out int bytes))return false;
        try{return bytes>=4&&Marshal.ReadInt32(buffer)==0;}
        finally{WTSFreeMemory(buffer);}
    }
    [DllImport("wtsapi32.dll",SetLastError=true)]private static extern bool WTSQuerySessionInformationW(IntPtr server,int session,int kind,out IntPtr buffer,out int bytes);
    [DllImport("wtsapi32.dll")]private static extern void WTSFreeMemory(IntPtr buffer);
}
