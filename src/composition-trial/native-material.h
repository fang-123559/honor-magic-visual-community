#pragma once
#include <d2d1effects.h>
#include <dwmapi.h>
#pragma comment(lib,"dxguid.lib")
#include <windows.graphics.effects.interop.h>
#include <winrt/Windows.Graphics.Effects.h>
namespace NativeEffectAbi=ABI::Windows::Graphics::Effects;
struct Preview70Effect : implements<Preview70Effect,Windows::Graphics::Effects::IGraphicsEffect,
    Windows::Graphics::Effects::IGraphicsEffectSource,NativeEffectAbi::IGraphicsEffectD2D1Interop>
{
    GUID effect;
    std::vector<Windows::Foundation::IInspectable> properties;
    Windows::Graphics::Effects::IGraphicsEffectSource source{nullptr};
    hstring name;
    Preview70Effect(GUID id,std::vector<Windows::Foundation::IInspectable> values,
                    Windows::Graphics::Effects::IGraphicsEffectSource input):effect(id),properties(std::move(values)),source(input){}
    hstring Name()const{return name;}void Name(hstring const &value){name=value;}
    HRESULT __stdcall GetEffectId(GUID *id)noexcept override{if(!id)return E_POINTER;*id=effect;return S_OK;}
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR,UINT*,NativeEffectAbi::GRAPHICS_EFFECT_PROPERTY_MAPPING*)noexcept override{return E_INVALIDARG;}
    HRESULT __stdcall GetPropertyCount(UINT *count)noexcept override{if(!count)return E_POINTER;*count=static_cast<UINT>(properties.size());return S_OK;}
    HRESULT __stdcall GetProperty(UINT i,ABI::Windows::Foundation::IPropertyValue **value)noexcept override
    {if(!value)return E_POINTER;*value=nullptr;if(i>=properties.size())return E_INVALIDARG;properties[i].as<ABI::Windows::Foundation::IPropertyValue>().copy_to(value);return S_OK;}
    HRESULT __stdcall GetSourceCount(UINT *count)noexcept override{if(!count)return E_POINTER;*count=1;return S_OK;}
    HRESULT __stdcall GetSource(UINT i,NativeEffectAbi::IGraphicsEffectSource **value)noexcept override
    {if(!value)return E_POINTER;*value=nullptr;if(i)return E_INVALIDARG;source.as<NativeEffectAbi::IGraphicsEffectSource>().copy_to(value);return S_OK;}
};
enum class NativeMaterial { Dock=1, Panel=2, Menu=3, Card=4 };
inline CompositionBrush Preview70Glass(Graphics &g,float scale=1,NativeMaterial tier=NativeMaterial::Card,float blurRadius=0)
{
    using Windows::Foundation::PropertyValue;
    int radius=static_cast<int>(std::round(blurRadius>0?blurRadius:tier==NativeMaterial::Panel?32.f:tier==NativeMaterial::Menu?24.f:tier==NativeMaterial::Dock?20.f:26.f));
    int key=(static_cast<int>(scale*96)*8+static_cast<int>(tier))*64+radius;
    auto found=g.glassFactories.find(key);
    if(found==g.glassFactories.end()){
        if(g.glassFactories.size()>=16)g.glassFactories.clear();
        auto input=CompositionEffectSourceParameter(L"wallpaper");
        auto blur=make<Preview70Effect>(CLSID_D2D1GaussianBlur,std::vector<Windows::Foundation::IInspectable>{
            PropertyValue::CreateSingle(radius*scale),PropertyValue::CreateUInt32(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED),PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD)},input);
        auto saturation=make<Preview70Effect>(CLSID_D2D1Saturation,std::vector<Windows::Foundation::IInspectable>{PropertyValue::CreateSingle(tier==NativeMaterial::Menu||tier==NativeMaterial::Card?1.35f:1.4f)},blur.as<Windows::Graphics::Effects::IGraphicsEffectSource>());
        found=g.glassFactories.emplace(key,g.compositor.CreateEffectFactory(saturation)).first;
    }
    auto brush=found->second.CreateBrush();brush.SetSourceParameter(L"wallpaper",g.compositor.CreateHostBackdropBrush());return brush;
}

// Event/draw driven only. No wallpaper capture, new device or frame timer.
inline bool NativeGlassAvailable()
{
    HIGHCONTRASTW high{sizeof(high)};SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(high),&high,0);
    DWORD transparency=1,bytes=sizeof(transparency);
    RegGetValueW(HKEY_CURRENT_USER,L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",L"EnableTransparency",RRF_RT_REG_DWORD,nullptr,&transparency,&bytes);
    return transparency&&!(high.dwFlags&HCF_HIGHCONTRASTON)&&!GetSystemMetrics(SM_REMOTESESSION);
}
inline void ReleaseNativeGlass(ContainerVisual const &parent,SpriteVisual &visual)
{
    if(visual){parent.Children().Remove(visual);visual.Brush(nullptr);visual=nullptr;}
}
struct NativeGlassState{size_t bytes=0;HRESULT error=S_OK;std::wstring reason;};
inline std::map<HWND,NativeGlassState> &NativeGlassStates(){static std::map<HWND,NativeGlassState> states;return states;}
inline void PrepareNativeGlassWindow(HWND hwnd){
    // Popup kinds with native EDIT children retain redirection. Make their
    // host transparent before sampling; Composition-only tray hosts discard
    // redirection at creation and use the same backdrop opt-in.
    MARGINS frame{-1,-1,-1,-1};check_hresult(DwmExtendFrameIntoClientArea(hwnd,&frame));
    BOOL allow=TRUE;check_hresult(DwmSetWindowAttribute(hwnd,17,&allow,sizeof(allow)));
}
// The content surface and blur halo share the existing 8 MiB bound. Long
// functional panels use a shorter viewport with scrolling rather than losing
// their designed material or increasing the allocation limit.
inline float NativeGlassHeightLimit(float width,float scale,NativeMaterial tier)
{
    if(width<=0||scale<=0)return 0;
    float halo=6*(tier==NativeMaterial::Panel?32.f:tier==NativeMaterial::Menu?24.f:tier==NativeMaterial::Dock?20.f:26.f)*scale;
    return std::max(0.f,std::floor((8*1024*1024.f/4)/(std::ceil(width*scale)+halo)-halo-4)/scale);
}
inline bool SyncNativeGlass(Graphics &g,HWND hwnd,ContainerVisual const &parent,SpriteVisual &visual,
                            D2D1_RECT_F rect,float scale,float radius,NativeMaterial tier,bool enabled,
                            Visual const &below=nullptr,float blurRadius=0)
{
    auto &state=NativeGlassStates()[hwnd];state.error=S_OK;
    float w=(rect.right-rect.left)*scale,h=(rect.bottom-rect.top)*scale;
    float halo=6*(blurRadius>0?blurRadius:tier==NativeMaterial::Panel?32.f:tier==NativeMaterial::Menu?24.f:tier==NativeMaterial::Dock?20.f:26.f)*scale;
    if(!enabled||!parent||!hwnd||w<=0||h<=0||(w+halo)*(h+halo)*4>8*1024*1024||!NativeGlassAvailable()){
        state.reason=!enabled?L"power-or-hidden":!NativeGlassAvailable()?L"system-transparency-or-session":L"surface-budget";state.bytes=0;
        if(parent)ReleaseNativeGlass(parent,visual);return false;
    }
    try{
        auto key=std::to_wstring(static_cast<int>(scale*96))+L":"+std::to_wstring(static_cast<int>(tier));
        if(!visual){
            PrepareNativeGlassWindow(hwnd);
            visual=g.compositor.CreateSpriteVisual();
            if(below)parent.Children().InsertBelow(visual,below);else parent.Children().InsertAtBottom(visual);
        }
        // A brush bound while its HWND is hidden may not yet have a DWM host.
        // Rebind once after show; normal paint/input keeps the same brush.
        key+=L":"+std::to_wstring(radius)+L":"+std::to_wstring(blurRadius)+L":"+std::to_wstring(IsWindowVisible(hwnd)!=FALSE);
        bool changed=visual.Comment()!=key||visual.Size().x!=w||visual.Size().y!=h;
        if(visual.Comment()!=key){visual.Brush(Preview70Glass(g,scale,tier,blurRadius));visual.Comment(key);}
        if(changed){visual.Size({w,h});auto geometry=g.compositor.CreateRoundedRectangleGeometry();geometry.Size({w,h});geometry.CornerRadius({radius*scale,radius*scale});visual.Clip(g.compositor.CreateGeometricClip(geometry));}
        visual.Offset({rect.left*scale,rect.top*scale,0});state.reason=L"host-backdrop";state.bytes=static_cast<size_t>((w+halo)*(h+halo)*4);return true;
    }catch(winrt::hresult_error const &e){state.error=e.code();state.reason=L"composition-error";state.bytes=0;ReleaseNativeGlass(parent,visual);return false;}
    catch(...){state.error=E_FAIL;state.reason=L"unknown-error";state.bytes=0;ReleaseNativeGlass(parent,visual);return false;} // Readable solid tint is painted by the caller.
}


// A panel is one continuous backdrop sampling region. Independent effect brushes
// per tile clamp/sample independently and expose rectangular seams. Keep the
// existing aggregate sheet budget, with one output instead of a tiled graph.
struct NativeGlassSheet {
    ContainerVisual root{nullptr};SpriteVisual sheet{nullptr};size_t bytes=0;
    void Release(ContainerVisual const &parent){if(root&&root.Parent()==parent)parent.Children().Remove(root);root=nullptr;sheet=nullptr;bytes=0;}
    bool Sync(Graphics &g,HWND hwnd,ContainerVisual const &parent,D2D1_RECT_F rect,float scale,float radius,NativeMaterial tier,bool enabled,Visual const &below,size_t budget){
        if(!enabled||!NativeGlassAvailable()||!parent||!hwnd){Release(parent);return false;}
        float w=(rect.right-rect.left)*scale,h=(rect.bottom-rect.top)*scale,blur=26;size_t estimate=0;
        budget=std::min<size_t>(budget,32*1024*1024);
        for(;blur>=12;blur-=2){float halo=6*blur*scale;estimate=static_cast<size_t>(std::ceil(w+halo))*static_cast<size_t>(std::ceil(h+halo))*4;if(estimate<=budget)break;}
        if(w<=0||h<=0||blur<12||estimate>budget){Release(parent);return false;}
        try{
            if(!root){PrepareNativeGlassWindow(hwnd);root=g.compositor.CreateContainerVisual();parent.Children().InsertBelow(root,below);sheet=g.compositor.CreateSpriteVisual();root.Children().InsertAtBottom(sheet);}
            root.Offset({rect.left*scale,rect.top*scale,0});root.Size({w,h});
            auto key=std::to_wstring(scale)+L":"+std::to_wstring(static_cast<int>(tier))+L":"+std::to_wstring(blur)+L":"+std::to_wstring(radius)+L":"+std::to_wstring(w)+L":"+std::to_wstring(h)+L":"+std::to_wstring(IsWindowVisible(hwnd)!=FALSE);
            if(sheet.Comment()!=key){sheet.Brush(Preview70Glass(g,scale,tier,blur));sheet.Size({w,h});auto shape=g.compositor.CreateRoundedRectangleGeometry();shape.Size({w,h});shape.CornerRadius({radius*scale,radius*scale});root.Clip(g.compositor.CreateGeometricClip(shape));sheet.Comment(key);}
            bytes=estimate;return true;
        }catch(...){Release(parent);return false;}
    }
};
