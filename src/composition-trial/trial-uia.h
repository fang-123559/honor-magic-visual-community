#pragma once
#include "trial.h"
// UI Automation fragments include virtual items; scrolling and focus materialize
// only the visible drawing range. Providers retain a detached lifetime token,
// never a raw Window after destruction. COM routes STA calls to the UI thread.
class TrialProvider final : public IRawElementProviderSimple,
                            public IRawElementProviderFragment,
                            public IRawElementProviderFragmentRoot,
                            public IInvokeProvider,
                            public ISelectionItemProvider,
                            public ISelectionProvider,
                            public IScrollItemProvider
{
    std::atomic<ULONG> references = 1;
    std::shared_ptr<ProviderState> state;
    std::wstring key;
    Window *Owner()
    {
        return Live(state);
    }

  public:
    TrialProvider(std::shared_ptr<ProviderState> value, std::wstring item)
        : state(std::move(value)), key(std::move(item))
    {
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IRawElementProviderSimple))
            *out = static_cast<IRawElementProviderSimple *>(this);
        else if (iid == __uuidof(IRawElementProviderFragment))
            *out = static_cast<IRawElementProviderFragment *>(this);
        else if (iid == __uuidof(IRawElementProviderFragmentRoot) && key.empty())
            *out = static_cast<IRawElementProviderFragmentRoot *>(this);
        else if (iid == __uuidof(IInvokeProvider) && !key.empty())
            *out = static_cast<IInvokeProvider *>(this);
        else if (iid == __uuidof(ISelectionItemProvider) && !key.empty())
            *out = static_cast<ISelectionItemProvider *>(this);
        else if (iid == __uuidof(IScrollItemProvider) && !key.empty())
            *out = static_cast<IScrollItemProvider *>(this);
        else if (iid == __uuidof(ISelectionProvider) && key.empty())
            *out = static_cast<ISelectionProvider *>(this);
        if (!*out)
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++references;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG value = --references;
        if (!value)
            delete this;
        return value;
    }
    HRESULT STDMETHODCALLTYPE get_ProviderOptions(ProviderOptions *out) override
    {
        if (!out)
            return E_POINTER;
        *out = static_cast<ProviderOptions>(ProviderOptions_ServerSideProvider | ProviderOptions_UseComThreading);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPatternProvider(PATTERNID pattern, IUnknown **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (!Owner())
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (pattern == UIA_InvokePatternId && !key.empty())
            return QueryInterface(__uuidof(IInvokeProvider), reinterpret_cast<void **>(out));
        if (pattern == UIA_SelectionItemPatternId && !key.empty() && key.rfind(L"grid:",0)!=0)
            return QueryInterface(__uuidof(ISelectionItemProvider), reinterpret_cast<void **>(out));
        if (pattern == UIA_ScrollItemPatternId && !key.empty() && key.rfind(L"grid:",0)!=0)
            return QueryInterface(__uuidof(IScrollItemProvider), reinterpret_cast<void **>(out));
        if (pattern == UIA_SelectionPatternId && key.empty())
            return QueryInterface(__uuidof(ISelectionProvider), reinterpret_cast<void **>(out));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyValue(PROPERTYID property, VARIANT *out) override
    {
        if (!out)
            return E_POINTER;
        VariantInit(out);
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        auto tile = key.empty() ? nullptr : window->Find(key);
        if (!key.empty() && !tile)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (property == UIA_NamePropertyId)
        {
            out->vt = VT_BSTR;
            out->bstrVal = SysAllocString(key.empty() ? L"Magic Composition 隔离桌面" : tile->name.c_str());
        }
        else if (property == UIA_ControlTypePropertyId)
        {
            out->vt = VT_I4;
            out->lVal = key.empty() ? UIA_ListControlTypeId : key.rfind(L"grid:",0)==0?UIA_ButtonControlTypeId:UIA_ListItemControlTypeId;
        }
        else if (property == UIA_AutomationIdPropertyId)
        {
            out->vt = VT_BSTR;
            out->bstrVal = SysAllocString(key.empty() ? L"composition-fixture" : key.c_str());
        }
        else if (property == UIA_IsEnabledPropertyId || property == UIA_IsKeyboardFocusablePropertyId ||
                 property == UIA_HasKeyboardFocusPropertyId || property == UIA_IsOffscreenPropertyId)
        {
            out->vt = VT_BOOL;
            bool result = property == UIA_HasKeyboardFocusPropertyId
                              ? ::GetFocus() == window->hwnd && (key.empty() || window->focusKey == key)
                          : property == UIA_IsOffscreenPropertyId ? !key.empty() && !window->IsOnscreen(key)
                                                                  : key.rfind(L"grid:",0)==0?window->gridEnabled.contains(key):true;
            out->boolVal = result ? VARIANT_TRUE : VARIANT_FALSE;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_HostRawElementProvider(IRawElementProviderSimple **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        return key.empty() ? UiaHostProviderFromHwnd(window->hwnd, out) : S_OK;
    }
    HRESULT STDMETHODCALLTYPE Navigate(NavigateDirection direction, IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        std::wstring targetKey;
        bool found = false;
        auto const &tiles = window->Tiles();
        if (key.empty() && (direction == NavigateDirection_FirstChild || direction == NavigateDirection_LastChild) &&
            !tiles.empty())
        {
            targetKey = (direction == NavigateDirection_FirstChild ? tiles.front() : tiles.back()).key;
            found = true;
        }
        else if (!key.empty() && direction == NavigateDirection_Parent)
            found = true;
        else if (!key.empty() &&
                 (direction == NavigateDirection_NextSibling || direction == NavigateDirection_PreviousSibling))
        {
            auto i = std::find_if(tiles.begin(), tiles.end(), [&](auto const &tile) { return tile.key == key; });
            if (i != tiles.end())
            {
                auto index = std::distance(tiles.begin(), i) + (direction == NavigateDirection_NextSibling ? 1 : -1);
                if (index >= 0 && index < static_cast<ptrdiff_t>(tiles.size()))
                {
                    targetKey = tiles[static_cast<size_t>(index)].key;
                    found = true;
                }
            }
        }
        if (found)
        {
            auto p = MakeProvider(state, targetKey);
            HRESULT hr = p->QueryInterface(__uuidof(IRawElementProviderFragment), reinterpret_cast<void **>(out));
            p->Release();
            return hr;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRuntimeId(SAFEARRAY **out) override
    {
        if (!out)
            return E_POINTER;
        if (!Owner())
            return UIA_E_ELEMENTNOTAVAILABLE;
        int id = 0;
        auto entry = state->ids.find(key);
        if (entry == state->ids.end())
            id = state->ids.emplace(key, ++state->nextId).first->second;
        else
            id = entry->second;
        int values[] = {UiaAppendRuntimeId, static_cast<int>(reinterpret_cast<INT_PTR>(state->hwnd) & 0x7fffffff), id};
        *out = SafeArrayCreateVector(VT_I4, 0, 3);
        if (!*out)
            return E_OUTOFMEMORY;
        for (LONG i = 0; i < 3; i++)
            SafeArrayPutElement(*out, &i, &values[i]);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_BoundingRectangle(UiaRect *out) override
    {
        if (!out)
            return E_POINTER;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        *out = {};
        if (!key.empty() && !window->IsOnscreen(key))
            return S_OK;
        if (key.empty())
        {
            RECT rect;
            GetWindowRect(window->hwnd, &rect);
            *out = {static_cast<double>(rect.left), static_cast<double>(rect.top),
                    static_cast<double>(rect.right - rect.left), static_cast<double>(rect.bottom - rect.top)};
        }
        else
        {
            auto rect = window->Bounds(key);
            POINT origin{0, 0};
            ClientToScreen(window->hwnd, &origin);
            double scale = window->dpi / 96;
            *out = {origin.x + rect.left * scale, origin.y + rect.top * scale, (rect.right - rect.left) * scale,
                    (rect.bottom - rect.top) * scale};
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetEmbeddedFragmentRoots(SAFEARRAY **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        return Owner() ? S_OK : UIA_E_ELEMENTNOTAVAILABLE;
    }
    HRESULT STDMETHODCALLTYPE SetFocus() override
    {
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        ::SetFocus(window->hwnd);
        if (!key.empty())
            window->Focus(key);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_FragmentRoot(IRawElementProviderFragmentRoot **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (!Owner())
            return UIA_E_ELEMENTNOTAVAILABLE;
        auto p = MakeProvider(state);
        HRESULT hr = p->QueryInterface(__uuidof(IRawElementProviderFragmentRoot), reinterpret_cast<void **>(out));
        p->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE ElementProviderFromPoint(double x, double y, IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        POINT point{static_cast<LONG>(x), static_cast<LONG>(y)};
        ScreenToClient(window->hwnd, &point);
        auto item = window->Hit(point.x * 96 / window->dpi, point.y * 96 / window->dpi);
        auto p = MakeProvider(state, item);
        HRESULT hr = p->QueryInterface(__uuidof(IRawElementProviderFragment), reinterpret_cast<void **>(out));
        p->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE GetFocus(IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        auto p = MakeProvider(state, window->focusKey);
        HRESULT hr = p->QueryInterface(__uuidof(IRawElementProviderFragment), reinterpret_cast<void **>(out));
        p->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE Invoke() override
    {
        auto window = Owner();
        if (!window || !window->Find(key))
            return UIA_E_ELEMENTNOTAVAILABLE;
        if(key.rfind(L"grid:",0)==0&&!window->gridEnabled.contains(key))return UIA_E_ELEMENTNOTENABLED;
        window->Activate(key);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Select() override
    {
        auto window = Owner();
        if (!window || !window->Find(key))
            return UIA_E_ELEMENTNOTAVAILABLE;
        window->Select(key);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE AddToSelection() override
    {
        auto window = Owner();
        if (!window || !window->Find(key))
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (!window->selected.count(key))
            window->Select(key, true);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE RemoveFromSelection() override
    {
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (window->selected.count(key))
            window->Select(key, true);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_IsSelected(BOOL *out) override
    {
        if (!out)
            return E_POINTER;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        *out = window->selected.count(key) ? TRUE : FALSE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_SelectionContainer(IRawElementProviderSimple **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (!Owner())
            return UIA_E_ELEMENTNOTAVAILABLE;
        *out = MakeProvider(state);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSelection(SAFEARRAY **out) override
    {
        if (!out)
            return E_POINTER;
        auto window = Owner();
        if (!window)
            return UIA_E_ELEMENTNOTAVAILABLE;
        *out = SafeArrayCreateVector(VT_UNKNOWN, 0, static_cast<ULONG>(window->selected.size()));
        if (!*out)
            return E_OUTOFMEMORY;
        LONG index = 0;
        for (auto const &item : window->selected)
        {
            auto provider = MakeProvider(state, item);
            SafeArrayPutElement(*out, &index, provider);
            provider->Release();
            index++;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_CanSelectMultiple(BOOL *out) override
    {
        if (!out)
            return E_POINTER;
        *out = TRUE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_IsSelectionRequired(BOOL *out) override
    {
        if (!out)
            return E_POINTER;
        *out = FALSE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ScrollIntoView() override
    {
        auto window = Owner();
        if (!window || !window->Find(key))
            return UIA_E_ELEMENTNOTAVAILABLE;
        window->Focus(key);
        return S_OK;
    }
};
IRawElementProviderSimple *MakeProvider(std::shared_ptr<ProviderState> const &state, std::wstring const &key)
{
    return new TrialProvider(state, key);
}
void Window::NotifyFocus()
{
    if (!UiaClientsAreListening() || !hwnd)
        return;
    auto p = MakeProvider(providerState, focusKey);
    UiaRaiseAutomationEvent(p, UIA_AutomationFocusChangedEventId);
    p->Release();
}
void Window::NotifySelection()
{
    if (!UiaClientsAreListening() || !hwnd)
        return;
    auto p = MakeProvider(providerState);
    UiaRaiseAutomationEvent(p, UIA_Selection_InvalidatedEventId);
    p->Release();
}
