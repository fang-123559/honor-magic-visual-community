#pragma once
// Finite interaction motion. Composition owns interpolation; callers only react
// to input/state events and use one-shot cleanup, never a CPU frame loop.
namespace NativeInteractionMotion {
inline constexpr int OpenMs=180, CloseMs=140, DockMs=150, PageMs=300,DockBodyMs=820;
inline float DockSpring(float t){
    constexpr float keys[][2]={{0,0},{.025f,.0344f},{.05f,.1228f},{.08f,.2721f},{.11f,.4414f},{.145f,.6357f},{.185f,.8269f},{.23f,.9838f},{.28f,1.0834f},{.335f,1.1192f},{.395f,1.1036f},{.46f,1.0617f},{.53f,1.0195f},{.605f,.9932f},{.685f,.9857f},{.77f,.9900f},{.855f,.9966f},{.93f,1.0003f},{1,1}};
    t=std::clamp(t,0.f,1.f);for(size_t i=1;i<std::size(keys);i++)if(t<=keys[i][0]){auto a=keys[i-1],b=keys[i];return a[1]+(b[1]-a[1])*(t-a[0])/(b[0]-a[0]);}return 1;
}
inline auto DockSpringVector(Compositor const &c,float3 from,float3 to){auto a=c.CreateVector3KeyFrameAnimation();for(int i=0;i<=40;i++){float p=i/40.f,k=DockSpring(p);a.InsertKeyFrame(p,from+(to-from)*k,c.CreateLinearEasingFunction());}a.Duration(std::chrono::milliseconds(DockBodyMs));return a;}
inline constexpr float DockReach=120, DockGain=.34f, DockLift=26, PageTravel=96;
inline auto Ease(Compositor const &c){return c.CreateCubicBezierEasingFunction({.22f,.61f},{.36f,1});}
inline auto DockEase(Compositor const &c){return c.CreateCubicBezierEasingFunction({.2f,.7f},{.3f,1});}
inline void Vector(Compositor const &c,Visual const &v,wchar_t const *property,float3 to,int ms,bool dock=false){
    auto a=c.CreateVector3KeyFrameAnimation();a.InsertExpressionKeyFrame(0,L"this.StartingValue");
    a.InsertKeyFrame(1,to,dock?DockEase(c):Ease(c));a.Duration(std::chrono::milliseconds(ms));v.StartAnimation(property,a);
}
inline void Opacity(Compositor const &c,Visual const &v,float to,int ms){
    auto a=c.CreateScalarKeyFrameAnimation();a.InsertExpressionKeyFrame(0,L"this.StartingValue");
    a.InsertKeyFrame(1,to,Ease(c));a.Duration(std::chrono::milliseconds(ms));v.StartAnimation(L"Opacity",a);
}
struct DockPose {float scale=1;float3 offset{},origin{};float opacity=1;};
inline DockPose Panel(float w,float h,bool picker,bool above,float amount,bool modal=false){
    DockPose p;p.scale=1-(picker?.08f:.12f)*amount;p.offset={0,(picker?16.f:8.f)*(above||picker?1.f:-1.f)*amount,0};
    p.origin={w/2,picker||modal?h/2:above?h:0,0};p.opacity=1-amount;return p;
}
inline DockPose Page(int direction,bool stationary,float progress){
    DockPose p;p.offset={stationary?0.f:direction*PageTravel*(1-progress),0,0};p.opacity=.55f+.45f*progress;return p;
}
inline DockPose Dock(float w,float h,float axis,float center,float icon,std::wstring const &side,bool active){
    float t=active?std::clamp(1-std::abs(axis-center)/DockReach,0.f,1.f):0;
    // Bound the growth of wide media/title widgets to the same icon-sized halo.
    float gain=DockGain*std::min(1.f,icon/std::max(w,h));DockPose p;p.scale=1+gain*t*t;
    float lift=DockLift*(p.scale-1);p.origin={w/2,h,0};p.offset={0,-lift,0};
    if(side==L"Top"){p.origin={w/2,0,0};p.offset={0,lift,0};}
    else if(side==L"Left"){p.origin={0,h/2,0};p.offset={lift,0,0};}
    else if(side==L"Right"){p.origin={w,h/2,0};p.offset={-lift,0,0};}return p;
}
inline D2D1_RECT_F Bounds(D2D1_RECT_F r,DockPose const &p){
    float x=r.left+p.origin.x,y=r.top+p.origin.y;
    return {x+(r.left-x)*p.scale+p.offset.x,y+(r.top-y)*p.scale+p.offset.y,
            x+(r.right-x)*p.scale+p.offset.x,y+(r.bottom-y)*p.scale+p.offset.y};
}
// Same Bezier as the compositor, for developer frame exports only.
inline float Progress(float time,bool dock=false){
    time=std::clamp(time,0.f,1.f);float lo=0,hi=1;
    auto value=[](float t,float a,float b){float u=1-t;return 3*u*u*t*a+3*u*t*t*b+t*t*t;};
    for(int i=0;i<20;i++){float m=(lo+hi)/2;if(value(m,dock?.2f:.22f,dock?.3f:.36f)<time)lo=m;else hi=m;}
    return value((lo+hi)/2,dock?.7f:.61f,1);
}
}
