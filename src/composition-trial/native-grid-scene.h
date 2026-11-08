#pragma once
// Finite in-place editor scene. One bounded panel raster; desktop pixels are reused.
inline void NativeBar::SetGridSuppressed(bool value){
    if(!dock||gridSuppressed==value)return;
    if(value){gridWasEdge=edge;gridSuppressed=true;shell.HidePreview(this);Hide(false);if(hwnd){KillTimer(hwnd,92);KillTimer(hwnd,93);KillTimer(hwnd,105);}}
    else{gridSuppressed=false;if(!visible||app.stopping)return;if(!gridWasEdge||Str(settings,L"hideMode")==L"Never"){edge=true;Reveal();}else Hide(false);}
}
inline void Window::SyncGridDock(bool suppress){
    if(!app.shell)return;
    for(auto const &[id,bar]:app.shell->bars){if(!bar->dock)continue;auto board=bar->PageBoard();if(!board||board->id!=boardId)continue;
        if(suppress&&!gridDockLayer&&pageLayer){
            bar->ClearDockHover();bar->ResetDockBodyMotion();auto c=app.graphics.compositor;gridDockLayer=c.CreateContainerVisual();pageLayer.Children().InsertAtTop(gridDockLayer);
            auto b=app.model.Find(boardId);gridDockLayer.Offset({static_cast<float>(bar->full.left-(b?b->x:0)),static_cast<float>(bar->full.top-(b?b->y:0)),0});gridDockLayer.Size({bar->width*bar->dpi/96,bar->height*bar->dpi/96});
            auto copy=[&](SpriteVisual const &source,float3 offset){if(!source||!source.Brush())return;auto v=c.CreateSpriteVisual();v.Size(source.Size());v.Offset(offset+source.Offset());v.Brush(source.Brush());gridDockLayer.Children().InsertAtTop(v);};
            size_t bytes=bar->surfaceBytes;for(auto const &[key,item]:bar->toolbarVisuals)bytes+=item.bytes;
            if(bytes<=8*1024*1024&&estimatedSurfaceBytes+bytes<=SurfaceLimit){
                copy(bar->materialVisual,{});copy(bar->visual,{});for(auto const &[key,item]:bar->toolbarVisuals){auto offset=item.group?item.group.Offset():float3{};copy(item.visual,offset);copy(item.indicator,offset);}gridDockBytes=bytes;
            }
            if(!gridDockLayer.Children().Count()){NativeMorphFrame placeholder;Preview70Theme theme{Preview70Theme::systemDark};auto size=gridDockLayer.Size();placeholder.Create(app.graphics,gridDockLayer,{size.x,size.y},{size.x,size.y},12*dpi/96,theme.Material(NativeMaterial::Dock,false),theme.dark,nullptr,dpi/96);}
            // Geometry stays tied to the actual Dock body, never its enlarged hover HWND.
        }
        bar->SetGridSuppressed(suppress);
    }
}
inline void Window::ReleaseGridScene(bool restoreDock){
    if(hwnd)KillTimer(hwnd,109);if(gridLayer&&gridLayer.Parent())gridLayer.Parent().Children().Remove(gridLayer);
    gridLayer=nullptr;gridVisual=nullptr;gridSurface=nullptr;gridBackdrop=nullptr;gridSurfaceBytes=0;gridSurfaceKey.clear();gridSceneReady=false;
    if(gridDockLayer&&gridDockLayer.Parent())gridDockLayer.Parent().Children().Remove(gridDockLayer);gridDockLayer=nullptr;gridDockBytes=0;
    if(pageLayer){pageLayer.StopAnimation(L"Scale");pageLayer.Scale(GridEditing()&&!gridClosing?float3{.9f,.9f,1}:float3{1,1,1});}
    if(restoreDock)SyncGridDock(false);gridClosing=gridBarCollapsing=false;estimatedSurfaceBytes=static_cast<size_t>(rootPixelWidth)*rootPixelHeight*4+(panelSurface?static_cast<size_t>(panelPixelWidth)*panelPixelHeight*4:0)+outgoingPageBytes;
}
inline void Window::FinishGridMotion(){
    if(hwnd)KillTimer(hwnd,109);if(gridClosing){ReleaseGridScene();return;}
    if(gridBarCollapsing){
        gridBarCollapsing=false;gridBarHidden=true;focusKey=L"grid:toggle";gridSurfaceKey.clear();desktopStamp.valid=false;
        if(gridLayer){gridLayer.StopAnimation(L"Scale");gridLayer.StopAnimation(L"Opacity");gridLayer.Scale({1,1,1});gridLayer.Opacity(1);}Draw();
    }
    if(pageLayer){pageLayer.StopAnimation(L"Scale");pageLayer.Scale(GridEditing()?float3{.9f,.9f,1}:float3{1,1,1});}
    if(gridLayer){gridLayer.StopAnimation(L"Scale");gridLayer.StopAnimation(L"Opacity");gridLayer.StopAnimation(L"Offset");gridLayer.Scale({1,1,1});gridLayer.Opacity(1);}
}
inline void Window::AnimateGrid(bool close){
    gridClosing=close;if(!pageLayer||!gridLayer||!NativeMotion(app.model)||!hwnd){FinishGridMotion();return;}
    if(hwnd)KillTimer(hwnd,109);auto c=app.graphics.compositor;
    pageLayer.CenterPoint({width*dpi/192,height*dpi/192,0});NativeInteractionMotion::Vector(c,pageLayer,L"Scale",close?float3{1,1,1}:float3{.9f,.9f,1},NativeContextMotion::RootMs);
    if(close)NativeContextMotion::Exit(c,gridLayer,false);
    else{gridLayer.CenterPoint({gridLayer.Size().x/2,gridLayer.Size().y,0});gridLayer.Scale({.78f,.78f,1});gridLayer.Opacity(0);NativeContextMotion::Enter(c,gridLayer,false);}
    if(!SetTimer(hwnd,109,NativeContextMotion::RootMs+16,nullptr))FinishGridMotion();
}
inline void Window::SyncGridScene(){
    if(gridClosing||gridBarCollapsing)return;
    if(!GridEditing()){if(gridSceneReady||gridLayer)ReleaseGridScene();return;}
    if(!root||!pageLayer||!hwnd)return;
    bool fresh=!gridLayer;auto &g=app.graphics;auto c=g.compositor;gridPanel=GridPanelBounds();float scale=dpi/96,w=gridPanel.right-gridPanel.left,h=gridPanel.bottom-gridPanel.top;
    if(fresh){gridLayer=c.CreateContainerVisual();root.Children().InsertAtTop(gridLayer);gridVisual=c.CreateSpriteVisual();gridLayer.Children().InsertAtTop(gridVisual);SyncGridDock(true);}
    gridLayer.Offset({gridPanel.left*scale,gridPanel.top*scale,0});gridLayer.Size({w*scale,h*scale});gridVisual.Size(gridLayer.Size());
    float blur=26;for(;blur>=6;blur-=2)if((w+6*blur)*(h+6*blur)*scale*scale*4<=8*1024*1024)break;
    bool glass=SyncNativeGlass(g,hwnd,gridLayer,gridBackdrop,{0,0,w,h},scale,16,NativeMaterial::Panel,blur>=6&&!NativeLowPower(app.model)&&!sessionPaused,gridVisual,blur);
    auto key=(gridEdit?std::wstring(gridEdit.Stringify()):L"loading")+gridError+focusKey+std::to_wstring(width)+L":"+std::to_wstring(height)+L":"+std::to_wstring(dpi)+L":"+std::to_wstring(gridBarHidden)+L":"+std::to_wstring(gridBusy)+L":"+std::to_wstring(glass)+L":"+std::to_wstring(Preview70Theme::revision);
    if(key!=gridSurfaceKey){
        size_t base=static_cast<size_t>(rootPixelWidth)*rootPixelHeight*4+(panelSurface?static_cast<size_t>(panelPixelWidth)*panelPixelHeight*4:0)+outgoingPageBytes+gridDockBytes;
        size_t budget=base<SurfaceLimit?std::min<size_t>(8*1024*1024,SurfaceLimit-base):0;if(budget<4096){ReleaseGridScene();return;}
        float raster=std::min(scale,std::sqrt((budget-2048)/std::max(1.f,w*h*4)));int pw=std::max(1,static_cast<int>(std::floor(w*raster))),ph=std::max(1,static_cast<int>(std::floor(h*raster)));size_t bytes=static_cast<size_t>(pw)*ph*4;
        if(!gridSurface||gridSurfaceBytes!=bytes){gridSurface=g.composition.CreateDrawingSurface({static_cast<float>(pw),static_cast<float>(ph)},Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);auto brush=c.CreateSurfaceBrush(gridSurface);brush.Stretch(CompositionStretch::Fill);gridVisual.Brush(brush);gridSurfaceBytes=bytes;}
        auto interop=gridSurface.as<abiComposition::ICompositionDrawingSurfaceInterop>();com_ptr<ID2D1DeviceContext> dc;POINT offset{};check_hresult(interop->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),dc.put_void(),&offset));bool ended=false;
        try{dc->SetDpi(raster*96,raster*96);dc->SetTransform(D2D1::Matrix3x2F::Translation(offset.x/raster-gridPanel.left,offset.y/raster-gridPanel.top));dc->Clear(D2D1::ColorF(0,0));PaintGridEditor(dc.get());auto result=interop->EndDraw();ended=true;check_hresult(result);}catch(...){if(!ended)interop->EndDraw();throw;}
        gridSurfaceKey=key;estimatedSurfaceBytes=base+gridSurfaceBytes;
    }
    gridSceneReady=true;if(fresh)AnimateGrid(false);
}
