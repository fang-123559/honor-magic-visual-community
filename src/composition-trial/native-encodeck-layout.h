#pragma once
// Fixed four-slot layout. Source indices stay bound to actual original controls.
inline constexpr wchar_t const *EncoModes[]={L"NoiseCancellation",L"Adaptive",L"Transparency",L"Off"};
inline constexpr wchar_t const *EncoLabels[]={L"降噪",L"自适应",L"通透",L"关闭"};
inline D2D1_RECT_F EncoModeRect(unsigned i){float x=140+i*55.f;return{x,76,x+49,126};}
inline D2D1_RECT_F EncoLevelRect(unsigned i){float x=140+i*55.f;return{x,147,x+49,174};}
inline int EncoModeIndex(JsonArray const &modes,unsigned slot){for(unsigned i=0;i<modes.Size()&&i<4;i++)if(Str(modes.GetAt(i).GetObject(),L"mode")==EncoModes[slot])return static_cast<int>(i);return -1;}
inline bool EncoContains(D2D1_RECT_F r,float x,float y){return x>=r.left&&x<r.right&&y>=r.top&&y<r.bottom;}
inline std::pair<std::wstring,int> EncoAction(JsonObject const &data,float x,float y){
 if(EncoContains({338,10,366,42},x,y))return{L"encodeck-open",-1};
 if(!data||!Bool(data,L"available"))return{L"encodeck-connect",-1};
 if(!Bool(data,L"controlReady")||!Bool(data,L"canSetNoise"))return{};
 auto modes=data.GetNamedArray(L"modes",JsonArray{}),levels=data.GetNamedArray(L"levels",JsonArray{});
 for(unsigned i=0;i<4;i++){int index=EncoModeIndex(modes,i);if(index>=0&&Bool(modes.GetAt(index).GetObject(),L"enabled")&&EncoContains(EncoModeRect(i),x,y))return{L"encodeck-mode",index};}
 for(unsigned i=0;i<levels.Size()&&i<4;i++){auto level=levels.GetAt(i).GetObject();int index=static_cast<int>(Num(level,L"index",-1));if(index>=0&&Bool(level,L"enabled")&&EncoContains(EncoLevelRect(i),x,y))return{L"encodeck-level",index};}return{};
}

inline void EncoOpenIcon(NativePaint &p,D2D1_RECT_F r,D2D1_COLOR_F ink){
 float x=r.left,y=r.top,s=(r.right-r.left)/16;auto line=[&](float a,float b,float c,float d){p.Line(x+a*s,y+b*s,x+c*s,y+d*s,ink,1.2f*s);};
 line(9,2,14,2);line(14,2,14,7);line(14,2,6,10);line(7,3,2,3);line(2,3,2,14);line(2,14,13,14);line(13,14,13,9);
}
inline void EncoIcon(NativePaint &p,unsigned mode,D2D1_RECT_F r,D2D1_COLOR_F ink){
 float x=r.left,y=r.top,s=(r.right-r.left)/16;auto line=[&](float a,float b,float c,float d){p.Line(x+a*s,y+b*s,x+c*s,y+d*s,ink,1.2f*s);};
 if(mode==0){
  com_ptr<ID2D1Factory> f;p.c->GetFactory(f.put());com_ptr<ID2D1PathGeometry> path;check_hresult(f->CreatePathGeometry(path.put()));com_ptr<ID2D1GeometrySink> sink;check_hresult(path->Open(sink.put()));sink->BeginFigure({x+2*s,y+10*s},D2D1_FIGURE_BEGIN_HOLLOW);sink->AddLine({x+2*s,y+7*s});sink->AddBezier({{x+2*s,y-s},{x+14*s,y-s},{x+14*s,y+7*s}});sink->AddLine({x+14*s,y+10*s});sink->EndFigure(D2D1_FIGURE_END_OPEN);check_hresult(sink->Close());p.c->DrawGeometry(path.get(),p.Color(ink),1.2f*s);
  p.c->DrawRoundedRectangle(D2D1::RoundedRect({x+s,y+7*s,x+5*s,y+14*s},s,s),p.Color(ink),s);p.c->DrawRoundedRectangle(D2D1::RoundedRect({x+11*s,y+7*s,x+15*s,y+14*s},s,s),p.Color(ink),s);
 }else if(mode==3){p.c->DrawEllipse({{x+8*s,y+8*s},6*s,6*s},p.Color(ink),1.2f*s);line(4,4,12,12);}
 else if(mode==1){p.c->DrawEllipse({{x+8*s,y+8*s},5*s,5*s},p.Color(ink),1.2f*s);line(8,0,8,4);line(8,12,8,16);line(0,8,4,8);line(12,8,16,8);}
 else{line(2,6,5,6);line(5,6,8,3);line(8,3,8,13);line(8,13,5,10);line(5,10,2,10);line(2,10,2,6);line(11,5,13,8);line(13,8,11,11);line(14,2,16,8);line(16,8,14,14);}
}
