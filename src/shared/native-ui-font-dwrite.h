#pragma once
#include "native-ui-font.h"
#include <dwrite_3.h>
#include <winrt/base.h>

namespace NativeUiFont
{
// Retained with the graphics factory. Text formats share this collection.
// Font parsing and registration never happen during frame rendering.
struct DirectWriteFonts
{
    winrt::com_ptr<IDWriteFontCollection> collection;
    std::wstring family;

    static winrt::com_ptr<IDWriteFontCollection> Load(
        IDWriteFactory *factory, std::wstring const &directory)
    {
        using winrt::com_ptr;
        if (directory.empty()) return {};
        com_ptr<IDWriteFactory5> modern;
        if (FAILED(factory->QueryInterface(__uuidof(IDWriteFactory5), modern.put_void()))) return {};
        com_ptr<IDWriteFontSetBuilder1> builder;
        if (FAILED(modern->CreateFontSetBuilder(builder.put()))) return {};
        bool regular = false;
        for (auto name : Files) {
            com_ptr<IDWriteFontFile> file;
            auto path = directory + name;
            if (FAILED(factory->CreateFontFileReference(path.c_str(), nullptr, file.put()))) continue;
            if (SUCCEEDED(builder->AddFontFile(file.get())) && name == Files.front()) regular = true;
        }
        if (!regular) return {};
        com_ptr<IDWriteFontSet> set;
        com_ptr<IDWriteFontCollection1> result;
        if (FAILED(builder->CreateFontSet(set.put())) ||
            FAILED(modern->CreateFontCollectionFromFontSet(set.get(), result.put()))) return {};
        UINT32 index = 0;
        BOOL exists = FALSE;
        if (FAILED(result->FindFamilyName(L"HONOR Sans CN", &index, &exists)) || !exists) return {};
        return result.as<IDWriteFontCollection>();
    }

    void Initialize(IDWriteFactory *factory)
    {
        collection = Load(factory, FontDirectory());
        if (collection) { family = L"HONOR Sans CN"; return; }

        winrt::com_ptr<IDWriteFontCollection> system;
        if (SUCCEEDED(factory->GetSystemFontCollection(system.put()))) {
            for (auto candidate : {L"HONOR Sans CN", L"HONOR Sans Design"}) {
                UINT32 index = 0;
                BOOL exists = FALSE;
                if (SUCCEEDED(system->FindFamilyName(candidate, &index, &exists)) && exists) {
                    family = candidate;
                    return;
                }
            }
        }
        family = WindowsFamily();
    }
};
}
