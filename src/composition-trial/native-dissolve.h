#pragma once
// Optional, finite in-place erasure. The original visual tree remains the source;
// no screenshots, particle simulation, extra device, or CPU frame timer.
namespace NativeDissolve {
inline constexpr int DurationMs=130;
inline constexpr float GrainDip=1.5f,Slope=16.f;
inline constexpr size_t MaskLimit=256*1024,LayerLimit=4*1024*1024;
inline float Offset(float time){return 1-(Slope+1)*(.5f-.5f*std::cos(std::clamp(time,0.f,1.f)*3.141592654f));}
inline unsigned Hash(unsigned x,unsigned y){unsigned n=x*0x1f123bb5u+y*0x5f356495u+17u;n^=n>>16;n*=0x7feb352du;n^=n>>15;n*=0x846ca68bu;return n^(n>>16);}
inline float Value(unsigned x,unsigned y){return (Hash(x,y)&65535)/65535.f;}
inline float Blob(unsigned x,unsigned y){
    float tx=(x%8)/8.f,ty=(y%8)/8.f;tx=tx*tx*(3-2*tx);ty=ty*ty*(3-2*ty);
    auto a=Value(x/8,y/8),b=Value(x/8+1,y/8),c=Value(x/8,y/8+1),d=Value(x/8+1,y/8+1);
    return (a+(b-a)*tx)*(1-ty)+(c+(d-c)*tx)*ty;
}
struct Noise {UINT width=0,height=0;std::vector<BYTE> pixels;};
inline Noise MakeNoise(float width,float height){
    Noise n;float step=GrainDip;
    // Keep grain size stable across DPI; reduce resolution only for extreme bars.
    if(width*height/(step*step)*4>MaskLimit)step=std::sqrt(width*height*4/MaskLimit)*1.02f;
    n.width=std::max(1u,static_cast<UINT>(std::ceil(width/step)));n.height=std::max(1u,static_cast<UINT>(std::ceil(height/step)));
    if(static_cast<size_t>(n.width)*n.height*4>MaskLimit)throw std::runtime_error("Dissolve mask budget");
    n.pixels.resize(static_cast<size_t>(n.width)*n.height*4);std::array<unsigned,256> bins{},cdf{};
    for(UINT y=0;y<n.height;y++)for(UINT x=0;x<n.width;x++){
        BYTE v=static_cast<BYTE>(std::clamp((Value(x,y)*.38f+Blob(x,y)*.62f)*255.f,0.f,255.f));
        n.pixels[(static_cast<size_t>(y)*n.width+x)*4+3]=v;++bins[v];
    }
    unsigned sum=0;for(unsigned i=0;i<256;i++){cdf[i]=sum+bins[i]/2;sum+=bins[i];}
    for(size_t i=0;i<n.pixels.size();i+=4){BYTE v=static_cast<BYTE>(cdf[n.pixels[i+3]]*255u/sum);n.pixels[i]=n.pixels[i+1]=n.pixels[i+2]=n.pixels[i+3]=v;}
    return n;
}
// Same Direct2D graph ABI already used by native-material.h, with two sources
// and one explicitly animatable gamma-transfer alpha offset.
struct Effect : implements<Effect,Windows::Graphics::Effects::IGraphicsEffect,
    Windows::Graphics::Effects::IGraphicsEffectSource,NativeEffectAbi::IGraphicsEffectD2D1Interop>{
    GUID id;hstring name;std::vector<Windows::Foundation::IInspectable> values;
    std::vector<Windows::Graphics::Effects::IGraphicsEffectSource> sources;
    Effect(GUID id,std::vector<Windows::Foundation::IInspectable> values,std::vector<Windows::Graphics::Effects::IGraphicsEffectSource> sources):id(id),values(std::move(values)),sources(std::move(sources)){}
    hstring Name()const{return name;}void Name(hstring const &v){name=v;}
    HRESULT __stdcall GetEffectId(GUID *v)noexcept override{if(!v)return E_POINTER;*v=id;return S_OK;}
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR key,UINT *index,NativeEffectAbi::GRAPHICS_EFFECT_PROPERTY_MAPPING *mapping)noexcept override{
        if(!key||!index||!mapping)return E_POINTER;
        if(id==CLSID_D2D1GammaTransfer&&wcscmp(key,L"AlphaOffset")==0){*index=D2D1_GAMMATRANSFER_PROP_ALPHA_OFFSET;*mapping=NativeEffectAbi::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;return S_OK;}return E_INVALIDARG;
    }
    HRESULT __stdcall GetPropertyCount(UINT *v)noexcept override{if(!v)return E_POINTER;*v=static_cast<UINT>(values.size());return S_OK;}
    HRESULT __stdcall GetProperty(UINT i,ABI::Windows::Foundation::IPropertyValue **v)noexcept override{if(!v)return E_POINTER;*v=nullptr;if(i>=values.size())return E_INVALIDARG;values[i].as<ABI::Windows::Foundation::IPropertyValue>().copy_to(v);return S_OK;}
    HRESULT __stdcall GetSourceCount(UINT *v)noexcept override{if(!v)return E_POINTER;*v=static_cast<UINT>(sources.size());return S_OK;}
    HRESULT __stdcall GetSource(UINT i,NativeEffectAbi::IGraphicsEffectSource **v)noexcept override{if(!v)return E_POINTER;*v=nullptr;if(i>=sources.size())return E_INVALIDARG;sources[i].as<NativeEffectAbi::IGraphicsEffectSource>().copy_to(v);return S_OK;}
};
inline CompositionEffectFactory Factory(Compositor const &compositor){
    using Windows::Foundation::PropertyValue;using Windows::Graphics::Effects::IGraphicsEffectSource;
    std::vector<Windows::Foundation::IInspectable> values;
    for(int i=0;i<4;i++){values.push_back(PropertyValue::CreateSingle(i==3?Slope:1));values.push_back(PropertyValue::CreateSingle(1));values.push_back(PropertyValue::CreateSingle(i==3?1.f:0.f));values.push_back(PropertyValue::CreateBoolean(i!=3));}
    values.push_back(PropertyValue::CreateBoolean(true));
    auto gamma=make<Effect>(CLSID_D2D1GammaTransfer,std::move(values),std::vector<IGraphicsEffectSource>{CompositionEffectSourceParameter(L"noise")});gamma.Name(L"erase");
    auto composite=make<Effect>(CLSID_D2D1Composite,std::vector<Windows::Foundation::IInspectable>{PropertyValue::CreateUInt32(D2D1_COMPOSITE_MODE_DESTINATION_IN)},std::vector<IGraphicsEffectSource>{CompositionEffectSourceParameter(L"content"),gamma.as<IGraphicsEffectSource>()});
    return compositor.CreateEffectFactory(composite,{L"erase.AlphaOffset"});
}
struct Session {
    LayerVisual layer{nullptr};CompositionDrawingSurface noise{nullptr};CompositionEffectBrush brush{nullptr};
    size_t maskBytes=0,layerBytes=0;HRESULT error=S_OK;
    bool Active()const{return layer!=nullptr;}
    void Play(Compositor const &c,bool reveal,bool interrupted=false){
        if(!brush)return;auto a=c.CreateScalarKeyFrameAnimation();
        if(interrupted){a.InsertExpressionKeyFrame(0,L"this.StartingValue");a.InsertKeyFrame(1,reveal?1.f:-Slope,c.CreateCubicBezierEasingFunction({.42f,0},{.58f,1}));}
        else for(int i=0;i<=8;i++)a.InsertKeyFrame(i/8.f,Offset(reveal?1-i/8.f:i/8.f));
        a.Duration(std::chrono::milliseconds(DurationMs));brush.Properties().StartAnimation(L"erase.AlphaOffset",a);
    }
    void Reset(ContainerVisual const &host,ContainerVisual const &content){
        if(brush)brush.Properties().StopAnimation(L"erase.AlphaOffset");
        if(layer){layer.Effect(nullptr);if(content.Parent()==layer)layer.Children().Remove(content);if(layer.Parent()==host)host.Children().Remove(layer);if(!content.Parent())host.Children().InsertAtTop(content);}
        brush=nullptr;noise=nullptr;layer=nullptr;maskBytes=layerBytes=0;
    }
    bool Start(Graphics &g,ContainerVisual const &host,ContainerVisual const &content,float width,float height,float scale,bool reveal=false){
        error=S_OK;if(Active()||width<=0||height<=0||!host||!content)return false;
        size_t bytes=static_cast<size_t>(std::ceil(width*scale))*static_cast<size_t>(std::ceil(height*scale))*4;
        if(bytes>LayerLimit)return false;
        HIGHCONTRASTW high{sizeof(high)};SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(high),&high,0);
        if(high.dwFlags&HCF_HIGHCONTRASTON||GetSystemMetrics(SM_REMOTESESSION))return false;
        try{
            auto pixels=MakeNoise(width,height);if(!g.dissolveFactory)g.dissolveFactory=Factory(g.compositor);brush=g.dissolveFactory.CreateBrush();
            noise=g.composition.CreateDrawingSurface({static_cast<float>(pixels.width),static_cast<float>(pixels.height)},Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
            auto interop=noise.as<abiComposition::ICompositionDrawingSurfaceInterop>();com_ptr<ID2D1DeviceContext> c;POINT point{};
            check_hresult(interop->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),c.put_void(),&point));
            try{
                c->SetDpi(96,96);c->SetTransform(D2D1::Matrix3x2F::Translation(static_cast<float>(point.x),static_cast<float>(point.y)));c->Clear(D2D1::ColorF(0,0));
                auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));com_ptr<ID2D1Bitmap1> bitmap;
                check_hresult(c->CreateBitmap({pixels.width,pixels.height},pixels.pixels.data(),pixels.width*4,&props,bitmap.put()));
                c->DrawBitmap(bitmap.get(),{0,0,static_cast<float>(pixels.width),static_cast<float>(pixels.height)},1,D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR);check_hresult(interop->EndDraw());
            }catch(...){interop->EndDraw();throw;}
            auto source=g.compositor.CreateSurfaceBrush(noise);source.Stretch(CompositionStretch::Fill);source.BitmapInterpolationMode(CompositionBitmapInterpolationMode::NearestNeighbor);brush.SetSourceParameter(L"noise",source);
            // content is deliberately unbound: LayerVisual supplies its subtree.
            auto next=g.compositor.CreateLayerVisual();next.Size({width*scale,height*scale});next.Clip(g.compositor.CreateInsetClip());next.Effect(brush);
            brush.Properties().InsertScalar(L"erase.AlphaOffset",reveal?-Slope:1.f);Play(g.compositor,reveal);
            layer=next;content.StopAnimation(L"Offset");content.Offset({0,0,0});host.Children().Remove(content);next.Children().InsertAtTop(content);host.Children().InsertAtTop(next);maskBytes=pixels.pixels.size();layerBytes=bytes;return true;
        }catch(winrt::hresult_error const &e){error=e.code();}catch(...){error=E_FAIL;}
        Reset(host,content);return false;
    }
};
}
