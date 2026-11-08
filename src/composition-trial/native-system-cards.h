#pragma once
#include "native-encodeck-layout.h"
#include "native-todo-card.h"
#include "native-week-card.h"
#include "native-honor-service-card.h"
#include "native-media-card.h"
#include "native-handoff-card.h"
// New preset views share the desktop surface and a single decoded clipboard texture.
inline void LoadClipboardTexture(App &app,JsonObject const &data)
{
    app.clipboardBitmap=nullptr;app.clipboardPng=Str(data,L"png");
    if(!Bool(data,L"available")||!app.graphics.wic)return;
    auto const &png=app.clipboardPng;DWORD length=0;
    if(!CryptStringToBinaryW(png.c_str(),static_cast<DWORD>(png.size()),CRYPT_STRING_BASE64,nullptr,&length,nullptr,nullptr)||length>512*1024)return;
    std::vector<BYTE> bytes(length);if(!CryptStringToBinaryW(png.c_str(),static_cast<DWORD>(png.size()),CRYPT_STRING_BASE64,bytes.data(),&length,nullptr,nullptr))return;
    com_ptr<IStream> stream;stream.attach(SHCreateMemStream(bytes.data(),length));com_ptr<IWICBitmapDecoder> decoder;com_ptr<IWICBitmapFrameDecode> frame;
    check_hresult(app.graphics.wic->CreateDecoderFromStream(stream.get(),nullptr,WICDecodeMetadataCacheOnLoad,decoder.put()));check_hresult(decoder->GetFrame(0,frame.put()));
    UINT w=0,h=0;check_hresult(frame->GetSize(&w,&h));if(!w||!h||w>752||h>368)return;
    com_ptr<IWICFormatConverter> converter;check_hresult(app.graphics.wic->CreateFormatConverter(converter.put()));check_hresult(converter->Initialize(frame.get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
    com_ptr<ID2D1DeviceContext> context;check_hresult(app.graphics.d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.put()));check_hresult(context->CreateBitmapFromWicBitmap(converter.get(),nullptr,app.clipboardBitmap.put()));
}
#include "native-weather-card.h"
inline bool PaintPresetCard(Window &window,ID2D1DeviceContext *c,Tile const &tile,D2D1_RECT_F rect)
{
    auto type=Str(tile.card,L"type");if(type!=L"system.weather"&&type!=L"system.clipboard-image"&&type!=L"system.encodeck"&&type!=L"system.handoff"&&type!=L"system.media"&&type!=L"system.honor-service"&&type!=L"system.todo"&&type!=L"system.week")return false;
    float w=static_cast<float>(Num(tile.card,L"columns",2))*96-8,h=static_cast<float>(Num(tile.card,L"rows",2))*96-8;
    float scale=std::min((rect.right-rect.left)/w,(rect.bottom-rect.top)/h);D2D1_MATRIX_3X2_F old;c->GetTransform(&old);c->SetTransform(D2D1::Matrix3x2F::Scale(scale,scale)*D2D1::Matrix3x2F::Translation(rect.left,rect.top)*old);
    const float radius=NativeMetrics::PanelRadius/std::max(.01f,scale);
    NativePaint p(c,window.app.graphics,Preview70Theme::systemDark);auto ink=p.theme.Ink(),muted=p.theme.MaterialMuted();if(!NativeLowPower(window.app.model))p.Shadow({0,0,w,h},radius);p.Material({0,0,w,h},NativeMaterial::Card,!NativeLowPower(window.app.model)&&(window.cardBackdrops.contains(tile.key)||window.materialPreview),radius);
    auto text=[&](std::wstring const &s,D2D1_RECT_F r,float size=12.5f,bool dim=false,bool center=false){p.Text(s,r,size,dim?muted:ink,400,center?DWRITE_TEXT_ALIGNMENT_CENTER:DWRITE_TEXT_ALIGNMENT_LEADING,0,true);};
    if(type==L"system.handoff"){PaintNativeHandoff(window,p,tile,w,h);}
    else if(type==L"system.media"){PaintNativeMedia(window,p,tile,w,h);}
    else if(type==L"system.honor-service"){PaintNativeHonorService(window,p,tile,w,h);}
    else if(type==L"system.todo"){PaintNativeTodo(p,NativeTodoData(window,tile),w,h,window.todoScrolls[tile.key]);}
    else if(type==L"system.week"){PaintNativeWeek(window,p,tile,w,h);}
    else if(type==L"system.clipboard-image"){
        if(auto bitmap=window.app.clipboardBitmap){
            com_ptr<ID2D1Factory> factory;c->GetFactory(factory.put());com_ptr<ID2D1RoundedRectangleGeometry> geometry;check_hresult(factory->CreateRoundedRectangleGeometry(D2D1::RoundedRect({0,0,w,h},radius,radius),geometry.put()));
            auto layer=D2D1::LayerParameters(D2D1::InfiniteRect(),geometry.get());c->PushLayer(layer,nullptr);p.Fill({0,0,w,h},D2D1::ColorF(0x202020));auto size=bitmap->GetSize();float zoom=std::max(w/size.width,h/size.height),bw=size.width*zoom,bh=size.height*zoom;
            c->DrawBitmap(bitmap.get(),{(w-bw)/2,(h-bh)/2,(w+bw)/2,(h+bh)/2},1,D2D1_INTERPOLATION_MODE_LINEAR);c->PopLayer();
        }else{
            p.EmptyFrame({18,18,w-18,h-18},muted);p.Icon(L"image",{w/2-14,h/2-37,w/2+14,h/2-9},muted);
            text(L"还没有图片",{18,h/2+1,w-18,h/2+24},12,false,true);text(L"截一张图，它会自己贴上来",{18,h/2+27,w-18,h/2+47},10.5f,true,true);
        }
    }else if(type==L"system.encodeck"){
        auto data=window.app.model.encodeckData;bool ready=data&&Bool(data,L"controlReady")&&Bool(data,L"canSetNoise");
        auto status=Str(data,L"providerStatus"),name=Str(data,L"deviceName");bool available=data&&Bool(data,L"available"),linked=data&&Bool(data,L"deviceConnected");
        EncoIcon(p,0,{16,15,36,35},muted);p.Text(!name.empty()?name:L"EncoDeck 耳机",{44,10,334,29},14,ink,600);
        std::wstring hint=ready?L"已连接 · 可调整":linked?L"已连接 · 控制暂不可用":Bool(data,L"connecting")?L"正在连接…":status==L"desktop-card-bridge"?L"EncoDeck 已就绪 · 耳机未连接":available?L"未连接 · 数值来自上次读取":status==L"not-found"?L"请指定 EncoDeck 安装位置":L"未连接 · 点击连接 EncoDeck";
        p.Text(hint,{44,30,334,44},9,muted,400);EncoOpenIcon(p,{344,18,360,34},ink);p.Line(16,50,w-16,50,muted,.4f);p.Line(128,60,128,174,muted,.4f);
        int n=0;for(auto const *key:{L"left",L"right",L"case"}){
            auto value=data?data.TryLookup(key):nullptr;auto battery=value&&value.ValueType()==JsonValueType::Object?value.GetObject():JsonObject{};double level=Num(battery,L"level",-1);bool stale=Bool(battery,L"stale",true);auto color=stale?muted:ink;float y=68+n*35.f;
            p.c->DrawRoundedRectangle(D2D1::RoundedRect({17,y+4,35,y+16},2,2),p.Color(color),1.2f);p.Line(36,y+8,36,y+12,color,1.6f);
            if(level>=0)p.Fill({20,y+7,20+12*static_cast<float>(std::clamp(level,0.,100.))/100,y+13},color,1);
            p.Text(level>=0?CardNumber(level)+L"%":L"—",{43,y,91,y+25},17,color,600);p.Text(n==0?L"左耳":n==1?L"右耳":L"充电盒",{91,y+5,122,y+22},8.5f,muted,400,DWRITE_TEXT_ALIGNMENT_TRAILING);
            if(Bool(battery,L"charging"))p.Text(L"充电",{43,y+23,91,y+33},7.5f,muted);++n;
        }
        auto modes=data?data.GetNamedArray(L"modes",JsonArray{}):JsonArray{},levels=data?data.GetNamedArray(L"levels",JsonArray{}):JsonArray{};auto current=Str(data,L"noiseMode");
        p.Text(L"降噪模式",{140,58,360,72},9.5f,ready?muted:p.theme.Gray(450),500);
        for(unsigned slot=0;slot<4;slot++){
            int index=EncoModeIndex(modes,slot);if(index<0&&available)continue;auto mode=index>=0?modes.GetAt(index).GetObject():JsonObject{};bool selected=Str(mode,L"mode")==current&&!current.empty(),enabled=ready&&Bool(mode,L"enabled");auto box=EncoModeRect(slot);auto fill=selected?p.theme.Accent():p.theme.Raised();if(!enabled)fill.a=selected?.18f:.3f;p.Fill(box,fill,6);auto color=enabled?(selected?p.theme.OnAccent():ink):muted;
            EncoIcon(p,slot,{box.left+17,box.top+9,box.left+32,box.top+24},color);p.Text(EncoLabels[slot],{box.left+1,box.top+29,box.right-1,box.bottom-3},9.5f,color,500,DWRITE_TEXT_ALIGNMENT_CENTER);
        }
        if(levels.Size()){
            p.Text(current==L"NoiseCancellation"?L"降噪强度":current==L"Adaptive"?L"自适应偏好":current==L"Transparency"?L"通透模式":L"模式强度",{140,131,360,144},9,muted);
            for(unsigned i=0;i<levels.Size()&&i<4;i++){auto level=levels.GetAt(i).GetObject();bool selected=Bool(level,L"selected"),enabled=ready&&Bool(level,L"enabled");auto fill=selected?p.theme.Accent():p.theme.Raised();if(!enabled)fill.a=selected?.18f:.3f;auto box=EncoLevelRect(i);p.Fill(box,fill,5);p.Text(Str(level,L"label"),{box.left+1,box.top+1,box.right-1,box.bottom-1},9,enabled?(selected?p.theme.OnAccent():ink):muted,500,DWRITE_TEXT_ALIGNMENT_CENTER,1);}
        }
        if(!ready){auto veil=p.theme.Surface();veil.a=.78f;p.Fill({136,56,364,177},veil,7);p.Text(linked?L"当前设备暂无可用降噪控制":available?L"连接耳机后可调整":L"点击卡片连接 EncoDeck",{138,103,362,133},11,ink,500,DWRITE_TEXT_ALIGNMENT_CENTER);}
    }else PaintNativeWeather(window,p,tile,w,h,radius);
    c->DrawRoundedRectangle(D2D1::RoundedRect({.5f,.5f,w-.5f,h-.5f},radius,radius),p.Color(p.theme.Gray(300,.24f)),1);
    c->SetTransform(old);return true;
}
