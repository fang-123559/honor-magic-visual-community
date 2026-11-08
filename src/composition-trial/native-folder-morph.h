#pragma once
// One finite compositor timeline for shape + individual icon positions/scales.
struct NativeFolderMorph {
    // AOSP Launcher3 uses 200 ms for folder open/close. Keep one timeline for
    // frame, icons and scrim so the shorter interaction never splits into layers.
    static constexpr int Duration=200;
    struct IconMotion {SpriteVisual visual{nullptr};CompositionDrawingSurface pixels{nullptr};float3 closed{},opened{},compactScale{1,1,1},expandedScale{1,1,1};bool mapped=false;std::wstring key;float side=0;};
    NativeMorphFrame frame;std::vector<IconMotion> icons;size_t bytes=0;D2D1_RECT_F cover{},opened{};bool glass=false;
    void Release(Window &w){frame.Release();for(auto &item:icons)if(item.visual&&item.visual.Parent())item.visual.Parent().Children().Remove(item.visual);icons.clear();bytes=0;if(w.folderMotion)w.folderMotion.Clip(nullptr);if(w.panel){w.panel.StopAnimation(L"Opacity");w.panel.Opacity(1);}if(w.folderBackdrop)w.folderBackdrop.IsVisible(true);}
    static void Paint(Window &w,IconMotion &item,Tile const &tile){auto interop=item.pixels.as<abiComposition::ICompositionDrawingSurfaceInterop>();com_ptr<ID2D1DeviceContext> dc;POINT offset{};check_hresult(interop->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),dc.put_void(),&offset));bool ended=false;float scale=w.dpi/96;
        try{dc->SetDpi(w.dpi,w.dpi);dc->SetTransform(D2D1::Matrix3x2F::Translation(offset.x/scale,offset.y/scale));dc->Clear(D2D1::ColorF(0,0));w.PaintIcon(dc.get(),tile,{0,0,item.side,item.side});auto hr=interop->EndDraw();ended=true;check_hresult(hr);}catch(...){if(!ended)interop->EndDraw();throw;}}
    void RefreshIcon(Window &w,std::wstring const &key){auto const &tiles=w.Tiles();for(auto &item:icons)if(item.key==key){auto tile=std::find_if(tiles.begin(),tiles.end(),[&](auto const &v){return v.key==key;});if(tile!=tiles.end())Paint(w,item,*tile);return;}}
    bool Prepare(Window &w,bool close){
        auto board=w.app.model.Find(w.boardId);if(!board)return false;auto folder=std::find_if(board->tiles.begin(),board->tiles.end(),[&](auto const &t){return t.folder==w.folderId;});if(folder==board->tiles.end())return false;
        cover=w.RootBounds(folder->key);opened=w.panelRect;if(cover.right<=cover.left||cover.bottom<=cover.top)return false;
        auto layout=w.CoverLayout(*folder,cover);int shown=layout.VisibleCount(static_cast<int>(folder->items.size()));auto const &tiles=w.Tiles();float scale=w.dpi/96;auto &g=w.app.graphics;
        auto visible=FolderVisibleRange(static_cast<int>(tiles.size()),w.columns,w.scrollRow,w.contentRect.bottom-w.contentRect.top,FolderRowStep(w.contentRect,w.columns));
        try{
            for(int i=0;i<static_cast<int>(tiles.size());i++){
                bool mapped=i<shown;if(!mapped&&(i<visible.first||i>=visible.second))continue;
                auto cell=FolderCellBounds(w.contentRect,w.columns,w.scrollRow,i);float cx=(cell.left+cell.right)/2;
                D2D1_RECT_F target{cx-22,cell.top+12,cx+22,cell.top+56},source=target;
                if(mapped){source=i<layout.Capacity()-1?layout.Cell(i):layout.Mini(i-(layout.Capacity()-1));
                    if(HonorScheme(w.app.model.preferences)!=L"honor-white"||HonorDesktopFile(tiles[i],L"honor-white",w.app.model.recycleFull).empty()){float pad=(source.right-source.left)*.14f;source={source.left+pad,source.top+pad,source.right-pad,source.bottom-pad};}}
                float side=std::max(source.right-source.left,44.f);int pixels=static_cast<int>(std::ceil(side*scale));size_t cost=static_cast<size_t>(pixels)*pixels*4;
                if(icons.size()>=128||bytes+cost>8*1024*1024||w.estimatedSurfaceBytes+bytes+cost>SurfaceLimit){Release(w);return false;}
                IconMotion item;item.mapped=mapped;item.pixels=g.composition.CreateDrawingSurface({static_cast<float>(pixels),static_cast<float>(pixels)},Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
                item.key=tiles[i].key;item.side=side;Paint(w,item,tiles[i]);
                item.visual=g.compositor.CreateSpriteVisual();item.visual.Size({static_cast<float>(pixels),static_cast<float>(pixels)});item.visual.Brush(g.compositor.CreateSurfaceBrush(item.pixels));
                item.closed=mapped?float3{(source.left-cover.left)*scale,(source.top-cover.top)*scale,0}:float3{(target.left-opened.left)*scale,(target.top-opened.top)*scale,0};
                item.opened={(target.left-opened.left)*scale,(target.top-opened.top)*scale,0};float compactScale=(source.right-source.left)*scale/pixels,expandedScale=44*scale/pixels;item.compactScale={compactScale,compactScale,1};item.expandedScale={expandedScale,expandedScale,1};
                item.visual.Offset(close?item.opened:item.closed);item.visual.Scale(close?item.expandedScale:item.compactScale);item.visual.Opacity(close||mapped?1.f:0.f);icons.push_back(std::move(item));bytes+=cost;
            }
            auto size=[&](D2D1_RECT_F rect){return float2{(rect.right-rect.left)*scale,(rect.bottom-rect.top)*scale};};Preview70Theme theme{Preview70Theme::systemDark};glass=static_cast<bool>(w.folderBackdrop);
            frame.Create(g,w.folderMotion,{std::max(size(cover).x,size(opened).x),std::max(size(cover).y,size(opened).y)},size(close?opened:cover),(close?NativeMetrics::FloatingRadius:NativeMetrics::PanelRadius)*scale,theme.Material(close?NativeMaterial::Panel:NativeMaterial::Card,glass),theme.dark,w.folderBackdrop?w.folderBackdrop.Brush():CompositionBrush{nullptr},scale);
            w.folderMotion.Children().Remove(frame.root);w.folderMotion.Children().InsertBelow(frame.root,w.panel);
            for(auto &item:icons)w.folderMotion.Children().InsertAtTop(item.visual);
            w.folderMotion.Clip(g.compositor.CreateGeometricClip(frame.clip));if(w.folderBackdrop)w.folderBackdrop.IsVisible(false);
            auto start=close?opened:cover;w.folderMotion.StopAnimation(L"Scale");w.folderMotion.StopAnimation(L"Offset");w.folderMotion.StopAnimation(L"Opacity");w.folderMotion.Scale({1,1,1});w.folderMotion.CenterPoint({0,0,0});w.folderMotion.Offset({start.left*scale,start.top*scale,0});w.folderMotion.Opacity(1);w.panel.Opacity(close?1.f:0.f);return true;
        }catch(...){Release(w);return false;}
    }
    void Animate(Window &w,bool close){auto &c=w.app.graphics.compositor;float scale=w.dpi/96;auto target=close?cover:opened;Preview70Theme theme{Preview70Theme::systemDark};
        frame.Animate(c,{(target.right-target.left)*scale,(target.bottom-target.top)*scale},(close?NativeMetrics::PanelRadius:NativeMetrics::FloatingRadius)*scale,theme.Material(close?NativeMaterial::Card:NativeMaterial::Panel,glass),Duration);
        NativeInteractionMotion::Vector(c,w.folderMotion,L"Offset",{target.left*scale,target.top*scale,0},Duration);
        NativeInteractionMotion::Opacity(c,w.panel,close?0.f:1.f,Duration);
        for(auto &item:icons){NativeInteractionMotion::Vector(c,item.visual,L"Offset",close?item.closed:item.opened,Duration);NativeInteractionMotion::Vector(c,item.visual,L"Scale",close?item.compactScale:item.expandedScale,Duration);if(!item.mapped)NativeInteractionMotion::Opacity(c,item.visual,close?0.f:1.f,Duration);}
    }
};
inline void Window::ResetFolderMorph(){if(!folderMorph)return;folderMorph->Release(*this);folderMorph.reset();++animationGeneration;if(batch){batch.Completed(batchToken);batch=nullptr;}closing=false;folderMotion.StopAnimation(L"Offset");folderMotion.StopAnimation(L"Scale");folderMotion.Opacity(1);folderMotion.Scale({1,1,1});folderMotion.Offset({panelRect.left*dpi/96,panelRect.top*dpi/96,0});panelStamp.valid=false;}
