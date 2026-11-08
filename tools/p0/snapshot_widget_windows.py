"""Read physical rectangles of this experiment's named windows for a supplied PID."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
from pathlib import Path

U = C.WinDLL("user32", use_last_error=True)


class Rect(C.Structure):
    _fields_ = [(name, W.LONG) for name in ["left", "top", "right", "bottom"]]


U.SetThreadDpiAwarenessContext.argtypes = [W.HANDLE]
U.SetThreadDpiAwarenessContext.restype = W.HANDLE
U.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
U.GetWindowThreadProcessId.restype = W.DWORD
U.GetWindowTextW.argtypes = [W.HWND, W.LPWSTR, C.c_int]
U.GetWindowTextW.restype = C.c_int
U.GetWindowRect.argtypes = [W.HWND, C.POINTER(Rect)]
U.GetWindowRect.restype = W.BOOL
U.IsWindowVisible.argtypes = [W.HWND]
U.IsWindowVisible.restype = W.BOOL
U.GetDpiForWindow.argtypes = [W.HWND]
U.GetDpiForWindow.restype = W.UINT
U.GetParent.argtypes = [W.HWND]
U.GetParent.restype = W.HWND
Callback = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
U.EnumWindows.argtypes = [Callback, W.LPARAM]
U.EnumWindows.restype = W.BOOL


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.relative_to(Path(__file__).resolve().parents[2])
    if output.exists():
        raise FileExistsError(output)
    previous = U.SetThreadDpiAwarenessContext(W.HANDLE(-4))
    rows = []

    @Callback
    def visit(hwnd, data):
        pid = W.DWORD()
        U.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value != args.pid:
            return True
        title = C.create_unicode_buffer(512)
        U.GetWindowTextW(hwnd, title, len(title))
        if title.value != "P0 Memory Probe":
            return True
        rect = Rect()
        if not U.GetWindowRect(hwnd, C.byref(rect)):
            rows.append({"hwnd": hwnd, "status": "unavailable", "winerror": C.get_last_error()})
            return True
        rows.append({"hwnd": hwnd, "pid": pid.value, "status": "observed",
                     "visible_flag": bool(U.IsWindowVisible(hwnd)), "dpi": U.GetDpiForWindow(hwnd),
                     "parent_or_owner": U.GetParent(hwnd),
                     "rect_physical": {name: getattr(rect, name) for name, _ in Rect._fields_}})
        return True

    try:
        if not previous or not U.EnumWindows(visit, 0):
            raise C.WinError(C.get_last_error())
    finally:
        if previous:
            U.SetThreadDpiAwarenessContext(previous)
    report = {"pid": args.pid, "coordinate_space": "physical", "windows": rows,
              "limitation": "Window visibility flag and geometry are not visual or occlusion acceptance"}
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
