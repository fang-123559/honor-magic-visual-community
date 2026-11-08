#pragma once
#include "native-official-panel-icons.h"
// Bundled monochrome HONOR SVG outlines. Parsed on demand, no image polling.
struct NativeOfficialPanelSvg {
    std::map<std::wstring,com_ptr<ID2D1SvgDocument>> documents;
    bool Draw(ID2D1DeviceContext *c,std::wstring const &name,D2D1_RECT_F rect,D2D1_COLOR_F ink){
        com_ptr<ID2D1DeviceContext5> svg;if(FAILED(c->QueryInterface(svg.put())))return false;
        auto found=documents.find(name);if(found==documents.end()){
            auto asset=std::find_if(std::begin(OfficialPanelAssets),std::end(OfficialPanelAssets),[&](auto const &v){return name==v.name;});if(asset==std::end(OfficialPanelAssets))return false;
            com_ptr<IStream> stream;stream.attach(SHCreateMemStream(reinterpret_cast<BYTE const*>(asset->svg),static_cast<UINT>(strlen(asset->svg))));
            com_ptr<ID2D1SvgDocument> doc;if(FAILED(svg->CreateSvgDocument(stream.get(),{24,24},doc.put())))return false;
            found=documents.emplace(name,std::move(doc)).first;
        }
        com_ptr<ID2D1SvgElement> root;found->second->GetRoot(root.put());if(root)root->SetAttributeValue(L"color",D2D1_SVG_ATTRIBUTE_POD_TYPE_COLOR,&ink,sizeof(ink));
        D2D1_MATRIX_3X2_F old;c->GetTransform(&old);c->SetTransform(D2D1::Matrix3x2F::Scale((rect.right-rect.left)/24,(rect.bottom-rect.top)/24)*D2D1::Matrix3x2F::Translation(rect.left,rect.top)*old);
        svg->DrawSvgDocument(found->second.get());c->SetTransform(old);return true;
    }
};
