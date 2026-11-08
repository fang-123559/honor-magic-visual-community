#pragma once
namespace NativePreviewMotion {
struct Options{int duration=150;float scale=.88f,shift=8;int Delay()const{return duration/2;}int Fade()const{return std::clamp(static_cast<int>(duration*.3f),90,160);}};
inline Options Read(JsonObject const &dock){Options o;auto value=dock.TryLookup(L"previewMotion");if(!value||value.ValueType()!=JsonValueType::Object)return o;auto v=value.GetObject();o.duration=static_cast<int>(std::clamp(Num(v,L"duration",150),80.,600.));o.scale=static_cast<float>(std::clamp(Num(v,L"scale",.88),.6,1.));o.shift=static_cast<float>(std::clamp(Num(v,L"shift",8),0.,24.));return o;}
inline float3 Shift(std::wstring const &side,float amount){if(side==L"Left")return {-amount,0,0};if(side==L"Right")return {amount,0,0};return {0,side==L"Top"?-amount:amount,0};}
// Convex envelope of the complete icon and the facing outer corners of the
// complete preview group. Extend to the icon's far edge, so diagonal travel
// over neighbouring Dock slots remains safe. All coordinates are physical;
// the invisible corridor never creates a window or intercepts input.
inline bool Bridge(POINT point,RECT icon,RECT panel,std::wstring const &side){
    std::array<POINT,4> q;
    if(side==L"Top"){if(icon.bottom>panel.top)return false;q={POINT{icon.left,icon.top},POINT{icon.right,icon.top},POINT{panel.right,panel.top},POINT{panel.left,panel.top}};}
    else if(side==L"Left"){if(icon.right>panel.left)return false;q={POINT{icon.left,icon.top},POINT{panel.left,panel.top},POINT{panel.left,panel.bottom},POINT{icon.left,icon.bottom}};}
    else if(side==L"Right"){if(panel.right>icon.left)return false;q={POINT{panel.right,panel.top},POINT{icon.right,icon.top},POINT{icon.right,icon.bottom},POINT{panel.right,panel.bottom}};}
    else{if(panel.bottom>icon.top)return false;q={POINT{panel.left,panel.bottom},POINT{panel.right,panel.bottom},POINT{icon.right,icon.bottom},POINT{icon.left,icon.bottom}};}
    long long sign=0;for(int i=0;i<4;++i){auto a=q[i],b=q[(i+1)%4];long long cross=(b.x-a.x)*static_cast<long long>(point.y-a.y)-(b.y-a.y)*static_cast<long long>(point.x-a.x);if(cross){long long next=cross>0?1:-1;if(sign&&next!=sign)return false;sign=next;}}return sign!=0;
}
}
