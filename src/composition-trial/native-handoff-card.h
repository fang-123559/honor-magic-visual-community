#pragma once
#include "native-card-text.h"
inline JsonObject NativeHandoffView(Window const &window,Tile const &tile){auto id=Str(tile.card,L"id");for(auto const &value:window.app.model.handoffData){auto view=value.GetObject();if(Str(view,L"id")==id)return view;}return JsonObject{};}
struct NativeHandoffInline : winrt::implements<NativeHandoffInline,IDWriteInlineObject>{
    float width,height;NativeHandoffInline(float w,float h):width(w),height(h){}
    HRESULT __stdcall Draw(void*,IDWriteTextRenderer*,FLOAT,FLOAT,BOOL,BOOL,IUnknown*)noexcept override{return S_OK;}
    HRESULT __stdcall GetMetrics(DWRITE_INLINE_OBJECT_METRICS *value)noexcept override{if(!value)return E_POINTER;*value={width,height,height-3,FALSE};return S_OK;}
    HRESULT __stdcall GetOverhangMetrics(DWRITE_OVERHANG_METRICS *value)noexcept override{if(!value)return E_POINTER;*value={};return S_OK;}
    HRESULT __stdcall GetBreakConditions(DWRITE_BREAK_CONDITION *before,DWRITE_BREAK_CONDITION *after)noexcept override{if(!before||!after)return E_POINTER;*before=*after=DWRITE_BREAK_CONDITION_CAN_BREAK;return S_OK;}
};
struct NativeHandoffLayout{
    com_ptr<IDWriteTextLayout> text;std::vector<std::pair<uint32_t,int>> markers;
};
inline NativeHandoffLayout NativeHandoffFlow(Graphics &graphics,JsonObject const &view,float width,float height){
    NativeHandoffLayout result;std::wstring text;auto flow=view.GetNamedArray(L"flow",JsonArray{});
    for(UINT i=0;i<flow.Size()&&i<16;i++){auto node=flow.GetAt(i).GetObject();if(Str(node,L"t")==L"text"){auto value=Str(node,L"v");if(value.size()+text.size()>2048)value.resize(2048-text.size());text+=value;}
        else{result.markers.push_back({static_cast<uint32_t>(text.size()),static_cast<int>(i)});text+=L"\xfffc";}if(text.size()>=2048)break;}
    if(text.empty())return result;check_hresult(graphics.text->CreateTextLayout(text.c_str(),static_cast<UINT32>(text.size()),graphics.Format(width>200?12.5f:12.f,false,400),std::max(1.f,width-28),4096,result.text.put()));result.text->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);result.text->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    for(auto const &[position,index]:result.markers){auto node=flow.GetAt(index).GetObject();bool image=Str(node,L"kind")==L"image";auto box=winrt::make_self<NativeHandoffInline>(image?(width>200?44.f:34.f):std::max(32.f,static_cast<float>(Str(node,L"ext").size())*7.f+14),image?(width>200?31.f:24.f):20.f);check_hresult(result.text->SetInlineObject(box.get(),{position,1}));}
    (void)height;return result;
}
inline ID2D1Bitmap1 *NativeHandoffBitmap(Window &window,std::wstring const &key,std::wstring const &png){
    auto &app=window.app;if(png.empty()||png.size()>22000)return nullptr;auto found=app.handoffImages.find(key);
    if(found!=app.handoffImages.end()&&app.handoffImageKeys[key]==png){found->second.age=++app.handoffImageAge;return found->second.bitmap.get();}
    if(found!=app.handoffImages.end()){app.handoffImages.erase(found);app.handoffImageKeys.erase(key);}
    Preview70Icons decoded;decoded.Load(app.graphics,key,png);auto value=decoded.values.find(key);if(value==decoded.values.end())return nullptr;
    size_t bytes=0;for(auto const &[_,v]:app.handoffImages)bytes+=v.bytes;
    while(!app.handoffImages.empty()&&(app.handoffImages.size()>=16||bytes+value->second.bytes>1024*1024)){auto oldest=std::min_element(app.handoffImages.begin(),app.handoffImages.end(),[](auto const &a,auto const &b){return a.second.age<b.second.age;});bytes-=oldest->second.bytes;app.handoffImageKeys.erase(oldest->first);app.handoffImages.erase(oldest);}
    auto image=value->second;image.age=++app.handoffImageAge;app.handoffImages[key]=image;app.handoffImageKeys[key]=png;return app.handoffImages[key].bitmap.get();
}
inline D2D1_RECT_F NativeHandoffBody(float width,float height){return {14,40,width-14,height-31};}
inline std::wstring NativeHandoffAction(Window &window,Tile const &tile,float x,float y,float width,float height){
    if(height<100)return x<width/2?L"handoff-copy":L"handoff-edit";
    if(x>=width-40&&y>=6&&y<=38)return L"handoff-edit";auto flow=NativeHandoffView(window,tile).GetNamedArray(L"flow",JsonArray{});return flow.Size()?L"handoff-copy":L"handoff-edit";
}
inline void PaintNativeHandoff(Window &window,NativePaint &p,Tile const &tile,float width,float height){
    auto view=NativeHandoffView(window,tile);auto ink=p.theme.Ink(),muted=p.theme.MaterialMuted();
    if(height<100){p.Line(width/2,24,width/2,height-24,p.theme.Edge());p.Icon(L"copy",{width/4-8,22,width/4+8,38},ink);p.Text(L"复制",{0,43,width/2,height-14},11.5f,ink,500,DWRITE_TEXT_ALIGNMENT_CENTER);p.Icon(L"edit",{width*.75f-8,22,width*.75f+8,38},ink);p.Text(L"编辑",{width/2,43,width,height-14},11.5f,ink,500,DWRITE_TEXT_ALIGNMENT_CENTER);}
    else{
        p.Text(L"文本交接台",{14,9,82,31},11.5f,ink,600);int images=static_cast<int>(Num(view,L"images")),files=static_cast<int>(Num(view,L"files")),chars=static_cast<int>(Num(view,L"chars"));std::wstring count;
        if(width>200&&chars>0)count=std::to_wstring(chars)+L" 字";for(auto pair:{std::pair{images,L" 图"},std::pair{files,L" 文件"}})if(pair.first>0){if(!count.empty())count+=L" · ";count+=std::to_wstring(pair.first)+pair.second;}if(count.empty()&&chars>0)count=std::to_wstring(chars)+L" 字";NativeCardText(p,count,{86,9,width-42,31},9.5f);p.Icon(L"edit",{width-31,14,width-17,28},muted);
        auto body=NativeHandoffBody(width,height);auto layout=NativeHandoffFlow(p.graphics,view,width,height);auto error=Str(view,L"error");
        if(!layout.text){p.EmptyFrame(body,muted);p.Text(error.empty()?L"点这里写点什么":error,{body.left+8,body.top+10,body.right-8,body.bottom-18},12,muted,400,DWRITE_TEXT_ALIGNMENT_CENTER,0,true);}
        else{DWRITE_TEXT_METRICS metrics{};layout.text->GetMetrics(&metrics);bool fade=metrics.height>body.bottom-body.top;com_ptr<ID2D1LinearGradientBrush> opacity;p.c->PushAxisAlignedClip(body,D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            if(fade){D2D1_GRADIENT_STOP stops[]{{0,D2D1::ColorF(0xffffff,1)},{1,D2D1::ColorF(0xffffff,0)}};com_ptr<ID2D1GradientStopCollection> colors;check_hresult(p.c->CreateGradientStopCollection(stops,2,colors.put()));check_hresult(p.c->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties({0,body.bottom-26},{0,body.bottom}),colors.get(),opacity.put()));p.c->PushLayer(D2D1::LayerParameters(body,nullptr,D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,D2D1::Matrix3x2F::Identity(),1,opacity.get()),nullptr);}
            p.c->DrawTextLayout({body.left,body.top},layout.text.get(),p.Color(ink),D2D1_DRAW_TEXT_OPTIONS_CLIP);auto flow=view.GetNamedArray(L"flow");
            for(auto const &[position,index]:layout.markers){FLOAT x=0,y=0;DWRITE_HIT_TEST_METRICS hit{};if(FAILED(layout.text->HitTestTextPosition(position,FALSE,&x,&y,&hit)))continue;auto node=flow.GetAt(index).GetObject();if(body.top+hit.top>body.bottom)continue;auto r=D2D1::RectF(body.left+hit.left+1,body.top+hit.top+1,body.left+hit.left+hit.width-1,body.top+hit.top+hit.height-1);bool image=Str(node,L"kind")==L"image";p.Fill(r,p.theme.SectionFilm(),4);
                if(image){auto bitmap=NativeHandoffBitmap(window,Str(view,L"id")+L":"+std::to_wstring(index),Str(node,L"png"));if(bitmap)p.c->DrawBitmap(bitmap,r,Bool(node,L"missing")?.35f:1.f,D2D1_INTERPOLATION_MODE_LINEAR);else p.Icon(L"image",{r.left+6,r.top+3,r.right-6,r.bottom-3},muted);}
                else p.Text(Str(node,L"ext"),r,9,Bool(node,L"missing")?muted:ink,600,DWRITE_TEXT_ALIGNMENT_CENTER);
            }if(fade)p.c->PopLayer();p.c->PopAxisAlignedClip();
        }p.Icon(L"copy",{14,height-22,25,height-11},muted);p.Text(L"点击复制",{30,height-26,width-14,height-8},9.5f,muted);
    }
    if(Bool(view,L"editing"))p.c->DrawRoundedRectangle(D2D1::RoundedRect({1,1,width-1,height-1},9,9),p.Color(p.theme.Accent()),1.5f);
}
inline int Window::HandoffAttachmentHit(Tile const &tile,float x,float y){
    auto rect=RootBounds(tile.key);float w=static_cast<float>(Num(tile.card,L"columns",2))*96-8,h=static_cast<float>(Num(tile.card,L"rows",2))*96-8;if(h<100)return -1;float scale=(rect.right-rect.left)/w;auto body=NativeHandoffBody(w,h);float tx=(x-rect.left)/scale-body.left,ty=(y-rect.top)/scale-body.top;if(tx<0||ty<0||tx>body.right-body.left||ty>body.bottom-body.top)return -1;
    auto key=tile.key+L":"+std::to_wstring(app.model.handoffVersion)+L":"+std::to_wstring(w);if(key!=handoffHoverKey){auto layout=NativeHandoffFlow(app.graphics,NativeHandoffView(*this,tile),w,h);handoffHoverLayout=layout.text;handoffHoverMarkers=std::move(layout.markers);handoffHoverKey=key;}
    if(!handoffHoverLayout)return -1;BOOL trailing=FALSE,inside=FALSE;DWRITE_HIT_TEST_METRICS hit{};if(FAILED(handoffHoverLayout->HitTestPoint(tx,ty,&trailing,&inside,&hit))||!inside)return -1;
    for(auto const &[position,index]:handoffHoverMarkers)if(hit.textPosition==position){handoffHoverBounds={rect.left+(body.left+hit.left)*scale,rect.top+(body.top+hit.top)*scale,rect.left+(body.left+hit.left+hit.width)*scale,rect.top+(body.top+hit.top+hit.height)*scale};return index;}return -1;
}

inline bool Window::HandoffPointer(UINT message,float x,float y){
    if(message==WM_CAPTURECHANGED){bool had=!handoffPressKey.empty();handoffPressKey.clear();return had;}
    if(!handoffPressKey.empty()){
        if(message!=WM_LBUTTONUP)return true;auto key=handoffPressKey,action=handoffPressAction;handoffPressKey.clear();ReleaseCapture();auto tile=Find(key);if(!tile||Hit(x,y)!=key)return true;auto rect=RootBounds(key);float w=static_cast<float>(Num(tile->card,L"columns",2))*96-8,h=static_cast<float>(Num(tile->card,L"rows",2))*96-8,scale=(rect.right-rect.left)/w;
        if(NativeHandoffAction(*this,*tile,(x-rect.left)/scale,(y-rect.top)/scale,w,h)==action)CardAction(key,action);return true;
    }
    if(message!=WM_LBUTTONDOWN)return false;auto tile=Find(Hit(x,y));if(!tile||!tile->card||Str(tile->card,L"type")!=L"system.handoff")return false;auto rect=RootBounds(tile->key);float w=static_cast<float>(Num(tile->card,L"columns",2))*96-8,h=static_cast<float>(Num(tile->card,L"rows",2))*96-8,scale=(rect.right-rect.left)/w,tx=(x-rect.left)/scale,ty=(y-rect.top)/scale;
    if(h>=100&&!(tx>=w-40&&ty>=6&&ty<=38))return false;Select(tile->key);handoffPressKey=tile->key;handoffPressAction=NativeHandoffAction(*this,*tile,tx,ty,w,h);SetCapture(hwnd);return true;
}

inline void TrimNativeHandoffImages(App &app){
    std::set<std::wstring> live;
    for(auto const &value:app.model.handoffData){auto card=value.GetObject();auto flow=card.GetNamedArray(L"flow",JsonArray{});for(unsigned index=0;index<flow.Size()&&index<16;++index){auto node=flow.GetAt(index).GetObject();if(Str(node,L"kind")==L"image"&&!Str(node,L"png").empty())live.insert(Str(card,L"id")+L":"+std::to_wstring(index));}}
    for(auto it=app.handoffImages.begin();it!=app.handoffImages.end();)if(!live.contains(it->first)){app.handoffImageKeys.erase(it->first);it=app.handoffImages.erase(it);}else ++it;
}
