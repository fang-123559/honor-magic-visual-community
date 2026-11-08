#pragma once
#include "native-material.h"
inline void Window::SyncCardBackdrops()
{
    // Page snapshots can remove the neighbour while the pointer stays still.
    // Clear both the painted affordance and its separate Composition glass.
    int validEdge=AvailablePageEdge(pageEdge);
    if(validEdge!=pageEdge){pageEdge=validEdge;desktopStamp.valid=false;}
    if(!pageLayer||!app.model.desktopRuntime)return;
    std::set<std::wstring> live;size_t budget=0;auto b=app.model.Find(boardId);float scale=dpi/96;
    std::set<std::wstring> previous;for(auto const &[key,_]:cardBackdrops)previous.insert(key);
    std::vector<Tile const*> candidates;
    if(b&&!sessionPaused&&!NativeLowPower(app.model)&&NativeGlassAvailable())for(auto const &tile:b->tiles){
        if(!tile.card&&tile.folder.empty()||!folderId.empty()&&tile.folder==folderId)continue;
        if(tile.card&&(Str(tile.card,L"type")==L"system.clock"||Str(tile.card,L"type")==L"system.clipboard-image"&&app.clipboardBitmap))continue;
        auto r=RootBounds(tile.key);if(r.right<=0||r.bottom<=0||r.left>=width||r.top>=height)continue;candidates.push_back(&tile);
    }
    D2D1_RECT_F pager{};bool wantPager=!GridEditing()&&b&&b->pages.size()>1&&folderId.empty()&&pageEdge&&!sessionPaused&&!NativeLowPower(app.model)&&NativeGlassAvailable();
    if(wantPager){float x=pageEdge<0?6:width-112,y=height/2-24;pager={x,y,x+106,y+48};}
    bool hadPager=static_cast<bool>(pageBackdrop);
    cardBackdropBytes=0;cardBlurRadius=0;float blur=26;
    auto estimate=[&](D2D1_RECT_F r,float amount){return static_cast<size_t>(std::ceil((r.right-r.left+6*amount)*scale))*static_cast<size_t>(std::ceil((r.bottom-r.top+6*amount)*scale))*4;};
    for(;blur>=6;blur-=2){size_t total=wantPager?estimate(pager,blur):0;for(auto tile:candidates)total+=estimate(RootBounds(tile->key),blur);if(total<=8*1024*1024)break;}
    if(blur>=6&&(!candidates.empty()||wantPager))cardBlurRadius=blur;
    if(blur>=6)for(auto tile:candidates){
        auto r=RootBounds(tile->key);auto &visual=cardBackdrops.try_emplace(tile->key,nullptr).first->second;
        if(!SyncNativeGlass(app.graphics,hwnd,pageLayer,visual,r,scale,NativeMetrics::PanelRadius,NativeMaterial::Card,true,desktop,blur)){cardBackdrops.erase(tile->key);continue;}
        budget+=estimate(r,blur);cardBackdropBytes=budget;visual.Opacity(pendingDragKeys.contains(tile->key)?0.f:dragging&&dragKeys.contains(tile->key)?.35f:1.f);live.insert(tile->key);
    }
    if(wantPager&&blur>=6&&SyncNativeGlass(app.graphics,hwnd,pageLayer,pageBackdrop,pager,scale,24,NativeMaterial::Card,true,desktop,blur)){budget+=estimate(pager,blur);cardBackdropBytes=budget;}else ReleaseNativeGlass(pageLayer,pageBackdrop);
    if(previous!=live||hadPager!=static_cast<bool>(pageBackdrop))desktopStamp.valid=false;
    for(auto it=cardBackdrops.begin();it!=cardBackdrops.end();)if(!live.contains(it->first)){ReleaseNativeGlass(pageLayer,it->second);it=cardBackdrops.erase(it);}else ++it;
}
