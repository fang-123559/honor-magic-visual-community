#pragma once
#include <d2d1svg.h>
#include "native-toolbar-icons.h"
#include "native-honor-icons.h"
#include "native-shell-strings.h"

// Values come from the bundled fancy-toolbar global.css/theme and dnd-kit.
// This class contains no frame timer: Composition animates cached item surfaces.
struct NativeToolbarVisual {
    ContainerVisual group{nullptr},hoverLayer{nullptr};
    float modelOpacity=1;
    float hoverScale=1;float3 hoverOffset{};ULONGLONG hoverRetireAt=0;
    SpriteVisual visual{nullptr};
    CompositionDrawingSurface surface{nullptr};
    SpriteVisual indicator{nullptr};CompositionColorBrush indicatorBrush{nullptr};
    ShapeVisual attention{nullptr};CompositionRoundedRectangleGeometry attentionGeometry{nullptr};CompositionColorBrush attentionBrush{nullptr};bool attentionMotion=false;
    Windows::UI::Color indicatorColor{};float2 indicatorSize{};std::wstring attentionState;
    ContainerVisual mediaClip{nullptr};SpriteVisual mediaTitle{nullptr};CompositionDrawingSurface mediaSurface{nullptr};
    std::wstring mediaKey;size_t mediaBytes=0;
    ShapeVisual mediaBody{nullptr};CompositionRoundedRectangleGeometry mediaGeometry{nullptr};CompositionColorBrush mediaBrush{nullptr};
    float2 mediaSize{};ULONGLONG mediaResizeAt=0;
    std::wstring rasterKey,extensionMotionKey;
    D2D1_RECT_F rect{};
    size_t bytes=0;
    ULONGLONG retireAt=0;
};
struct NativeToolbarSvg {
    std::map<std::wstring,com_ptr<ID2D1SvgDocument>> documents;
    bool Draw(ID2D1DeviceContext *context,std::wstring const &requested,D2D1_RECT_F rect,D2D1_COLOR_F ink) {
        auto name=HonorReferenceGlyph(requested);
        com_ptr<ID2D1DeviceContext> c;c.copy_from(context);
        auto svgContext=c.try_as<ID2D1DeviceContext5>();if(!svgContext)return false;
        auto found=documents.find(name);
        if(found==documents.end()) {
            char const *source=nullptr;
            for(auto const &asset:HonorSvgAssets)if(name==asset.name){source=asset.svg;break;}
            if(!source)for(auto const &asset:ToolbarSvgAssets)if(name==asset.name){source=asset.svg;break;}
            if(!source)return false;
            com_ptr<IStream> stream;stream.attach(SHCreateMemStream(reinterpret_cast<BYTE const*>(source),static_cast<UINT>(strlen(source))));
            com_ptr<ID2D1SvgDocument> doc;
            if(FAILED(svgContext->CreateSvgDocument(stream.get(),{24,24},doc.put())))return false;
            if(documents.size()>=64)documents.clear();found=documents.emplace(name,std::move(doc)).first;
        }
        com_ptr<ID2D1SvgElement> root;found->second->GetRoot(root.put());
        if(root)root->SetAttributeValue(L"color",D2D1_SVG_ATTRIBUTE_POD_TYPE_COLOR,&ink,sizeof(ink));
        D2D1_MATRIX_3X2_F transform;context->GetTransform(&transform);
        context->SetTransform(D2D1::Matrix3x2F::Scale((rect.right-rect.left)/24,(rect.bottom-rect.top)/24)*D2D1::Matrix3x2F::Translation(rect.left,rect.top)*transform);
        svgContext->DrawSvgDocument(found->second.get());context->SetTransform(transform);return true;
    }
};
#include "native-clock-format.h"
inline auto &NativeShellLocaleCache(){static std::map<std::wstring,JsonObject> cache;return cache;}
inline std::wstring NativeShellText(std::wstring language,std::wstring const &key) {
    auto &cache=NativeShellLocaleCache();
    std::transform(language.begin(),language.end(),language.begin(),::towlower);
    auto select=[&](std::wstring const &lang)->std::wstring{
        auto found=cache.find(lang);if(found!=cache.end())return Str(found->second,key.c_str());
        auto asset=std::find_if(std::begin(NativeShellLocaleAssets),std::end(NativeShellLocaleAssets),[&](auto const &v){return lang==v.language;});if(asset==std::end(NativeShellLocaleAssets))return L"";
        if(cache.size()>=2)cache.clear();auto parsed=JsonObject::Parse(to_hstring(asset->json));cache.emplace(lang,parsed);return Str(parsed,key.c_str());
    };
    auto value=select(language);if(value.empty()){auto dash=language.find(L'-');if(dash!=language.npos)value=select(language.substr(0,dash));}if(value.empty())value=select(L"en");return value.empty()?key:value;

}
