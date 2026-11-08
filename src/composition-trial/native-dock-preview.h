#pragma once
// weg-preview.scss: 256 DIP cards, 8 DIP padding/gaps, at most four columns.
// Only visible windows retain DWM relationships; scrolling rebuilds them on
// input. There is no screenshot capture, refresh timer or compatibility page.
inline void NativePopup::BuildDockPreviewRows() {
    rows.clear();auto windows=Data().GetNamedArray(L"windows",JsonArray{});
    int columns=std::max(1,static_cast<int>((width-8)/264));float total=std::ceil(windows.Size()/static_cast<float>(columns))*199+8;
    previewScroll=std::clamp(previewScroll,0.f,std::max(0.f,total-height));
    for(UINT i=0;i<windows.Size()&&i<256;i++) {
        auto item=windows.GetAt(i).GetObject();float x=8+i%columns*264.f,y=8+i/columns*199.f-previewScroll;
        PopupRow row;row.id=row.key=L"window:"+std::to_wstring(static_cast<long long>(Num(item,L"hwnd")));row.handle=std::to_wstring(static_cast<long long>(Num(item,L"hwnd")));row.label=Str(item,L"title");row.control=L"dock-preview";row.action=L"focus";row.rect={x,y,x+256,y+191};row.offscreen=row.rect.bottom<=8||row.rect.top>=height-8;rows.push_back(row);
        PopupRow close;close.id=close.key=L"close:"+row.handle;close.handle=row.handle;close.label=Ui(L"关闭",L"Close");close.action=L"close";close.control=L"dock-preview-close";close.rect={x+216,y+8,x+248,y+40};close.offscreen=row.offscreen;rows.push_back(close);
    }
    UpdateTaskPreviews();
}
inline void NativePopup::PaintDockPreview(ID2D1DeviceContext *c) {
    NativePaint p(c,app.graphics,dark);auto &theme=p.theme;p.Material({0,0,width,height},NativeMaterial::Panel,Glass(),10);
    c->PushAxisAlignedClip({8,8,width-8,height-8},D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    for(int i=0;i<static_cast<int>(rows.size());++i){auto const &row=rows[i];if(row.offscreen)continue;auto r=row.rect;
        if(row.control==L"dock-preview"){
            p.Shadow(r,10);p.Fill(r,theme.Film(),10);
            p.Text(row.label,{r.left+12,r.top+8,r.right-48,r.top+40},12.8f,theme.Ink(),600);
            p.Fill({r.left+8,r.top+48,r.right-8,r.bottom-8},theme.Sunken(),10);
        }else{
            // No colored hover/focus overlay on preview controls.
            previewSvg.Draw(c,L"IoClose",{r.left+8,r.top+8,r.right-8,r.bottom-8},theme.Ink());
        }
    }
    c->PopAxisAlignedClip();
}
inline void NativePopup::UpdateDockPreviewThumbnails() {
    for(auto const &row:rows){if(row.control!=L"dock-preview"||row.offscreen)continue;
        auto source=reinterpret_cast<HWND>(static_cast<INT_PTR>(_wcstoi64(row.handle.c_str(),nullptr,10)));if(!IsWindow(source))continue;
        HTHUMBNAIL thumbnail=nullptr;if(FAILED(DwmRegisterThumbnail(hwnd,source,&thumbnail)))continue;
        SIZE sourceSize{};if(FAILED(DwmQueryThumbnailSourceSize(thumbnail,&sourceSize))||sourceSize.cx<=0||sourceSize.cy<=0){DwmUnregisterThumbnail(thumbnail);continue;}
        float left=row.rect.left+8,top=row.rect.top+48,w=240,h=135;
        // The reference fixes image height and crops wider images at the center.
        float factor=h/sourceSize.cy,imageW=sourceSize.cx*factor;
        RECT src{0,0,sourceSize.cx,sourceSize.cy};
        if(imageW>w){float crop=(sourceSize.cx-w/factor)/2;src.left=static_cast<LONG>(crop);src.right=sourceSize.cx-src.left;imageW=w;}else left+=(w-imageW)/2;
        float clipTop=std::max(top,8.f),clipBottom=std::min(top+h,height-8);if(clipBottom<=clipTop){DwmUnregisterThumbnail(thumbnail);continue;}
        src.top=static_cast<LONG>((clipTop-top)/factor);src.bottom=static_cast<LONG>((clipBottom-top)/factor);
        float scale=dpi/96;DWM_THUMBNAIL_PROPERTIES props{};props.dwFlags=DWM_TNP_RECTDESTINATION|DWM_TNP_RECTSOURCE|DWM_TNP_VISIBLE|DWM_TNP_OPACITY|DWM_TNP_SOURCECLIENTAREAONLY;
        size_t bytes=static_cast<size_t>(imageW*scale*(clipBottom-clipTop)*scale*4);if(taskThumbnails.size()>=6||taskPreviewBytes+bytes>8*1024*1024){DwmUnregisterThumbnail(thumbnail);break;}
        props.rcSource=src;props.rcDestination={static_cast<LONG>(left*scale),static_cast<LONG>(clipTop*scale),static_cast<LONG>((left+imageW)*scale),static_cast<LONG>(clipBottom*scale)};props.fVisible=TRUE;props.opacity=previewEnterAt?0:255;props.fSourceClientAreaOnly=FALSE;
        if(FAILED(DwmUpdateThumbnailProperties(thumbnail,&props))){DwmUnregisterThumbnail(thumbnail);continue;}
        taskThumbnails.push_back(thumbnail);previewThumbnailProps.emplace(thumbnail,props);taskPreviewBytes+=bytes;
    }
}
struct NativePreview : NativePopup {
    NativeShell &shell;NativeBar *owner=nullptr;int item=-1;std::wstring itemId,windowStamp;
    explicit NativePreview(NativeShell &shell):NativePopup(shell.app),shell(shell){}
    ~NativePreview(){NativePopup::FinishHide();}
    bool ContainsPoint(POINT point){
        if(!visible||dismissing||!hwnd)return false;RECT rect{};GetWindowRect(hwnd,&rect);if(PtInRect(&rect,point))return true;
        if(!owner)return false;auto found=std::find_if(owner->rows.begin(),owner->rows.end(),[&](auto const &row){return row.id==itemId;});if(found==owner->rows.end())return false;
        float scale=owner->dpi/96;auto r=found->rect;RECT anchor{owner->full.left+static_cast<LONG>(r.left*scale),owner->full.top+static_cast<LONG>(r.top*scale),owner->full.left+static_cast<LONG>(r.right*scale),owner->full.top+static_cast<LONG>(r.bottom*scale)};
        if(PtInRect(&anchor,point))return true;
        return NativePreviewMotion::Bridge(point,anchor,rect,Str(owner->settings,L"position"));
    }
    bool ContainsCursor(){POINT point{};return GetCursorPos(&point)&&ContainsPoint(point);}
    void Hide(bool notify=true,bool restore=false)override {
        auto prior=owner;owner=nullptr;NativePopup::Hide(false,restore);if(prior){prior->menuOpen=false;prior->ScheduleHide();}(void)notify;
    }
    static std::wstring WindowsStamp(JsonArray const &windows){std::wstring stamp;for(auto const &v:windows){auto row=v.GetObject();stamp+=std::to_wstring(static_cast<long long>(Num(row,L"hwnd")))+L":"+Str(row,L"title")+L"\n";}return stamp;}
    static std::wstring OwnerStamp(NativeBar const &bar,int index){auto r=bar.rows[index].rect;return std::to_wstring(bar.dpi)+L":"+std::to_wstring(bar.full.left)+L":"+std::to_wstring(bar.full.top)+L":"+std::to_wstring(bar.full.right)+L":"+std::to_wstring(bar.full.bottom)+L":"+std::to_wstring(r.left)+L":"+std::to_wstring(r.top)+L":"+std::to_wstring(r.right)+L":"+std::to_wstring(r.bottom)+L":"+std::to_wstring(bar.dark);}
    void Show(NativeBar &bar,int index,bool animate=true) {
        if(index<0||index>=static_cast<int>(bar.entries.size()))return;
        auto entry=bar.entries[index];auto windows=entry.GetNamedArray(L"windows",JsonArray{});if(!windows.Size())return;
        auto previous=owner;owner=nullptr;NativePopup::FinishHide();if(previous&&previous!=&bar){previous->menuOpen=false;previous->ScheduleHide();}owner=&bar;item=index;itemId=bar.rows[index].id;windowStamp=WindowsStamp(windows)+OwnerStamp(bar,index);bar.HideBarTooltip();bar.menuOpen=true;previewMotion=NativePreviewMotion::Read(bar.settings);
        auto r=bar.rows[index].rect;auto side=Str(bar.settings,L"position");float scale=bar.dpi/96;int margin=static_cast<int>(Num(bar.settings,L"margin")*scale);
        int x=bar.full.left+static_cast<int>((r.left+r.right)*scale/2),y=bar.full.top+static_cast<int>((r.top+r.bottom)*scale/2);
        if(side==L"Left")x=bar.full.right+margin;else if(side==L"Right")x=bar.full.left-margin;else if(side==L"Top")y=bar.full.bottom+margin;else y=bar.full.top-margin;
        JsonObject packet,payload,position,data;Put(packet,L"kind",std::wstring(L"@magic/dock-preview"));Put(packet,L"token",std::wstring(L"local-dock-preview"));Put(position,L"x",x);Put(position,L"y",y);payload.Insert(L"desiredPosition",position);
        Put(payload,L"previewAnchorX",bar.full.left+(r.left+r.right)*scale/2);Put(payload,L"previewAnchorY",bar.full.top+(r.top+r.bottom)*scale/2);
        Put(payload,L"alignX",side==L"Left"?std::wstring(L"Start"):side==L"Right"?std::wstring(L"End"):std::wstring(L"Center"));Put(payload,L"alignY",side==L"Top"?std::wstring(L"Start"):side==L"Bottom"?std::wstring(L"End"):std::wstring(L"Center"));
        previewSide=side;data.Insert(L"previewAnimate",JsonValue::CreateBooleanValue(animate));data.Insert(L"rows",JsonArray{});data.Insert(L"windows",windows);data.Insert(L"dark",JsonValue::CreateBooleanValue(bar.dark));Put(data,L"language",Str(bar.data,L"language"));packet.Insert(L"payload",payload);packet.Insert(L"data",data);packet.Insert(L"account",JsonObject{});
        NativePopup::Show(packet);owner=&bar;bar.menuOpen=true;
    }
    void Activate(int index)override {
        if(!owner||index<0||index>=static_cast<int>(rows.size()))return;
        auto row=rows[index];auto bar=owner;auto found=std::find_if(bar->rows.begin(),bar->rows.end(),[&](auto const &r){return r.id==itemId;});if(found==bar->rows.end()){Hide();return;}
        int target=static_cast<int>(found-bar->rows.begin());auto handle=_wcstoi64(row.handle.c_str(),nullptr,10);
        bool close=row.action==L"close";if(!close)Hide(false);bar->Send(target,close?L"close-one":L"focus",handle);
    }
    void MiddleActivate(int index)override {if(index<0||index>=static_cast<int>(rows.size()))return;auto action=rows[index].action;rows[index].action=L"close";Activate(index);if(index<static_cast<int>(rows.size()))rows[index].action=action;}
    void Refresh(){if(!owner||!visible)return;auto bar=owner;auto found=std::find_if(bar->rows.begin(),bar->rows.end(),[&](auto const &r){return r.id==itemId;});if(found==bar->rows.end()){Hide();return;}
        auto index=static_cast<int>(found-bar->rows.begin());auto windows=bar->entries[index].GetNamedArray(L"windows",JsonArray{});if(!windows.Size()){Hide();return;}auto stamp=WindowsStamp(windows)+OwnerStamp(*bar,index);if(stamp!=windowStamp)Show(*bar,index,false);
    }
};
