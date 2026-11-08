#pragma once
// Apps use a Composition-only panel and one owned opaque GDI host per offered
// text field. No redirected child plane survives or moves with the panel fade.
inline bool NativePopup::InputFamily(HWND window)const{
    if(!window)return false;for(auto const &[_,host]:inputHosts)if(window==host||IsChild(host,window))return true;return false;
}
inline HWND NativePopup::EnsureInput(PopupRow const &row){
    if(auto found=edits.find(row.id);found!=edits.end())return found->second;
    HWND parent=hwnd;
    if(kind==L"@magic/apps-menu"){
        WNDCLASSW type{};type.lpfnWndProc=InputHostProc;type.hInstance=GetModuleHandleW(nullptr);type.lpszClassName=L"MagicCompositionInputHost";type.hCursor=LoadCursorW(nullptr,IDC_IBEAM);RegisterClassW(&type);
        parent=CreateWindowExW(WS_EX_TOOLWINDOW,type.lpszClassName,L"",WS_POPUP|WS_CLIPCHILDREN,-30000,-30000,1,1,hwnd,nullptr,type.hInstance,this);
        if(!parent)throw_last_error();inputHosts[row.id]=parent;
    }
    auto value=row.id==L"folder-name"?Str(Data(),L"favoriteFolderName"):L"";
    HWND edit=CreateWindowExW(0,L"EDIT",value.c_str(),WS_CHILD|WS_TABSTOP|ES_AUTOHSCROLL|(row.control==L"password"?ES_PASSWORD:0),0,0,1,1,parent,nullptr,GetModuleHandleW(nullptr),nullptr);
    if(!edit)throw_last_error();edits[row.id]=edit;
    SendMessageW(edit, EM_SETLIMITTEXT, 1024, 0);
    if(parent!=hwnd)SetWindowSubclass(edit,InputEditProc,1,reinterpret_cast<DWORD_PTR>(this));return edit;
}
inline void NativePopup::ShowInput(std::wstring const &id,bool show){
    auto edit=edits.find(id);if(edit==edits.end())return;show=show&&visible&&!dismissing;
    auto setVisible=[show](HWND window){
        if(((GetWindowLongPtrW(window,GWL_STYLE)&WS_VISIBLE)!=0)!=show)
            ShowWindow(window,show?SW_SHOWNOACTIVATE:SW_HIDE);
    };
    if(auto host=inputHosts.find(id);host!=inputHosts.end())setVisible(host->second);
    setVisible(edit->second);
}
inline void NativePopup::PlaceInput(std::wstring const &id,int x,int y,int w,int h,bool show){
    auto edit=edits.find(id);if(edit==edits.end())return;
    auto place=[w,h](HWND window,HWND parent,int left,int top){
        RECT actual{};GetWindowRect(window,&actual);
        if(parent)MapWindowPoints(nullptr,parent,reinterpret_cast<POINT*>(&actual),2);
        if(actual.left!=left||actual.top!=top||actual.right-actual.left!=w||actual.bottom-actual.top!=h)
            SetWindowPos(window,nullptr,left,top,w,h,SWP_NOZORDER|SWP_NOACTIVATE);
    };
    if(auto host=inputHosts.find(id);host!=inputHosts.end()){
        POINT p{x,y};ClientToScreen(hwnd,&p);
        place(host->second,nullptr,p.x,p.y);
        place(edit->second,host->second,0,0);
    }else place(edit->second,hwnd,x,y);
    ShowInput(id,show);
}
inline LRESULT CALLBACK NativePopup::InputHostProc(HWND window,UINT message,WPARAM w,LPARAM l){
    auto self=reinterpret_cast<NativePopup*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(message==WM_NCCREATE){self=static_cast<NativePopup*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(self));}
    if(self){
        if(message==WM_COMMAND||message==WM_CTLCOLOREDIT)return SendMessageW(self->hwnd,message,w,l);
        if(message==WM_ERASEBKGND){RECT rect{};GetClientRect(window,&rect);FillRect(reinterpret_cast<HDC>(w),&rect,self->inputBackground?self->inputBackground:GetSysColorBrush(COLOR_WINDOW));return 1;}
        if(message==WM_SETFOCUS){auto edit=GetWindow(window,GW_CHILD);if(edit)SetFocus(edit);return 0;}
        if(message==WM_ACTIVATE&&LOWORD(w)==WA_INACTIVE)return SendMessageW(self->hwnd,message,w,l);
        if(message==WM_CLOSE)return SendMessageW(self->hwnd,message,w,l);
    }
    return DefWindowProcW(window,message,w,l);
}
inline LRESULT CALLBACK NativePopup::InputEditProc(HWND edit,UINT message,WPARAM w,LPARAM l,UINT_PTR,DWORD_PTR data){
    auto self=reinterpret_cast<NativePopup*>(data);
    if(message==WM_IME_STARTCOMPOSITION)self->composingInputs.insert(edit);
    if(message==WM_IME_ENDCOMPOSITION)self->composingInputs.erase(edit);
    if(message==WM_KEYDOWN&&(w==VK_ESCAPE||w==VK_RETURN)&&!self->composingInputs.contains(edit))return SendMessageW(self->hwnd,message,w,l);
    if(message==WM_NCDESTROY){self->composingInputs.erase(edit);RemoveWindowSubclass(edit,InputEditProc,1);}
    return DefSubclassProc(edit,message,w,l);
}
