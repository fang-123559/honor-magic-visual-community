#pragma once
#include "trial.h"
// Package activation and the transition launcher use separate copies of the
// same renderer. Compare them once per helper process, retaining a process
// handle so a recycled PID cannot reuse the result. No permanent timer/hook.
inline bool NativeRendererFilesMatch(wchar_t const *left,wchar_t const *right){
    if(_wcsicmp(left,right)==0)return true;
    HANDLE a=CreateFileW(left,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    HANDLE b=CreateFileW(right,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    bool match=false;
    if(a!=INVALID_HANDLE_VALUE&&b!=INVALID_HANDLE_VALUE){
        BY_HANDLE_FILE_INFORMATION ai{},bi{};LARGE_INTEGER as{},bs{};
        if(GetFileInformationByHandle(a,&ai)&&GetFileInformationByHandle(b,&bi)&&!((ai.dwFileAttributes|bi.dwFileAttributes)&FILE_ATTRIBUTE_REPARSE_POINT)&&GetFileSizeEx(a,&as)&&GetFileSizeEx(b,&bs)&&as.QuadPart==bs.QuadPart&&as.QuadPart>0&&as.QuadPart<=64*1024*1024){
            std::array<BYTE,16384> ab{},bb{};DWORD an=0,bn=0;match=true;LONGLONG remaining=as.QuadPart;
            while(remaining>0){DWORD wanted=static_cast<DWORD>(std::min<LONGLONG>(remaining,ab.size()));if(!ReadFile(a,ab.data(),wanted,&an,nullptr)||!ReadFile(b,bb.data(),wanted,&bn,nullptr)||an!=wanted||bn!=wanted||memcmp(ab.data(),bb.data(),an)){match=false;break;}remaining-=an;}
        }
    }
    if(a!=INVALID_HANDLE_VALUE)CloseHandle(a);if(b!=INVALID_HANDLE_VALUE)CloseHandle(b);return match;
}
inline bool NativeTransitionOwned(HWND window){
    struct Identity{DWORD pid=0;HANDLE process=nullptr;bool matches=false;~Identity(){if(process)CloseHandle(process);}};
    static Identity cached;DWORD pid=0;GetWindowThreadProcessId(window,&pid);
    if(cached.pid==pid&&cached.process&&WaitForSingleObject(cached.process,0)==WAIT_TIMEOUT)return cached.matches;
    if(cached.process)CloseHandle(cached.process);cached.pid=pid;cached.matches=false;
    cached.process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);
    wchar_t path[32768]{},self[32768]{};DWORD size=32768;
    if(cached.process&&QueryFullProcessImageNameW(cached.process,0,path,&size)&&GetModuleFileNameW(nullptr,self,32768))cached.matches=NativeRendererFilesMatch(path,self);
    return cached.matches;
}
// One event-driven z-order transaction for bars and transient native surfaces.
inline void NativeLayerPosition(HWND positioned,int x,int y,int w,int h,UINT flags){
    struct Windows {std::vector<HWND> overlays,transitions;} windows;
    // GetWindow(GW_HWNDNEXT) can revisit a window while another thread changes
    // z-order. EnumWindows avoids following that live linked list indefinitely.
    EnumWindows([](HWND window,LPARAM context)->BOOL{
        if(!IsWindowVisible(window))return TRUE;
        auto &found=*reinterpret_cast<Windows*>(context);DWORD pid=0;GetWindowThreadProcessId(window,&pid);
        wchar_t name[80]{};GetClassNameW(window,name,80);
        if(pid==GetCurrentProcessId()&&(wcscmp(name,L"MagicCompositionPopupWindow")==0||wcscmp(name,L"MagicNativeDwmPreview")==0||wcscmp(name,L"MagicCompositionInputHost")==0))found.overlays.push_back(window);
        else if(wcscmp(name,L"MagicNativeTransition")==0)found.transitions.push_back(window);
        return TRUE;
    },reinterpret_cast<LPARAM>(&windows));
    auto &overlays=windows.overlays;
    wchar_t targetClass[80]{};GetClassNameW(positioned,targetClass,80);
    bool overlay=wcscmp(targetClass,L"MagicCompositionPopupWindow")==0||wcscmp(targetClass,L"MagicNativeDwmPreview")==0||wcscmp(targetClass,L"MagicCompositionInputHost")==0;
    // Bottom to top keeps child menus above their parent without activating any window.
    std::reverse(overlays.begin(),overlays.end());
    if(overlay&&std::find(overlays.begin(),overlays.end(),positioned)==overlays.end())overlays.push_back(positioned);
    if(!overlay)overlays.insert(overlays.begin(),positioned);
    for(HWND transition:windows.transitions){
        if(IsWindowVisible(transition)&&NativeTransitionOwned(transition))overlays.push_back(transition);
    }
    auto batch=BeginDeferWindowPos(static_cast<int>(overlays.size()));
    if(batch){
        for(HWND window:overlays){if(!batch)break;
            batch=window==positioned?DeferWindowPos(batch,window,HWND_TOPMOST,x,y,w,h,flags):DeferWindowPos(batch,window,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
        }
        if(batch&&EndDeferWindowPos(batch))return;
    }
    for(HWND window:overlays){
        if(window==positioned)SetWindowPos(window,HWND_TOPMOST,x,y,w,h,flags);
        else SetWindowPos(window,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
    }
}
