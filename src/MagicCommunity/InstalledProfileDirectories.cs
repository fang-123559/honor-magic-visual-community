using System.IO;
using System.Runtime.InteropServices;

namespace MagicCommunity;

internal static class InstalledProfileDirectories
{
    internal static string PrepareBackendDirectory(string root) => PrepareDirectory(root, "data", "native-system");
    internal static string PrepareTransitionDirectory(string root) => PrepareDirectory(root, "runtime", "desktop");

    // The selected profile and marker have been verified. Pin each parent while
    // creating its direct child so an existing junction cannot redirect creation.
    private static string PrepareDirectory(string root, string parentName, string childName)
    {
        using var profile = PinDirectory(root);
        string parent = Path.Combine(root, parentName);
        Directory.CreateDirectory(parent);
        using var parentHandle = PinDirectory(parent);
        string child = Path.Combine(parent, childName);
        Directory.CreateDirectory(child);
        using var childHandle = PinDirectory(child);
        return child;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle PinDirectory(string path)
    {
        const uint readAttributes = 0x80, backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
        var handle = CreateFileW(path, readAttributes, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
            backupSemantics | openReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("安装数据目录必须是真实目录：" + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, ExactSpelling=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access,
        FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

}
