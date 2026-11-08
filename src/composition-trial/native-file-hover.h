#pragma once
// Only the one hovered layout member is read. No directory scan or hover cache.
inline void Window::HideFileHover(bool releaseLayout){
    if(hwnd)KillTimer(hwnd,111);++fileHoverEpoch;fileHoverWaiting=false;fileHoverKey.clear();fileHoverIndex=-1;if(releaseLayout){handoffHoverLayout=nullptr;handoffHoverMarkers.clear();handoffHoverKey.clear();}if(fileTooltip)fileTooltip->Hide(false);
}
inline void Window::FileHover(float x,float y){
    if(!app.model.desktopRuntime||pressed||dragging||marquee||GridEditing()||renaming||closing||selected.size()>1){HideFileHover();return;}
    auto key=Hit(x,y);auto tile=Find(key);int attachment=-1;
    if(tile&&tile->card&&Str(tile->card,L"type")==L"system.handoff")attachment=HandoffAttachmentHit(*tile,x,y);
    else if(!tile||!key.starts_with(L"item:")||tile->card||!tile->folder.empty()){HideFileHover();return;}
    if(tile&&tile->card&&attachment<0){HideFileHover();return;}
    if(key==fileHoverKey&&attachment==fileHoverIndex)return;HideFileHover();fileHoverKey=key;fileHoverIndex=attachment;fileHoverWaiting=true;SetTimer(hwnd,111,attachment>=0?110:550,nullptr);
}
inline void Window::ShowFileHover(){
    KillTimer(hwnd,111);if(!fileHoverWaiting||fileHoverKey.empty()||pressed||dragging||renaming||marquee||!IsWindowVisible(hwnd))return;
    fileHoverWaiting=false;auto key=fileHoverKey;auto epoch=fileHoverEpoch;auto attachment=fileHoverIndex;auto state=providerState;
    JsonObject request;Put(request,L"op",std::wstring(attachment>=0?L"handoff-info":L"file-info"));if(attachment>=0)Put(request,L"index",attachment);Put(request,L"board",boardId);Put(request,L"key",key);if(!folderId.empty())Put(request,L"folder",folderId);
    app.Request(request,[state,key,epoch,attachment](JsonObject const &response){
        auto window=Live(state);if(!window||!Bool(response,L"ok")||window->fileHoverKey!=key||window->fileHoverEpoch!=epoch||window->pressed||window->dragging||window->renaming||window->marquee||!IsWindowVisible(window->hwnd))return;
        POINT cursor{};GetCursorPos(&cursor);ScreenToClient(window->hwnd,&cursor);if(window->Hit(cursor.x*96/window->dpi,cursor.y*96/window->dpi)!=key)return;
        auto info=response.GetNamedObject(L"result");auto text=Str(info,L"name")+L"\n最后修改时间："+Str(info,L"modified");if(text.size()>1024)return;
        auto r=attachment>=0?window->handoffHoverBounds:window->Bounds(key);float scale=window->dpi/96;POINT origin{};ClientToScreen(window->hwnd,&origin);
        RECT icon{origin.x+static_cast<LONG>(r.left*scale),origin.y+static_cast<LONG>(r.top*scale),origin.x+static_cast<LONG>(r.right*scale),origin.y+static_cast<LONG>(r.bottom*scale)};
        JsonObject packet,payload,data,position;Put(packet,L"kind",std::wstring(L"@magic/bar-tooltip"));Put(packet,L"token",std::wstring(L"local-file-tooltip"));Put(position,L"x",icon.left);Put(position,L"y",icon.bottom+6);payload.Insert(L"desiredPosition",position);Put(payload,L"alignX",std::wstring(L"Start"));Put(payload,L"alignY",std::wstring(L"Start"));
        Put(data,L"text",text);if(attachment>=0)Put(data,L"previewPng",Str(info,L"png"));data.Insert(L"dark",JsonValue::CreateBooleanValue(Preview70Theme::systemDark));data.Insert(L"rows",JsonArray{});packet.Insert(L"payload",payload);packet.Insert(L"data",data);packet.Insert(L"account",JsonObject{});
        if(!window->fileTooltip)window->fileTooltip=std::make_unique<NativePopup>(window->app);window->fileTooltip->Show(packet);
        RECT tip{},overlap{};GetWindowRect(window->fileTooltip->hwnd,&tip);MONITORINFO monitor{sizeof(monitor)};GetMonitorInfoW(MonitorFromRect(&icon,MONITOR_DEFAULTTONEAREST),&monitor);
        if(IntersectRect(&overlap,&tip,&icon)){
            int w=tip.right-tip.left,h=tip.bottom-tip.top,x=tip.left,y=tip.top;
            if(icon.top-h-6>=monitor.rcWork.top)y=icon.top-h-6;
            else if(icon.right+w+6<=monitor.rcWork.right)x=icon.right+6;
            else if(icon.left-w-6>=monitor.rcWork.left)x=icon.left-w-6;
            else {window->fileTooltip->Hide(false);return;}
            SetWindowPos(window->fileTooltip->hwnd,HWND_TOPMOST,x,y,w,h,SWP_NOACTIVATE);
        }
    });
}

inline size_t Window::AdditionalSurfaceBytes()const{
    size_t result=desktopNameBytes+(fileTooltip?fileTooltip->surfaceBytes+fileTooltip->glassSheet.bytes:0);for(auto const &[_,v]:mediaTitles)result+=v.bytes;return result;
}
