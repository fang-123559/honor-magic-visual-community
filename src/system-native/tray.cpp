#include "providers.h"
#include <UIAutomation.h>
namespace magic::system
{
namespace
{
std::wstring BoundedBstr(BSTR text)
{
    return text ? std::wstring(text, std::min<UINT>(SysStringLen(text), 512)) : L"";
}
std::wstring CachedText(IUIAutomationElement *element, PROPERTYID property)
{
    VARIANT value{};
    std::wstring text;
    if (SUCCEEDED(element->GetCachedPropertyValue(property, &value)) && value.vt == VT_BSTR)
        text = BoundedBstr(value.bstrVal);
    VariantClear(&value);
    return text;
}
} // namespace
JsonObject InspectWindowsTray()
{
    winrt::com_ptr<IUIAutomation> automation;
    winrt::check_hresult(CoCreateInstance(CLSID_CUIAutomation8, nullptr, CLSCTX_INPROC_SERVER, __uuidof(IUIAutomation),
                                          automation.put_void()));
    if (auto settings = automation.try_as<IUIAutomation2>())
    {
        winrt::check_hresult(settings->put_AutoSetFocus(FALSE));
        winrt::check_hresult(settings->put_ConnectionTimeout(300));
        winrt::check_hresult(settings->put_TransactionTimeout(500));
    }
    winrt::com_ptr<IUIAutomationCacheRequest> cache;
    winrt::check_hresult(automation->CreateCacheRequest(cache.put()));
    for (auto property : {UIA_NamePropertyId, UIA_AutomationIdPropertyId, UIA_ClassNamePropertyId,
                          UIA_ControlTypePropertyId, UIA_BoundingRectanglePropertyId, UIA_IsOffscreenPropertyId})
        winrt::check_hresult(cache->AddProperty(property));
    winrt::com_ptr<IUIAutomationCondition> all;
    winrt::check_hresult(automation->CreateTrueCondition(all.put()));
    JsonArray surfaces;
    for (auto cls : {L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd", L"NotifyIconOverflowWindow",
                     L"TopLevelWindowForOverflowXamlIsland"})
    {
        HWND window = nullptr;
        unsigned instances = 0;
        while ((window = FindWindowExW(nullptr, window, cls, nullptr)) != nullptr && ++instances <= 16)
        {
            DWORD pid = 0;
            GetWindowThreadProcessId(window, &pid);
            Handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
            if (!process.value)
                continue;
            auto image = Image(process);
            std::wstring expected(32768, L'\0');
            UINT n = GetWindowsDirectoryW(expected.data(), 32768);
            expected.resize(n);
            expected += L"\\explorer.exe";
            if (_wcsicmp(image.c_str(), expected.c_str()) != 0)
                continue;
            winrt::com_ptr<IUIAutomationElement> root;
            winrt::check_hresult(automation->ElementFromHandle(window, root.put()));
            winrt::com_ptr<IUIAutomationElementArray> elements;
            winrt::check_hresult(
                root->FindAllBuildCache(TreeScope_Descendants, all.get(), cache.get(), elements.put()));
            int count = 0;
            winrt::check_hresult(elements->get_Length(&count));
            JsonArray rows;
            for (int i = 0; i < std::min(count, 256); i++)
            {
                winrt::com_ptr<IUIAutomationElement> element;
                if (FAILED(elements->GetElement(i, element.put())))
                    continue;
                JsonObject row;
                Text(row, L"name", CachedText(element.get(), UIA_NamePropertyId));
                Text(row, L"automationId", CachedText(element.get(), UIA_AutomationIdPropertyId));
                Text(row, L"class", CachedText(element.get(), UIA_ClassNamePropertyId));
                CONTROLTYPEID type = 0;
                BOOL offscreen = TRUE;
                RECT rect{};
                element->get_CachedControlType(&type);
                element->get_CachedIsOffscreen(&offscreen);
                element->get_CachedBoundingRectangle(&rect);
                Number(row, L"controlType", type);
                Flag(row, L"offscreen", offscreen != FALSE);
                Number(row, L"left", rect.left);
                Number(row, L"top", rect.top);
                Number(row, L"right", rect.right);
                Number(row, L"bottom", rect.bottom);
                rows.Append(row);
            }
            JsonObject surface;
            Text(surface, L"class", cls);
            Text(surface, L"hwnd", std::to_wstring(reinterpret_cast<uintptr_t>(window)));
            Flag(surface, L"visible", IsWindowVisible(window) != FALSE);
            surface.Insert(L"elements", rows);
            surfaces.Append(surface);
        }
    }
    JsonObject result;
    Flag(result, L"readOnly", true);
    result.Insert(L"surfaces", surfaces);
    return result;
}
} // namespace magic::system
