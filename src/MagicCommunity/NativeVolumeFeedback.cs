using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicCommunity;

internal static class NativeVolumeFeedback
{
    internal static bool ShouldPlay(bool released,string operation,double level,bool muted) =>
        released && operation=="audio-action" && level>0 && !muted;

    // Use the user's Windows sample on the current default endpoint at its confirmed
    // master volume. A new release replaces the previous sample in this process.
    internal static bool Play()
    {
        const uint asyncFile=0x0001|0x0002|0x20000;
        try {
            using var scheme=Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\.Default\.Current");
            string? configured=scheme?.GetValue(null) as string;
            if(configured is not null && string.IsNullOrWhiteSpace(configured))return true; // Silent sound scheme.
            if(configured is not null && PlaySound(Environment.ExpandEnvironmentVariables(configured),IntPtr.Zero,asyncFile))return true;
            return PlaySound(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Media","Windows Background.wav"),IntPtr.Zero,asyncFile);
        } catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    [DllImport("winmm.dll",EntryPoint="PlaySoundW",CharSet=CharSet.Unicode)]
    private static extern bool PlaySound(string sound,IntPtr module,uint flags);
}
