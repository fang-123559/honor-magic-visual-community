"""Collect P0 evidence without changing Explorer, icons, display settings, or files."""
import argparse
import ctypes as c
from ctypes import wintypes as w
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import platform
import subprocess
import uuid
import winreg

user32 = c.WinDLL("user32", use_last_error=True)
shcore = c.WinDLL("shcore", use_last_error=True)
shell32 = c.WinDLL("shell32", use_last_error=True)
ole32 = c.WinDLL("ole32", use_last_error=True)


class MonitorInfo(c.Structure):
    _fields_ = [("cbSize", w.DWORD), ("monitor", w.RECT), ("work", w.RECT),
                ("flags", w.DWORD), ("device", w.WCHAR * 32)]


class Guid(c.Structure):
    _fields_ = [("data1", w.DWORD), ("data2", w.WORD), ("data3", w.WORD),
                ("data4", w.BYTE * 8)]


WindowCallback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
MonitorCallback = c.WINFUNCTYPE(w.BOOL, w.HANDLE, w.HDC, c.POINTER(w.RECT), w.LPARAM)
user32.EnumWindows.argtypes = [WindowCallback, w.LPARAM]
user32.EnumWindows.restype = w.BOOL
user32.EnumChildWindows.argtypes = [w.HWND, WindowCallback, w.LPARAM]
user32.EnumChildWindows.restype = w.BOOL
user32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
user32.GetClassNameW.restype = c.c_int
user32.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
user32.GetWindowThreadProcessId.restype = w.DWORD
user32.GetParent.argtypes = [w.HWND]
user32.GetParent.restype = w.HWND
user32.IsWindowVisible.argtypes = [w.HWND]
user32.IsWindowVisible.restype = w.BOOL
user32.GetWindowRect.argtypes = [w.HWND, c.POINTER(w.RECT)]
user32.GetWindowRect.restype = w.BOOL
user32.GetDpiForWindow.argtypes = [w.HWND]
user32.GetDpiForWindow.restype = w.UINT
user32.SendMessageTimeoutW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM,
                                      w.UINT, w.UINT, c.POINTER(c.c_size_t)]
user32.SendMessageTimeoutW.restype = w.LPARAM
user32.EnumDisplayMonitors.argtypes = [w.HDC, c.POINTER(w.RECT), MonitorCallback, w.LPARAM]
user32.EnumDisplayMonitors.restype = w.BOOL
user32.GetMonitorInfoW.argtypes = [w.HANDLE, c.POINTER(MonitorInfo)]
user32.GetMonitorInfoW.restype = w.BOOL
user32.SetThreadDpiAwarenessContext.argtypes = [w.HANDLE]
user32.SetThreadDpiAwarenessContext.restype = w.HANDLE
shcore.GetDpiForMonitor.argtypes = [w.HANDLE, c.c_int, c.POINTER(w.UINT), c.POINTER(w.UINT)]
shcore.GetDpiForMonitor.restype = c.c_long
shcore.GetScaleFactorForMonitor.argtypes = [w.HANDLE, c.POINTER(c.c_int)]
shcore.GetScaleFactorForMonitor.restype = c.c_long
shell32.SHGetKnownFolderPath.argtypes = [c.POINTER(Guid), w.DWORD, w.HANDLE, c.POINTER(c.c_void_p)]
shell32.SHGetKnownFolderPath.restype = c.c_long
ole32.CoTaskMemFree.argtypes = [c.c_void_p]
ole32.CoTaskMemFree.restype = None


def rect_dict(rect):
    return dict(left=rect.left, top=rect.top, right=rect.right, bottom=rect.bottom)


def class_name(hwnd):
    buffer = c.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buffer, len(buffer))
    return buffer.value


def known_folder(folder_id):
    guid = Guid.from_buffer_copy(uuid.UUID(folder_id).bytes_le)
    pointer = c.c_void_p()
    result = shell32.SHGetKnownFolderPath(c.byref(guid), 0, None, c.byref(pointer))
    if result != 0:
        return {"error_hresult": hex(result & 0xFFFFFFFF)}
    try:
        path = c.wstring_at(pointer.value)
        return {"path": path, "exists": Path(path).is_dir()}
    finally:
        ole32.CoTaskMemFree(pointer)


def collect_monitors():
    monitors = []
    # Only the probe thread's DPI context changes; system display settings do not.
    previous = user32.SetThreadDpiAwarenessContext(c.c_void_p(-4))

    @MonitorCallback
    def callback(handle, dc, rect, data):
        info = MonitorInfo()
        info.cbSize = c.sizeof(info)
        if not user32.GetMonitorInfoW(handle, c.byref(info)):
            monitors.append({"handle": int(handle), "error": c.get_last_error()})
            return True
        dpi_x, dpi_y, scale = w.UINT(), w.UINT(), c.c_int()
        dpi_result = shcore.GetDpiForMonitor(handle, 0, c.byref(dpi_x), c.byref(dpi_y))
        scale_result = shcore.GetScaleFactorForMonitor(handle, c.byref(scale))
        monitors.append({"handle": int(handle), "device": info.device,
                         "primary": bool(info.flags & 1),
                         "bounds_physical": rect_dict(info.monitor),
                         "work_area_physical": rect_dict(info.work),
                         "dpi": [dpi_x.value, dpi_y.value] if dpi_result == 0 else None,
                         "scale_percent": scale.value if scale_result == 0 else None,
                         "dpi_hresult": dpi_result, "scale_hresult": scale_result})
        return True

    try:
        if not user32.EnumDisplayMonitors(None, None, callback, 0):
            raise c.WinError(c.get_last_error())
    finally:
        if previous:
            user32.SetThreadDpiAwarenessContext(previous)
    return {"thread_per_monitor_v2_context_set": bool(previous), "items": monitors}


def collect_windows_inner():
    windows, desktop_hosts = [], []
    wanted_classes = {"Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32",
                      "WinUIDesktopWin32WindowClass"}

    def record(hwnd, desktop_child=False):
        name = class_name(hwnd)
        if name not in wanted_classes:
            return
        # A ListView outside the enumerated desktop hierarchy is irrelevant here.
        if name == "SysListView32" and not desktop_child:
            return
        pid = w.DWORD()
        thread_id = user32.GetWindowThreadProcessId(hwnd, c.byref(pid))
        rect = w.RECT()
        rect_ok = user32.GetWindowRect(hwnd, c.byref(rect))
        item = {"hwnd": int(hwnd), "parent_hwnd": int(user32.GetParent(hwnd) or 0),
                "class": name, "pid": pid.value, "tid": thread_id,
                "visible": bool(user32.IsWindowVisible(hwnd)),
                "window_dpi": user32.GetDpiForWindow(hwnd),
                "rect": rect_dict(rect) if rect_ok else None}
        if name == "SysListView32":
            count = c.c_size_t()
            c.set_last_error(0)
            ok = user32.SendMessageTimeoutW(hwnd, 0x1004, 0, 0, 0x0002, 500, c.byref(count))
            item["item_count"] = count.value if ok else None
            item["item_count_query_error"] = None if ok else c.get_last_error()
        windows.append(item)

    @WindowCallback
    def top_callback(hwnd, data):
        name = class_name(hwnd)
        record(hwnd)
        if name in {"Progman", "WorkerW"}:
            desktop_hosts.append(hwnd)
        return True

    @WindowCallback
    def child_callback(hwnd, data):
        record(hwnd, desktop_child=True)
        return True

    if not user32.EnumWindows(top_callback, 0):
        raise c.WinError(c.get_last_error())
    for host in desktop_hosts:
        user32.EnumChildWindows(host, child_callback, 0)
    return list({item["hwnd"]: item for item in windows}.values())


def collect_windows():
    previous = user32.SetThreadDpiAwarenessContext(c.c_void_p(-4))
    try:
        items = collect_windows_inner()
        for item in items:
            item["rect_coordinate_space"] = "physical" if previous else "caller_dpi_virtualized"
        return items
    finally:
        if previous:
            user32.SetThreadDpiAwarenessContext(previous)


def collect_os():
    names = ["ProductName", "DisplayVersion", "CurrentBuildNumber", "UBR", "EditionID"]
    values = {}
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                        r"SOFTWARE\Microsoft\Windows NT\CurrentVersion") as key:
        for name in names:
            try:
                values[name] = winreg.QueryValueEx(key, name)[0]
            except FileNotFoundError:
                values[name] = None
    values["architecture"] = platform.machine()
    values["note"] = "ProductName is a registry label; use build and DisplayVersion for the baseline."
    return values


def collect_processes():
    script = """Get-Process -ErrorAction SilentlyContinue |
Where-Object { $_.ProcessName -match 'seelen|slu|HonorDesktop|ZoneHost|DesktopHost|explorer|lively' } |
Select-Object ProcessName,Id,Path,WorkingSet64,PrivateMemorySize64,CPU | ConvertTo-Json -Depth 3"""
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, text=True, encoding="utf-8", errors="replace",
                            timeout=20)
    if result.returncode != 0:
        return {"error": result.stderr.strip(), "exit_code": result.returncode}
    items = json.loads(result.stdout) if result.stdout.strip() else []
    if isinstance(items, dict):
        items = [items]
    return {"items": items, "note": "One snapshot; WorkingSet64 is total working set, not private working set. CPU is cumulative seconds, not percent."}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    def capture_section(callback):
        try:
            return {"status": "observed", "data": callback()}
        except Exception as error:
            return {"status": "unavailable", "error_type": type(error).__name__,
                    "error": str(error), "winerror": getattr(error, "winerror", None)}

    evidence = {"schema_version": 1,
                "captured_at": datetime.now(timezone(timedelta(hours=8))).isoformat(),
                "scope": "Read-only desktop/environment snapshot; no takeover, no file names collected.",
                "os": capture_section(collect_os), "monitors": capture_section(collect_monitors),
                "known_folders": {
                    "desktop": known_folder("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
                    "public_desktop": known_folder("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25")},
                "desktop_windows": capture_section(collect_windows),
                "processes": capture_section(collect_processes)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(args.output.resolve()),
                      "monitors": evidence["monitors"],
                      "desktop_windows": {
                          "status": evidence["desktop_windows"]["status"],
                          "items": [item for item in evidence["desktop_windows"].get("data", [])
                                    if item["class"] in {"Progman", "SHELLDLL_DefView", "SysListView32"}],
                          "error": evidence["desktop_windows"].get("error")}},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
