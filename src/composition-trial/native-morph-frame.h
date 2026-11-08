#pragma once
// Retained rounded frame: geometry changes, raster text/icons never stretch.
struct NativeMorphFrame {
    ContainerVisual root{nullptr};ShapeVisual rim{nullptr};SpriteVisual backdrop{nullptr};
    CompositionRoundedRectangleGeometry clip{nullptr},outer{nullptr},inner{nullptr};
    float unit=1;
    CompositionColorBrush fill{nullptr};CompositionLinearGradientBrush edge{nullptr},bevel{nullptr};
    static Windows::UI::Color Color(D2D1_COLOR_F c){return {static_cast<BYTE>(c.a*255),static_cast<BYTE>(c.r*255),static_cast<BYTE>(c.g*255),static_cast<BYTE>(c.b*255)};}
    void Release(){if(root&&root.Parent())root.Parent().Children().Remove(root);root=nullptr;rim=nullptr;backdrop=nullptr;clip=nullptr;outer=nullptr;inner=nullptr;fill=nullptr;edge=nullptr;bevel=nullptr;}
    void Create(Graphics &g,ContainerVisual const &parent,float2 maximum,float2 size,float radius,D2D1_COLOR_F tint,bool dark,CompositionBrush const &blur,float pixelScale=1){
        unit=pixelScale;
        auto c=g.compositor;root=c.CreateContainerVisual();root.Size(maximum);parent.Children().InsertAtTop(root);
        clip=c.CreateRoundedRectangleGeometry();clip.Size(size);clip.CornerRadius({radius,radius});root.Clip(c.CreateGeometricClip(clip));
        if(blur){backdrop=c.CreateSpriteVisual();backdrop.Size(maximum);backdrop.Brush(blur);root.Children().InsertAtBottom(backdrop);}
        rim=c.CreateShapeVisual();rim.Size(maximum);root.Children().InsertAtTop(rim);
        auto gradient=[&](bool reverse,float opacity){auto b=c.CreateLinearGradientBrush();b.MappingMode(CompositionMappingMode::Absolute);b.StartPoint(reverse?float2{size.x,0}:float2{0,0});b.EndPoint(reverse?float2{0,size.y}:size);
            for(auto stop:Preview70Theme{dark}.Contour()){stop.color.a*=opacity;b.ColorStops().Append(c.CreateColorGradientStop(stop.position,Color(stop.color)));}return b;};
        edge=gradient(false,1);bevel=gradient(true,.38f);fill=c.CreateColorBrush(Color(tint));
        outer=c.CreateRoundedRectangleGeometry();outer.Offset({.5f*unit,.5f*unit});outer.Size({std::max(0.f,size.x-unit),std::max(0.f,size.y-unit)});outer.CornerRadius({radius,radius});
        auto shape=c.CreateSpriteShape(outer);shape.FillBrush(fill);shape.StrokeBrush(edge);shape.StrokeThickness(unit);rim.Shapes().Append(shape);
        inner=c.CreateRoundedRectangleGeometry();inner.Offset({1.4f*unit,1.4f*unit});inner.Size({std::max(0.f,size.x-2.8f*unit),std::max(0.f,size.y-2.8f*unit)});inner.CornerRadius({std::max(0.f,radius-unit),std::max(0.f,radius-unit)});
        auto second=c.CreateSpriteShape(inner);second.StrokeBrush(bevel);second.StrokeThickness(.65f*unit);rim.Shapes().Append(second);
    }
    template<class Target>static void Vector2(Compositor const &c,Target const &object,wchar_t const *key,float2 end,int ms){auto a=c.CreateVector2KeyFrameAnimation();a.InsertExpressionKeyFrame(0,L"this.StartingValue");a.InsertKeyFrame(1,end,NativeInteractionMotion::Ease(c));a.Duration(std::chrono::milliseconds(ms));object.StartAnimation(key,a);}
    void Animate(Compositor const &c,float2 end,float radius,D2D1_COLOR_F tint,int ms){
        Vector2(c,clip,L"Size",end,ms);Vector2(c,clip,L"CornerRadius",{radius,radius},ms);
        Vector2(c,outer,L"Size",{std::max(0.f,end.x-unit),std::max(0.f,end.y-unit)},ms);Vector2(c,outer,L"CornerRadius",{radius,radius},ms);
        Vector2(c,inner,L"Size",{std::max(0.f,end.x-2.8f*unit),std::max(0.f,end.y-2.8f*unit)},ms);Vector2(c,inner,L"CornerRadius",{std::max(0.f,radius-unit),std::max(0.f,radius-unit)},ms);
        Vector2(c,edge,L"EndPoint",end,ms);Vector2(c,bevel,L"StartPoint",{end.x,0},ms);Vector2(c,bevel,L"EndPoint",{0,end.y},ms);
        auto color=c.CreateColorKeyFrameAnimation();color.InsertExpressionKeyFrame(0,L"this.StartingValue");color.InsertKeyFrame(1,Color(tint),NativeInteractionMotion::Ease(c));color.Duration(std::chrono::milliseconds(ms));fill.StartAnimation(L"Color",color);
    }
};
