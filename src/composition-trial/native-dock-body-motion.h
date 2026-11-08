#pragma once
// Native counterpart of dock-enter.html. The demo's click-to-add/remove and
// hover pop are intentionally absent; only actual model deltas trigger reflow.
inline void NativeBar::ResetDockBodyMotion(){
    if(!dock)return;if(hwnd)KillTimer(hwnd,DockBodyTimer);dockEnterAt=dockResizeAt=0;dockBodyPending=false;dockShiftX=dockShiftY=0;
    if(toolbarRoot){toolbarRoot.StopAnimation(L"Offset");toolbarRoot.Offset({0,0,0});toolbarRoot.StopAnimation(L"Scale");toolbarRoot.Scale({1,1,1});toolbarRoot.StopAnimation(L"Opacity");toolbarRoot.Opacity(1);}
    for(auto const &v:{visual,materialVisual})if(v){v.StopAnimation(L"Scale");v.Scale({1,1,1});}
    if(dockViewportClip)for(auto property:Vertical()?std::array{L"TopInset",L"BottomInset"}:std::array{L"LeftInset",L"RightInset"}){dockViewportClip.StopAnimation(property);if(property==std::wstring(L"TopInset"))dockViewportClip.TopInset(0);else if(property==std::wstring(L"BottomInset"))dockViewportClip.BottomInset(0);else if(property==std::wstring(L"LeftInset"))dockViewportClip.LeftInset(0);else dockViewportClip.RightInset(0);}
    for(auto &[_,v]:toolbarVisuals)if(v.mediaGeometry){v.mediaGeometry.StopAnimation(L"Size");v.mediaGeometry.Size(v.mediaSize);v.mediaResizeAt=0;v.visual.StopAnimation(L"Opacity");v.visual.Opacity(1);if(v.mediaClip){v.mediaClip.StopAnimation(L"Opacity");v.mediaClip.Opacity(1);}}
    if(dock&&hwnd&&!edge)UpdateDockRegion(true);
}
inline void NativeBar::StartDockEntrance(){
    dockHasShown=true;if(!dock||!toolbarRoot||!NativeMotion(app.model))return;ResetDockBodyMotion();dockEnterAt=GetTickCount64();PositionBar(SWP_NOACTIVATE);UpdateDockRegion(true);
    auto side=Str(settings,L"position");float s=dpi/96;
    toolbarRoot.CenterPoint({(Vertical()?(side==L"Left"?0.f:width):width/2)*s,(Vertical()?height/2:side==L"Top"?0.f:height)*s,0});
    auto scale=NativeInteractionMotion::DockSpringVector(app.graphics.compositor,{0,0,1},{1,1,1});toolbarRoot.StartAnimation(L"Scale",scale);
    float3 from=Vertical()?float3{(side==L"Left"?-10.f:10.f)*s,0,0}:float3{0,(side==L"Top"?-10.f:10.f)*s,0};
    auto offset=NativeInteractionMotion::DockSpringVector(app.graphics.compositor,from,{});toolbarRoot.StartAnimation(L"Offset",offset);
    auto opacity=app.graphics.compositor.CreateScalarKeyFrameAnimation();opacity.InsertKeyFrame(0,0);opacity.InsertKeyFrame(1,1,NativeInteractionMotion::Ease(app.graphics.compositor));opacity.Duration(std::chrono::milliseconds(230));toolbarRoot.StartAnimation(L"Opacity",opacity);
    if(!SetTimer(hwnd,DockBodyTimer,NativeInteractionMotion::DockBodyMs+16,nullptr)){toolbarRoot.StopAnimation(L"Offset");toolbarRoot.Offset({0,0,0});ResetDockBodyMotion();PositionBar(SWP_NOACTIVATE);}
}
inline void NativeBar::PrepareDockReflow(float previousWidth,float previousHeight,RECT const &previous){
    if(!NativeMotion(app.model)||!dockHasShown||barDrag)return;
    auto now=GetTickCount64();float k=dockResizeAt?NativeInteractionMotion::DockSpring((now-dockResizeAt)/static_cast<float>(NativeInteractionMotion::DockBodyMs)):1;
    dockFromWidth=dockResizeAt?dockFromWidth+(previousWidth-dockFromWidth)*k:previousWidth;
    dockFromHeight=dockResizeAt?dockFromHeight+(previousHeight-dockFromHeight)*k:previousHeight;
    dockShiftX=(previous.left-full.left)*1.f;dockShiftY=(previous.top-full.top)*1.f;
    RECT envelope{std::min(previous.left,full.left),std::min(previous.top,full.top),std::max(previous.right,full.right),std::max(previous.bottom,full.bottom)};
    if(dockResizeAt){envelope.left=std::min(envelope.left,dockEnvelope.left);envelope.top=std::min(envelope.top,dockEnvelope.top);envelope.right=std::max(envelope.right,dockEnvelope.right);envelope.bottom=std::max(envelope.bottom,dockEnvelope.bottom);}
    dockEnvelope=envelope;dockResizeAt=now;dockBodyPending=true;
    if(!SetTimer(hwnd,DockBodyTimer,NativeInteractionMotion::DockBodyMs+16,nullptr))ResetDockBodyMotion();
}
inline void NativeBar::ApplyDockReflow(){
    if(!dock||!dockBodyPending)return;dockBodyPending=false;
    for(auto const &v:{visual,materialVisual})if(v){v.CenterPoint({width*dpi/192,height*dpi/192,0});auto a=NativeInteractionMotion::DockSpringVector(app.graphics.compositor,{dockFromWidth/std::max(1.f,width),dockFromHeight/std::max(1.f,height),1},{1,1,1});v.StartAnimation(L"Scale",a);}
    // The viewport must follow the moving body rather than snap to the final
    // smaller width. Keep this clip across input/provider redraws until cleanup.
    if(dockViewportClip){auto &c=app.graphics.compositor;auto a=c.CreateScalarKeyFrameAnimation();float from=((Vertical()?height:width)-(Vertical()?dockFromHeight:dockFromWidth))*dpi/192;
        for(int i=0;i<=40;++i){float p=i/40.f;a.InsertKeyFrame(p,from*(1-NativeInteractionMotion::DockSpring(p)),c.CreateLinearEasingFunction());}a.Duration(std::chrono::milliseconds(NativeInteractionMotion::DockBodyMs));
        dockViewportClip.StartAnimation(Vertical()?L"TopInset":L"LeftInset",a);dockViewportClip.StartAnimation(Vertical()?L"BottomInset":L"RightInset",a);
    }
}
