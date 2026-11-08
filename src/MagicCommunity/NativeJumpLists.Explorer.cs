using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MagicCommunity;
internal static partial class NativeJumpLists
{
    internal const string ExplorerId = "Microsoft.Windows.Explorer";
    internal static bool IsWindowsExplorer(string path) => LocalPath(path) &&
        string.Equals(Path.GetFullPath(path), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);

    // Read-only compatibility boundary. Windows exposes Recent/Frequent publicly,
    // but not pinned destinations. This Win10+ IID is queried only in the existing
    // disposable Shell STA. No pin/write/list-commit methods are declared or used.
    // ABI reference: Open-Shell StartMenuDLL/JumpLists.cpp, IAutomaticDestinationList10b.
    [ComImport, Guid("E9C5EF8D-FD41-4F72-BA87-EB03BAD5817C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomaticDestinationsReader
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr link, IntPtr reserved);
        [PreserveSig] int HasList([MarshalAs(UnmanagedType.Bool)] out bool available);
        [PreserveSig] int GetList(int type, uint count, uint flags, ref Guid iid, out ITaskArray items);
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationLink
    {
        void GetPath(IntPtr path, int length, IntPtr data, uint flags);
        [PreserveSig] int GetIDList(out IntPtr id);
    }
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(IntPtr id, ref Guid iid, out IShellItem item);

    private static string ItemName(IShellItem item, uint kind)
    {
        if (item.GetDisplayName(kind, out var text) < 0) return "";
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    private static Entry[] ExplorerEntries(ITaskArray items)
    {
        var found = new List<Entry>();
        if (items.GetCount(out uint count) < 0) return [];
        for (uint index = 0; index < Math.Min(count, 128); index++)
        {
            object? value = null; IShellItem? item = null; IntPtr pidl = IntPtr.Zero;
            try
            {
                var unknown = new Guid("00000000-0000-0000-C000-000000000046");
                if (items.GetAt(index, ref unknown, out value) < 0) continue;
                item = value as IShellItem;
                if (item is null && value is IDestinationLink link && link.GetIDList(out pidl) >= 0 && pidl != IntPtr.Zero)
                { var iid = typeof(IShellItem).GUID; if (SHCreateItemFromIDList(pidl, ref iid, out item) < 0) continue; }
                if (item is null) continue;
                string path = ItemName(item, 0x80028000), label = ItemName(item, 0); // parsing identity and localized display name
                if (path.Length == 0 || path.Length > 32760 || path.Any(char.IsControl) || label.Length == 0 || label.Any(char.IsControl)) continue;
                string id = Key(ExplorerId, path);
                if (found.All(x => x.Id != id)) found.Add(new(id, label.Length > 120 ? label[..117] + "…" : label, path));
            }
            finally
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
                if (item is not null && !ReferenceEquals(item, value)) Marshal.ReleaseComObject(item);
                if (value is not null) Marshal.ReleaseComObject(value);
            }
        }
        return found.ToArray();
    }
    private static Result ReadExplorer(Stopwatch clock)
    {
        object? instance = null; Entry[] pinned = [], frequent = [];
        bool compatible = false;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("F0AE1542-F497-484B-A175-A20DB09144BA"))!);
            if (instance is IAutomaticDestinationsReader reader && reader.Initialize(ExplorerId, IntPtr.Zero, IntPtr.Zero) >= 0)
            {
                Entry[] Read(int type, uint count)
                {
                    var iid = typeof(ITaskArray).GUID;
                    if (reader.GetList(type, count, 1, ref iid, out var items) < 0 || items is null) return [];
                    try { return ExplorerEntries(items); } finally { Marshal.ReleaseComObject(items); }
                }
                pinned = Read(0, 128); frequent = Read(2, 16); compatible = true;
            }
        }
        catch (COMException) { }
        finally { if (instance is not null) Marshal.ReleaseComObject(instance); }
        if (!compatible)
        {
            // Keep usable public Frequent data when a future Windows removes the
            // private read interface; never invent or overwrite a pinned list.
            object? source = null; IObjectArray? array = null;
            try
            {
                source = Activator.CreateInstance(Type.GetTypeFromCLSID(new("86bec222-30f2-47e0-9f25-60d11cd75c28"))!);
                var lists = (IApplicationDocumentLists)source!; var iid = typeof(ITaskArray).GUID;
                if (lists.SetAppID(ExplorerId) >= 0 && lists.GetList(1, 16, ref iid, out array) >= 0)
                    frequent = ExplorerEntries((ITaskArray)array);
            }
            catch (COMException) { }
            finally { if (array is not null) Marshal.ReleaseComObject(array); if (source is not null) Marshal.ReleaseComObject(source); }
        }
        var keys = pinned.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return new(ExplorerId, [], clock.Elapsed.TotalMilliseconds, compatible ? "Windows Explorer destinations" : "Windows Explorer public Frequent")
        { Pinned = pinned, Frequent = frequent.Where(x => !keys.Contains(x.Id)).ToArray() };
    }
    private static object OpenExplorer(JsonElement request, string id)
    {
        var list = Read(request);
        var entry = list.Pinned.Concat(list.Frequent).SingleOrDefault(x => x.Id == id)
            ?? throw new InvalidDataException("文件夹已不在 Windows 跳转列表中。");
        var iid = typeof(IShellItem).GUID; IShellItem? item = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(entry.Path, IntPtr.Zero, ref iid, out item));
            DesktopContextMenu.InvokeDefaultAction(item);
            return new { accepted = true, scope = "Windows Explorer destination activation" };
        }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }
}
