using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace MagicCommunity
{
    // Shared by the managed adapter and the Windows PowerShell lifecycle scripts.
    // Keep this file compatible with the system PowerShell C# compiler.
    public static class SystemRuntimeIdentity
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect { public int Left,Top,Right,Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct AppBar { public uint Size;public IntPtr Window;public uint Callback,Edge;public Rect Bounds;public IntPtr Parameter; }
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint message,ref AppBar data);
        public static uint NativeTaskbarState(){var data=new AppBar();data.Size=(uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(AppBar));return SHAppBarMessage(4,ref data).ToUInt32();}
        public static string Plain(string value)
        {
            string path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("System runtime requires a local path.");
            string current = path;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("System runtime path contains a reparse point.");
                current = Path.GetDirectoryName(current) ?? String.Empty;
            }
            return path;
        }
        private static bool Administrator(SecurityIdentifier sid)
        {
            return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                   sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }
        public static void Protected(string path)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !Administrator(owner))
                throw new InvalidDataException("System runtime is not administrator-owned.");
            var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (raw.DiscretionaryAcl == null) throw new InvalidDataException("System runtime has a null DACL.");
            foreach (GenericAce ace in raw.DiscretionaryAcl)
            {
                var common = ace as CommonAce;
                if (common == null) throw new InvalidDataException("System runtime has an unsupported ACL entry.");
                if (common.AceQualifier == AceQualifier.AccessAllowed &&
                    (common.AccessMask & 0x500d0156) != 0 && !Administrator(common.SecurityIdentifier))
                    throw new InvalidDataException("System runtime grants ordinary users write access.");
            }
        }
        public static string Installation(string value)
        {
            string folder = Plain(value);
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MagicCommunity", "SystemRuntime");
            if (!folder.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("System runtime is outside its protected version directory.");
            for (string part = folder; ; part = Path.GetDirectoryName(part) ?? String.Empty)
            {
                Protected(part);
                if (String.Equals(part, Path.GetDirectoryName(parent), StringComparison.OrdinalIgnoreCase)) break;
            }
            return folder;
        }
        public static string Hash(string path)
        {
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("System runtime file exceeds bound.");
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string Payload(string folder, string relative, string expected)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid runtime payload path.");
            foreach (string part in relative.Replace('/', '\\').Split('\\'))
                if (part.Length == 0 || part == "." || part == ".." || part.Contains(":")) throw new InvalidDataException("Invalid runtime payload component.");
            string path = Plain(Path.Combine(folder, relative));
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload escapes runtime.");
            for (string part = path; !String.Equals(part, folder, StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part) ?? String.Empty) Protected(part);
            if (expected == null || expected.Length != 64 || !String.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("System runtime payload hash differs: " + relative);
            return path;
        }
    }
}