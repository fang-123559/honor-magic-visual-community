#pragma once
#include <windows.h>
#include <string>
#include <array>
#include <vector>

// Resolve once per process. Packaged fonts are registered privately for GDI.
namespace NativeUiFont
{
inline constexpr std::array<wchar_t const *, 3> Files{
    L"HONORSansCN-Regular.ttf", L"HONORSansCN-Medium.ttf", L"HONORSansCN-Light.ttf"};

inline std::wstring FontDirectory()
{
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    path.resize(length);
    auto separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : path.substr(0, separator + 1) + L"fonts\\";
}

inline std::wstring WindowsFamily()
{
    NONCLIENTMETRICSW metrics{};
    metrics.cbSize = sizeof(metrics);
    if (SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0) && metrics.lfMessageFont.lfFaceName[0])
        return metrics.lfMessageFont.lfFaceName;
    LOGFONTW stock{};
    if (GetObjectW(GetStockObject(DEFAULT_GUI_FONT), sizeof(stock), &stock) && stock.lfFaceName[0])
        return stock.lfFaceName;
    return L"Segoe UI";
}

struct PrivateFonts
{
    std::vector<std::wstring> loaded;
    PrivateFonts()
    {
        auto directory = FontDirectory();
        if (directory.empty()) return;
        for (auto name : Files) {
            auto path = directory + name;
            if (AddFontResourceExW(path.c_str(), FR_PRIVATE, nullptr) > 0)
                loaded.push_back(std::move(path));
        }
    }
    ~PrivateFonts()
    {
        for (auto const &path : loaded) RemoveFontResourceExW(path.c_str(), FR_PRIVATE, nullptr);
    }
    PrivateFonts(PrivateFonts const &) = delete;
    PrivateFonts &operator=(PrivateFonts const &) = delete;
};

inline PrivateFonts const &Resources()
{
    static PrivateFonts fonts;
    return fonts;
}

struct Probe { wchar_t const *name; bool found=false; };
inline int CALLBACK Match(LOGFONTW const *font, TEXTMETRICW const *, DWORD, LPARAM data)
{
    auto requested = reinterpret_cast<Probe *>(data);
    if (_wcsicmp(font->lfFaceName, requested->name) != 0) return 1;
    requested->found = true; // Exact family, not a GDI substitution.
    return 0;
}
inline std::wstring const &Family()
{
    static std::wstring const family = [] {
        (void)Resources();
        HDC dc = GetDC(nullptr);
        if (dc) {
            for (auto candidate : {L"HONOR Sans CN", L"HONOR Sans Design"}) {
                LOGFONTW query{};query.lfCharSet=DEFAULT_CHARSET;
                wcscpy_s(query.lfFaceName, candidate);
                Probe result{candidate};
                EnumFontFamiliesExW(dc,&query,Match,reinterpret_cast<LPARAM>(&result),0);
                if (result.found) { ReleaseDC(nullptr,dc);return std::wstring(candidate); }
            }
            ReleaseDC(nullptr,dc);
        }
        return WindowsFamily();
    }();
    return family;
}
}
