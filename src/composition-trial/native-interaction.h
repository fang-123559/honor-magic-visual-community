#pragma once
// One layout-derived region feeds hit testing, selection and the glass treatment.
// Coordinates are DIPs; a viewport clips scrollable regions before all uses.
struct NativeInteractionRegion {
    D2D1_RECT_F bounds{},viewport{};float radius=NativeMetrics::ControlRadius;bool enabled=true;
    D2D1_RECT_F Visible()const{return {std::max(bounds.left,viewport.left),std::max(bounds.top,viewport.top),std::min(bounds.right,viewport.right),std::min(bounds.bottom,viewport.bottom)};}
    bool Valid()const{auto r=Visible();return enabled&&r.right>r.left&&r.bottom>r.top;}
    bool Contains(float x,float y)const{auto r=Visible();return Valid()&&Inside(r,x,y);}
};
inline NativeInteractionRegion NativeRegion(D2D1_RECT_F bounds,D2D1_RECT_F viewport,float radius=NativeMetrics::ControlRadius,bool enabled=true){
    radius=std::max(0.f,std::min({radius,(bounds.right-bounds.left)/2,(bounds.bottom-bounds.top)/2}));return {bounds,viewport,radius,enabled};
}
// Two compositor brushes, no bitmap or CPU frame loop. The light is SDR and
// clipped to the same semantic target as hit testing; it never changes layout.
struct NativeHoverVisual {
    ContainerVisual root{nullptr};SpriteVisual film{nullptr},light{nullptr};ShapeVisual rim{nullptr};
    CompositionRoundedRectangleGeometry geometry{nullptr};CompositionRadialGradientBrush radial{nullptr};
    D2D1_RECT_F last{};bool active=false;float lastDpi=0;bool lastDark=false,lastMotion=false;D2D1_POINT_2F lastLight{};ULONGLONG lastLightAt=0;
    void Release(ContainerVisual const &){if(root&&root.Parent())root.Parent().Children().Remove(root);root=nullptr;film=nullptr;light=nullptr;rim=nullptr;geometry=nullptr;radial=nullptr;active=false;lastDpi=0;}
    void Sync(Compositor const &c,ContainerVisual const &parent,NativeInteractionRegion region,D2D1_POINT_2F point,float scale,bool motion,bool dark){
        if(!parent)return;
        if(!region.Valid()){if(root&&active){if(motion)NativeInteractionMotion::Opacity(c,root,0,140);else{root.StopAnimation(L"Opacity");root.Opacity(0);}}active=false;return;}
        auto color=[](BYTE a,BYTE r=255,BYTE g=255,BYTE b=255){return Windows::UI::Color{a,r,g,b};};
        if(root&&(root.Parent()!=parent||lastDpi!=scale||lastDark!=dark))Release(parent);
        bool fresh=!root;bool wasActive=active;
        if(fresh){root=c.CreateContainerVisual();film=c.CreateSpriteVisual();light=c.CreateSpriteVisual();geometry=c.CreateRoundedRectangleGeometry();
            auto gradient=c.CreateLinearGradientBrush();gradient.StartPoint({0,0});gradient.EndPoint({0,1});
            gradient.ColorStops().Append(c.CreateColorGradientStop(0,color(dark?53:100)));
            gradient.ColorStops().Append(c.CreateColorGradientStop(.42f,color(dark?16:28)));
            gradient.ColorStops().Append(c.CreateColorGradientStop(1,color(dark?23:40,255,238,220)));film.Brush(gradient);
            radial=c.CreateRadialGradientBrush();radial.ColorStops().Append(c.CreateColorGradientStop(0,color(dark?40:25,255,239,218)));radial.ColorStops().Append(c.CreateColorGradientStop(.6f,color(dark?12:8,255,239,218)));radial.ColorStops().Append(c.CreateColorGradientStop(1,color(0)));
            radial.MappingMode(CompositionMappingMode::Absolute);radial.EllipseRadius({120*scale,120*scale});light.Brush(radial);
            root.Clip(c.CreateGeometricClip(geometry));root.Children().InsertAtBottom(film);root.Children().InsertAtTop(light);rim=c.CreateShapeVisual();auto edge=c.CreateSpriteShape(geometry);edge.StrokeBrush(c.CreateColorBrush(color(dark?80:35,dark?255:44,dark?255:41,dark?255:39)));edge.StrokeThickness(1);rim.Shapes().Append(edge);root.Children().InsertAtTop(rim);parent.Children().InsertAtTop(root);lastDpi=scale;lastDark=dark;
        }
        auto r=region.Visible();float w=(r.right-r.left)*scale,h=(r.bottom-r.top)*scale;
        bool motionChanged=lastMotion!=motion;bool boundsChanged=fresh||!wasActive||motionChanged||memcmp(&r,&last,sizeof(r))!=0;
        if(boundsChanged&&!fresh&&motion){NativeInteractionMotion::Vector(c,root,L"Offset",{r.left*scale,r.top*scale,0},120);
            auto size=c.CreateVector2KeyFrameAnimation();size.InsertExpressionKeyFrame(0,L"this.StartingValue");size.InsertKeyFrame(1,{w,h},NativeInteractionMotion::Ease(c));size.Duration(std::chrono::milliseconds(120));geometry.StartAnimation(L"Size",size);
        }else if(boundsChanged){root.StopAnimation(L"Offset");root.Offset({r.left*scale,r.top*scale,0});geometry.StopAnimation(L"Size");geometry.Size({w,h});}
        root.Size({w,h});geometry.CornerRadius({region.radius*scale,region.radius*scale});
        // Keep the film large enough while the shared clip interpolates, including
        // brief gaps between rows. Re-entry retargets the existing visual.
        float fw=std::max(w,(last.right-last.left)*scale),fh=std::max(h,(last.bottom-last.top)*scale);
        film.Size({fw,fh});light.Size({fw,fh});rim.Size({fw,fh});
        // Animate the light, rather than repainting every row at pointer rate.
        float2 center=motion?float2{(point.x-r.left)*scale,(point.y-r.top)*scale}:float2{w/2,h/2};
        auto now=GetTickCount64();bool moveLight=fresh||!wasActive||motionChanged||boundsChanged||(motion&&now-lastLightAt>=16&&std::hypot(point.x-lastLight.x,point.y-lastLight.y)>.75f);
        if(moveLight&&motion&&wasActive){auto a=c.CreateVector2KeyFrameAnimation();a.InsertExpressionKeyFrame(0,L"this.StartingValue");a.InsertKeyFrame(1,center,NativeInteractionMotion::Ease(c));a.Duration(std::chrono::milliseconds(180));radial.StartAnimation(L"EllipseCenter",a);}
        else if(moveLight){radial.StopAnimation(L"EllipseCenter");radial.EllipseCenter(center);}
        if(moveLight){lastLight=point;lastLightAt=now;}
        if(!active&&motion){if(fresh)root.Opacity(0);NativeInteractionMotion::Opacity(c,root,1,140);}else if(!motion||fresh){root.StopAnimation(L"Opacity");root.Opacity(1);}
        active=true;last=r;lastMotion=motion;
    }
};
