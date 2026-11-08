using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MagicCommunity.Setup
{
    // A PIDL link carries the AppsFolder item and its icon across MSIX upgrades.
    public static class DesktopLink
    {
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath(IntPtr path, int size, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint mask, out uint attributes);

        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ILIsEqual(IntPtr left, IntPtr right);

        private static readonly Guid ShellLinkClass = new Guid("00021401-0000-0000-C000-000000000046");

        private static IntPtr Target(string name)
        {
            IntPtr pidl;
            uint ignored;
            Marshal.ThrowExceptionForHR(SHParseDisplayName(name, IntPtr.Zero, out pidl, 0, out ignored));
            return pidl;
        }

        public static void Save(string target, string file)
        {
            IntPtr pidl = Target(target);
            object shellLink = null;
            try
            {
                shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, true));
                ((IShellLinkW)shellLink).SetIDList(pidl);
                ((IPersistFile)shellLink).Save(file, true);
            }
            finally
            {
                if (shellLink != null) Marshal.FinalReleaseComObject(shellLink);
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        public static bool PointsTo(string target, string file)
        {
            IntPtr expected = Target(target), actual = IntPtr.Zero;
            object shellLink = null;
            try
            {
                shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, true));
                ((IPersistFile)shellLink).Load(file, 0);
                ((IShellLinkW)shellLink).GetIDList(out actual);
                return actual != IntPtr.Zero && ILIsEqual(actual, expected);
            }
            finally
            {
                if (shellLink != null) Marshal.FinalReleaseComObject(shellLink);
                Marshal.FreeCoTaskMem(actual);
                Marshal.FreeCoTaskMem(expected);
            }
        }
    }
}
