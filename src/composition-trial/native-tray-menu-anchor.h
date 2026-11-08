#pragma once
// Tray applications own their menus. Observe only the process behind the
// clicked icon, briefly, and reposition recognized popup classes without
// copying commands, resizing content or moving application windows.
struct NativeTrayMenuAnchor {
    HWINEVENTHOOK showHook=nullptr,moveHook=nullptr;
    UINT_PTR expiry=0;HWND owner=nullptr,root=nullptr;DWORD pid=0;
    POINT anchor{};RECT work{};bool above=false,moving=false;unsigned moves=0;
    static inline NativeTrayMenuAnchor *current=nullptr;
    ~NativeTrayMenuAnchor(){Reset();}
    void Reset(){
        if(expiry)KillTimer(nullptr,expiry);expiry=0;
        if(showHook)UnhookWinEvent(showHook);if(moveHook)UnhookWinEvent(moveHook);
        showHook=moveHook=nullptr;owner=root=nullptr;pid=0;moves=0;
        if(current==this)current=nullptr;
    }
    static void CALLBACK Expire(HWND,UINT,UINT_PTR id,DWORD){if(current&&current->expiry==id)current->Reset();}
    static void CALLBACK Event(HWINEVENTHOOK,DWORD,HWND window,LONG object,LONG child,DWORD,DWORD){
        if(current&&object==OBJID_WINDOW&&child==CHILDID_SELF)current->Place(window);
    }
    static bool MenuStyle(std::wstring const &value,LONG_PTR style,LONG_PTR extended){
        if(value==L"#32768")return true;
        // QQ/Electron's frameless menu may carry WS_CAPTION and no WS_POPUP.
        // Its transient topmost, non-resizable, no-system-menu surface differs
        // from the normal Chrome_WidgetWin_1 application window.
        if(value==L"Chrome_WidgetWin_1")return (extended&WS_EX_TOPMOST)&&!(style&(WS_THICKFRAME|WS_SYSMENU));
        if(!(style&WS_POPUP)||(style&WS_CAPTION))return false;
        return value.find(L"QWindowPopup")!=std::wstring::npos||
            (value==L"Chrome_WidgetWin_2"&&(extended&WS_EX_TOOLWINDOW));
    }
    static bool MenuClass(HWND window){
        wchar_t name[128]{};GetClassNameW(window,name,128);
        return MenuStyle(name,GetWindowLongPtrW(window,GWL_STYLE),GetWindowLongPtrW(window,GWL_EXSTYLE));
    }
    // Pure geometry shared with isolated checks. Never resize a third-party
    // popup: the application owns its scrolling and command hit rectangles.
    static POINT Position(RECT rect,RECT area,POINT trigger,bool above,bool first){
        LONG width=rect.right-rect.left,height=rect.bottom-rect.top;
        LONG x=first?trigger.x-width/2:rect.left,y=first?(above?trigger.y-height:trigger.y):rect.top;
        return {std::clamp(x,area.left,std::max(area.left,area.right-width)),
                std::clamp(y,area.top,std::max(area.top,area.bottom-height))};
    }
    void Place(HWND window){
        if(moving||moves>=12||!IsWindow(owner)||!IsWindowVisible(window)||!MenuClass(window))return;
        DWORD actual=0;GetWindowThreadProcessId(window,&actual);if(actual!=pid)return;
        RECT rect{};if(!GetWindowRect(window,&rect)||rect.right<=rect.left||rect.bottom<=rect.top)return;
        if(root&&!IsWindowVisible(root)){Reset();return;}
        if(!root)root=window;
        auto point=Position(rect,work,anchor,above,window==root);
        if(point.x==rect.left&&point.y==rect.top)return;
        moving=true;++moves;
        SetWindowPos(window,nullptr,point.x,point.y,0,0,SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOOWNERZORDER);
        moving=false;
    }
    bool Arm(HWND iconOwner,POINT trigger,bool openAbove,RECT bar){
        Reset();DWORD process=0;if(!IsWindow(iconOwner)||!GetWindowThreadProcessId(iconOwner,&process)||!process)return false;
        MONITORINFO screen{sizeof(screen)};if(!GetMonitorInfoW(MonitorFromPoint(trigger,MONITOR_DEFAULTTONEAREST),&screen))return false;
        owner=iconOwner;pid=process;anchor=trigger;above=openAbove;work=screen.rcWork;
        if(above)work.bottom=std::min(work.bottom,bar.top);else work.top=std::max(work.top,bar.bottom);
        if(work.bottom<=work.top){Reset();return false;}
        current=this;
        showHook=SetWinEventHook(EVENT_OBJECT_SHOW,EVENT_OBJECT_SHOW,nullptr,Event,pid,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
        moveHook=SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE,EVENT_OBJECT_LOCATIONCHANGE,nullptr,Event,pid,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
        expiry=SetTimer(nullptr,0,3000,Expire);
        if(!showHook||!moveHook||!expiry){Reset();return false;}return true;
    }
};
inline NativeTrayMenuAnchor &TrayMenuAnchor(){static NativeTrayMenuAnchor anchor;return anchor;}
