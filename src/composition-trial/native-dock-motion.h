#pragma once
// Logical slot geometry never changes with hover. The HWND gets bounded inward
// room while `full` remains the original Dock body used by anchoring/hide rules.
inline void NativeBar::PositionBar(UINT flags)
{
    RECT host=full;if(dockResizeAt){host.left=std::min(host.left,dockEnvelope.left);host.top=std::min(host.top,dockEnvelope.top);host.right=std::max(host.right,dockEnvelope.right);host.bottom=std::max(host.bottom,dockEnvelope.bottom);}
    contentX=contentY=0;
    if(dock){
        float s=dpi/96;LONG lift=static_cast<LONG>(std::ceil((iconSize*NativeInteractionMotion::DockGain+NativeInteractionMotion::DockLift*NativeInteractionMotion::DockGain+6)*s));
        LONG axial=static_cast<LONG>(std::ceil((iconSize*NativeInteractionMotion::DockGain/2+6)*s));if(dockEnterAt||dockEnterPending||dockResizeAt)axial=std::max(axial,static_cast<LONG>(std::ceil((Vertical()?std::max(height,dockFromHeight):std::max(width,dockFromWidth))*.07f*s)));auto side=Str(settings,L"position");
        if(Vertical()){host.top-=axial;host.bottom+=axial;if(side==L"Left")host.right+=lift;else host.left-=lift;}
        else{host.left-=axial;host.right+=axial;if(side==L"Top")host.bottom+=lift;else host.top-=lift;}
        MONITORINFO m{sizeof(m)};if(GetMonitorInfoW(MonitorFromRect(&full,MONITOR_DEFAULTTONEAREST),&m)){
            host.left=std::max(host.left,m.rcMonitor.left);host.top=std::max(host.top,m.rcMonitor.top);
            host.right=std::min(host.right,m.rcMonitor.right);host.bottom=std::min(host.bottom,m.rcMonitor.bottom);
        }
        contentX=(full.left-host.left)/s;contentY=(full.top-host.top)/s;
    }
    if(barHost){barHost.Offset({contentX*dpi/96,contentY*dpi/96,0});barHost.Size({width*dpi/96,height*dpi/96});}
    NativeLayerPosition(hwnd,host.left,host.top,host.right-host.left,host.bottom-host.top,flags);
    UpdateDockRegion();
}
inline NativeInteractionMotion::DockPose NativeBar::DockPose(int i,bool maximum)const
{
    auto r=rows[i].rect;float center=Vertical()?(r.top+r.bottom)/2:(r.left+r.right)/2;
    bool eligible=dock&&!edge&&!barDrag&&!dockReorderHoverBlocked&&rows[i].control.empty()&&!rows[i].offscreen;
    bool active=eligible&&(maximum||pointerKind==PT_MOUSE&&!menuOpen&&dockMotionEnabled&&dockHoverAxis!=-FLT_MAX);
    return NativeInteractionMotion::Dock(r.right-r.left,r.bottom-r.top,maximum?center:dockHoverAxis,center,iconSize,Str(settings,L"position"),active);
}
inline D2D1_RECT_F NativeBar::VisualRowBounds(int i)const
{
    return dock?NativeInteractionMotion::Bounds(rows[i].rect,DockPose(i)):rows[i].rect;
}
inline int NativeBar::Hit(float x,float y)const
{
    x-=contentX;y-=contentY;
    if(dockEnterAt){float t=(GetTickCount64()-dockEnterAt)/static_cast<float>(NativeInteractionMotion::DockBodyMs),k=NativeInteractionMotion::DockSpring(t);if(k<.02f)return -1;auto side=Str(settings,L"position");float ox=Vertical()?(side==L"Left"?0.f:width):width/2,oy=Vertical()?height/2:side==L"Top"?0.f:height;float dx=Vertical()?(side==L"Left"?-10.f:10.f)*(1-k):0,dy=Vertical()?0:(side==L"Top"?-10.f:10.f)*(1-k);x=ox+(x-ox-dx)/k;y=oy+(y-oy-dy)/k;}
    int best=-1;float distance=FLT_MAX;
    for(int i=static_cast<int>(rows.size())-1;i>=0;--i){auto const &r=rows[i];
        if(r.control==L"drag-ghost"||r.control==L"drag-gap"||r.offscreen)continue;
        if(!Inside(VisualRowBounds(i),x,y)&&!Inside(r.rect,x,y))continue;
        if(!dock)return i;float d=std::abs((Vertical()?y:x)-(Vertical()?(r.rect.top+r.rect.bottom)/2:(r.rect.left+r.rect.right)/2));
        if(d<distance){distance=d;best=i;}
    }return best;
}
inline void NativeBar::DockPointer(float x,float y)
{
    if(!dock||barDrag||dockReorderHoverBlocked||pointerKind!=PT_MOUSE)return;
    float axis=Vertical()?y-contentY:x-contentX;
    if(dockHoverAxis!=-FLT_MAX&&std::abs(axis-dockHoverAxis)<.5f)return;
    dockHoverAxis=axis;SyncDockHover();
}
inline void NativeBar::ClearDockHover()
{
    if(!dock)return;dockHoverAxis=-FLT_MAX;SyncDockHover();
}
inline void NativeBar::LockDockReorderHover()
{
    if(!dock)return;dockReorderHoverBlocked=true;ClearDockHover();
}
inline void NativeBar::DockPointerLeft(bool outside)
{
    // Capture/region changes can emit WM_MOUSELEAVE while the pointer remains
    // over the Dock. Only a real departure after the grab can rearm magnifying.
    if(dock&&!barDrag&&outside)dockReorderHoverBlocked=false;
    ClearDockHover();
}
inline void NativeBar::SyncDockHover()
{
    if(!dock||!toolbarLayer)return;bool motion=NativeMotion(app.model);dockMotionEnabled=motion;ULONGLONG now=GetTickCount64(),next=MAXULONGLONG;
    int nearest=-1;float distance=FLT_MAX;
    for(int i=0;i<static_cast<int>(rows.size());++i){auto found=toolbarVisuals.find(rows[i].id);if(found==toolbarVisuals.end()||!found->second.hoverLayer)continue;
        auto &v=found->second;auto pose=DockPose(i);float s=dpi/96;
        if(!v.mediaGeometry)v.hoverLayer.CenterPoint({(8+pose.origin.x)*s,(8+pose.origin.y)*s,0});
        float3 offset{pose.offset.x*s,pose.offset.y*s,0};
        bool following=motion&&pose.scale>1;
        if(!motion||(following&&!v.mediaResizeAt)||dockReorderHoverBlocked){
            // Pointer tracking is a position, not a new 150 ms transition on
            // every input packet. Publish the latest pose once per input; the
            // compositor presents it at the display's own refresh cadence.
            v.hoverLayer.StopAnimation(L"Scale");v.hoverLayer.StopAnimation(L"Offset");
            v.hoverLayer.Scale({pose.scale,pose.scale,1});v.hoverLayer.Offset(offset);v.hoverRetireAt=0;
        }
        else if(std::abs(pose.scale-v.hoverScale)>.0001f||std::abs(offset.x-v.hoverOffset.x)>.01f||std::abs(offset.y-v.hoverOffset.y)>.01f){
            NativeInteractionMotion::Vector(app.graphics.compositor,v.hoverLayer,L"Scale",{pose.scale,pose.scale,1},NativeInteractionMotion::DockMs,true);
            NativeInteractionMotion::Vector(app.graphics.compositor,v.hoverLayer,L"Offset",offset,NativeInteractionMotion::DockMs,true);
            if(pose.scale<=1&&v.hoverScale>1)v.hoverRetireAt=now+NativeInteractionMotion::DockMs+16;
        }
        v.hoverScale=pose.scale;v.hoverOffset=offset;
        if(pose.scale>1){v.hoverRetireAt=0;float d=std::abs(dockHoverAxis-(Vertical()?(rows[i].rect.top+rows[i].rect.bottom)/2:(rows[i].rect.left+rows[i].rect.right)/2));if(d<distance){distance=d;nearest=i;}}
        if(v.hoverRetireAt&&v.hoverRetireAt<=now){v.hoverLayer.StopAnimation(L"Scale");v.hoverLayer.StopAnimation(L"Offset");v.hoverLayer.Scale({1,1,1});v.hoverLayer.Offset({0,0,0});v.hoverRetireAt=0;}
        if(v.hoverRetireAt)next=std::min(next,v.hoverRetireAt);
    }
    if(nearest>=0){auto const &id=rows[nearest].id;if(dockHoverTop!=id){auto &v=toolbarVisuals.at(id);toolbarLayer.Children().Remove(v.group);toolbarLayer.Children().InsertAtTop(v.group);dockHoverTop=id;}}else dockHoverTop.clear();
    if(hwnd){if(next==MAXULONGLONG)KillTimer(hwnd,103);else SetTimer(hwnd,103,static_cast<UINT>(std::max<ULONGLONG>(1,next-now)),nullptr);}
    UpdateDockRegion();
}
inline void NativeBar::UpdateDockRegion(bool force)
{
    if(!dock||!hwnd||edge)return;std::set<std::wstring> active;
    for(auto const &[id,v]:toolbarVisuals)if(v.hoverScale>1||v.hoverRetireAt)active.insert(id);
    float s=dpi/96;int x=static_cast<int>(std::lround(contentX*s)),y=static_cast<int>(std::lround(contentY*s));
    int w=static_cast<int>(std::ceil(width*s)),h=static_cast<int>(std::ceil(height*s));RECT body{x,y,x+w+1,y+h+1};
    if(!force&&active==dockRegionItems&&dockRegionDpi==dpi&&memcmp(&body,&dockRegionBody,sizeof(body))==0)return;
    dockRegionItems=std::move(active);dockRegionBody=body;dockRegionDpi=dpi;
    if(dockEnterAt||dockEnterPending||dockResizeAt){RECT host{};GetClientRect(hwnd,&host);auto envelope=CreateRectRgn(0,0,host.right,host.bottom);if(!SetWindowRgn(hwnd,envelope,FALSE))DeleteObject(envelope);return;}
    auto region=CreateRoundRectRgn(x,y,x+w+1,y+h+1,static_cast<int>(32*s),static_cast<int>(32*s));
    // Retain each affected icon's maximum halo until its finite return completes.
    // Region updates occur only when the affected set changes, not every frame.
    for(int i=0;i<static_cast<int>(rows.size());++i)if(dockRegionItems.contains(rows[i].id)){
        auto r=NativeInteractionMotion::Bounds(rows[i].rect,DockPose(i,true));
        auto extra=CreateRectRgn(x+static_cast<int>(std::floor((r.left-3)*s)),y+static_cast<int>(std::floor((r.top-3)*s)),x+static_cast<int>(std::ceil((r.right+3)*s)),y+static_cast<int>(std::ceil((r.bottom+3)*s)));
        CombineRgn(region,region,extra,RGN_OR);DeleteObject(extra);
    }
    if(!SetWindowRgn(hwnd,region,FALSE))DeleteObject(region);
}
