#pragma once
#include "native-popup.h"
// Detached lifetime tokens make providers unavailable after a popup closes.
// Keys identify actual actions/layouts/dates, rather than recycled row indices.
class NativePopupProvider final : public IRawElementProviderSimple,
                                  public IRawElementProviderFragment,
                                  public IRawElementProviderFragmentRoot,
                                  public IInvokeProvider,
                                  public IScrollItemProvider
{
    std::atomic<ULONG> refs = 1;
    std::shared_ptr<PopupLife> life;
    std::wstring key;
    NativePopup *Owner()
    {
        return life && life->owner && life->owner->visible && !life->owner->dismissing ? life->owner : nullptr;
    }
    // Rendering rows also contain drag ghosts/gaps. They are not additional
    // controls: exposing their copied keys creates a cyclic sibling chain.
    static bool IsControl(PopupRow const &row)
    {
        return !row.key.empty() && row.control != L"drag-ghost" && row.control != L"drag-gap";
    }
    static bool Exposed(NativePopup const &owner, int index)
    {
        auto const &row = owner.rows[index];
        if (!IsControl(row)) return false;
        // Defend all popup types against duplicate keys supplied by a model.
        // A child must have exactly one position in the fragment's sibling chain.
        for (int i = 0; i < index; ++i)
            if (IsControl(owner.rows[i]) && owner.rows[i].key == row.key) return false;
        return true;
    }
    int Index()
    {
        auto owner = Owner();
        if (!owner || key.empty()) return -1;
        for (int i = 0; i < static_cast<int>(owner->rows.size()); ++i)
            if (IsControl(owner->rows[i]) && owner->rows[i].key == key) return i;
        return -1;
    }
    HRESULT Neighbour(NativePopup const &owner, int start, int step, IRawElementProviderFragment **out)
    {
        for (int i = start; i >= 0 && i < static_cast<int>(owner.rows.size()); i += step)
            if (Exposed(owner, i)) return Node(owner.rows[i].key, out);
        return S_OK;
    }
    HRESULT RowNode(NativePopup const &owner, int index, IRawElementProviderFragment **out)
    {
        // Hit/focus may identify a visual-only ghost: return its real control
        // when present, otherwise the root. Never manufacture a dangling child.
        if (index >= 0 && index < static_cast<int>(owner.rows.size())) {
            auto const &id = owner.rows[index].key;
            for (auto const &row : owner.rows)
                if (IsControl(row) && row.key == id) return Node(id, out);
        }
        return Node(L"", out);
    }
    HRESULT Node(std::wstring const &value, IRawElementProviderFragment **out)
    {
        auto node = new NativePopupProvider(life, value);
        *out = static_cast<IRawElementProviderFragment *>(node);
        return S_OK;
    }

  public:
    NativePopupProvider(std::shared_ptr<PopupLife> state, std::wstring value)
        : life(std::move(state)), key(std::move(value))
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
        else if (iid == __uuidof(IScrollItemProvider) && !key.empty())
            *out = static_cast<IScrollItemProvider *>(this);
        if (!*out)
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto value = --refs;
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
        if (!Owner() || (!key.empty() && Index() < 0))
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (pattern == UIA_ScrollItemPatternId && !key.empty())
            return QueryInterface(__uuidof(IScrollItemProvider), reinterpret_cast<void **>(out));
        if (pattern == UIA_InvokePatternId && !key.empty())
            return QueryInterface(__uuidof(IInvokeProvider), reinterpret_cast<void **>(out));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyValue(PROPERTYID property, VARIANT *out) override
    {
        if (!out)
            return E_POINTER;
        VariantInit(out);
        auto owner = Owner();
        int i = Index();
        if (!owner || (!key.empty() && i < 0))
            return UIA_E_ELEMENTNOTAVAILABLE;
        auto row = i >= 0 ? &owner->rows[i] : nullptr;
        if (property == UIA_NamePropertyId)
        {
            out->vt = VT_BSTR;
            auto name = row ? row->label + (row->detail.empty() ? L"" : L" " + row->detail) : owner->caption;
            if (row && row->active)
                name += owner->Ui(L"（已选）", L" (selected)");
            out->bstrVal = SysAllocString(name.c_str());
        }
        else if (property == UIA_AutomationIdPropertyId)
        {
            out->vt = VT_BSTR;
            out->bstrVal = SysAllocString(key.empty() ? L"magic-native-popup" : key.c_str());
        }
        else if (property == UIA_ControlTypePropertyId)
        {
            out->vt = VT_I4;
            out->lVal = owner->LocalContext()?(row?(row->control==L"separator"?UIA_SeparatorControlTypeId:UIA_MenuItemControlTypeId):UIA_MenuControlTypeId):row ? UIA_ButtonControlTypeId : UIA_PaneControlTypeId;
        }
        else if (property == UIA_IsKeyboardFocusablePropertyId || property == UIA_IsControlElementPropertyId ||
                 property == UIA_IsContentElementPropertyId || property == UIA_IsEnabledPropertyId ||
                 property == UIA_HasKeyboardFocusPropertyId || property == UIA_IsOffscreenPropertyId)
        {
            out->vt = VT_BOOL;
            bool value = property == UIA_IsOffscreenPropertyId ? row && row->offscreen
                         : property == UIA_IsEnabledPropertyId ? !row || row->enabled
                         : property == UIA_HasKeyboardFocusPropertyId
                             ? ::GetFocus() == owner->hwnd && (row ? owner->focus == i : owner->focus < 0)
                             : true;
            out->boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_HostRawElementProvider(IRawElementProviderSimple **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto owner = Owner();
        if (!owner)
            return UIA_E_ELEMENTNOTAVAILABLE;
        return key.empty() ? UiaHostProviderFromHwnd(owner->hwnd, out) : S_OK;
    }
    HRESULT STDMETHODCALLTYPE Navigate(NavigateDirection direction, IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto owner = Owner();
        if (!owner)
            return UIA_E_ELEMENTNOTAVAILABLE;
        int i = Index();
        if (key.empty())
        {
            if (direction == NavigateDirection_FirstChild)
                return Neighbour(*owner, 0, 1, out);
            if (direction == NavigateDirection_LastChild)
                return Neighbour(*owner, static_cast<int>(owner->rows.size()) - 1, -1, out);
        }
        else
        {
            if (i < 0) return UIA_E_ELEMENTNOTAVAILABLE;
            if (direction == NavigateDirection_Parent) return Node(L"", out);
            if (direction == NavigateDirection_NextSibling) return Neighbour(*owner, i + 1, 1, out);
            if (direction == NavigateDirection_PreviousSibling) return Neighbour(*owner, i - 1, -1, out);
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRuntimeId(SAFEARRAY **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (!Owner() || (!key.empty() && Index() < 0))
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (key.empty())
            return S_OK;
        unsigned long long hash = 1469598103934665603ULL;
        for (wchar_t c : key)
        {
            hash ^= c;
            hash *= 1099511628211ULL;
        }
        int values[] = {UiaAppendRuntimeId, static_cast<int>(hash), static_cast<int>(hash >> 32)};
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
        *out = {};
        auto owner = Owner();
        int i = Index();
        if (!owner || (!key.empty() && i < 0))
            return UIA_E_ELEMENTNOTAVAILABLE;
        RECT rect{};
        GetWindowRect(owner->hwnd, &rect);
        if (key.empty())
        {
            *out = {static_cast<double>(rect.left), static_cast<double>(rect.top),
                    static_cast<double>(rect.right - rect.left), static_cast<double>(rect.bottom - rect.top)};
            return S_OK;
        }
        auto r = owner->VisualRowBounds(i);
        POINT p{};
        ClientToScreen(owner->hwnd, &p);
        float scale = owner->dpi / 96;
        *out = {p.x + (owner->contentX+r.left) * scale, p.y + (owner->contentY+r.top) * scale, (r.right - r.left) * scale, (r.bottom - r.top) * scale};
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
        auto owner = Owner();
        if(owner)owner->FinishResize();
        int i = Index();
        if (!owner || (!key.empty() && i < 0))
            return UIA_E_ELEMENTNOTAVAILABLE;
        SetForegroundWindow(owner->hwnd);
        ::SetFocus(owner->hwnd);
        owner->focus = i;
        owner->Draw();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE get_FragmentRoot(IRawElementProviderFragmentRoot **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (!Owner())
            return UIA_E_ELEMENTNOTAVAILABLE;
        auto root = new NativePopupProvider(life, L"");
        *out = static_cast<IRawElementProviderFragmentRoot *>(root);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ElementProviderFromPoint(double x, double y, IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto owner = Owner();
        if (!owner)
            return UIA_E_ELEMENTNOTAVAILABLE;
        POINT p{static_cast<LONG>(x), static_cast<LONG>(y)};
        ScreenToClient(owner->hwnd, &p);
        int i = owner->Hit(p.x * 96 / owner->dpi, p.y * 96 / owner->dpi);
        return RowNode(*owner, i, out);
    }
    HRESULT STDMETHODCALLTYPE GetFocus(IRawElementProviderFragment **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        auto owner = Owner();
        if (!owner)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (::GetFocus() != owner->hwnd)
            return S_OK;
        return RowNode(*owner, owner->focus, out);
    }
    HRESULT STDMETHODCALLTYPE ScrollIntoView() override
    {
        auto owner = Owner();
        if(owner)owner->FinishResize();
        int i = Index();
        if (!owner || i < 0)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if(owner->FunctionalPanel()&&!owner->functionalPinned.contains(owner->rows[i].id)){
            auto r=owner->rows[i].rect;if(r.top<owner->functionalTop)owner->scroll=std::max(0,owner->scroll-static_cast<int>(std::ceil((owner->functionalTop-r.top)/56)));
            else if(r.bottom>owner->functionalBottom)owner->scroll+=static_cast<int>(std::ceil((r.bottom-owner->functionalBottom)/56));owner->BuildRows();owner->Draw();return S_OK;
        }
        if (owner->rows[i].offscreen && owner->kind == L"@magic/keyboard-selector")
        {
            owner->scroll = i;
            owner->BuildRows();
            owner->Draw();
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke() override
    {
        auto owner = Owner();
        if(owner)owner->FinishResize();
        int i = Index();
        if (!owner || i < 0)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (!owner->rows[i].enabled)
            return UIA_E_ELEMENTNOTENABLED;
        try
        {
            ScrollIntoView();
            owner->Activate(i);
            return S_OK;
        }
        catch (...)
        {
            return E_FAIL;
        }
    }
};
inline IRawElementProviderSimple *PopupProvider(std::shared_ptr<PopupLife> const &life, std::wstring const &key)
{
    return static_cast<IRawElementProviderSimple *>(new NativePopupProvider(life, key));
}
