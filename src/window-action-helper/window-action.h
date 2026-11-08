#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>
namespace magic::window_action {
inline bool Foreground(HWND target) {
    HWND foreground=GetForegroundWindow();
    if(foreground==target)return true;
    // A visible owned modal is legitimate; arbitrary same-process windows are not.
    DWORD pid=0, other=0;GetWindowThreadProcessId(target,&pid);
    GetWindowThreadProcessId(foreground,&other);
    return pid && pid==other && foreground && IsWindowVisible(foreground) &&
        !IsIconic(foreground) && GetAncestor(foreground,GA_ROOTOWNER)==target;
}
struct Result { bool accepted=false, restored=false, activated=false, minimized=false; DWORD error=0; };
inline Result Perform(HWND hwnd, std::wstring const& action) {
    Result result;
    if(!IsWindow(hwnd)){result.error=ERROR_INVALID_WINDOW_HANDLE;return result;}
    if(action==L"close"){
        // Request the normal application close path, including its save prompt.
        // Queue acceptance is not a promise that the application has exited.
        SetLastError(0);result.accepted=PostMessageW(hwnd,WM_CLOSE,0,0)!=FALSE;
        if(!result.accepted)result.error=GetLastError();return result;
    }
    bool minimize=action==L"minimize";
    if(action!=L"activate" && action!=L"restore" && !minimize){result.error=ERROR_INVALID_PARAMETER;return result;}
    SetLastError(0);
    if(minimize || IsIconic(hwnd)) {
        if(!ShowWindowAsync(hwnd,minimize?SW_MINIMIZE:SW_RESTORE))result.error=GetLastError();
    }
    if(!minimize){SetLastError(0);if(!SetForegroundWindow(hwnd) && !result.error)result.error=GetLastError();}
    // Interactive bounded completion check on the IPC worker, never the UI/idle path.
    for(int i=0;i<21;i++) {
        if(!IsWindow(hwnd))break;
        result.minimized=IsIconic(hwnd)!=FALSE;
        result.restored=IsWindowVisible(hwnd) && !result.minimized;
        result.activated=result.restored && Foreground(hwnd);
        result.accepted=minimize?result.minimized:(result.restored&&result.activated);
        if(result.accepted || result.error==ERROR_ACCESS_DENIED)break;
        Sleep(15);
    }
    return result;
}
}
