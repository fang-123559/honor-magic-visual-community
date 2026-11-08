#include "providers.h"
// Windows Shell compatibility ABI. Not a stable Microsoft SDK contract.
// Declaration cross-check: Chromium status_tray_state_changer_win.cc and
// Geoff Chappell's first-hand Shell interface analysis. No Seelen code/library.
namespace magic::system
{
namespace
{
struct ShellNotifyItem
{
    PWSTR executable;
    PWSTR tooltip;
    HICON icon;
    HWND owner;
    DWORD preference;
    UINT id;
    GUID guid;
};
struct __declspec(uuid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB")) NotifyCallback : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Notify(ULONG, ShellNotifyItem *) = 0;
};
struct __declspec(uuid("D133CE13-3537-48BA-93A7-AFCD5D2053B4")) TrayNotify : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE RegisterCallback(NotifyCallback *, ULONG *) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnregisterCallback(ULONG *) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPreference(ShellNotifyItem const *) = 0;
    virtual HRESULT STDMETHODCALLTYPE EnableAutoTray(BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE DoAction(BOOL) = 0;
};
constexpr CLSID TrayClass{0x25dead04, 0x1eac, 0x4911, {0x9e, 0x3a, 0xad, 0x0a, 0x4a, 0xb5, 0x60, 0xfd}};
std::wstring SafeText(PCWSTR address, size_t limit)
{
    std::wstring text;
    text.reserve(std::min<size_t>(limit, 256));
    for (size_t i = 0; address && i < limit; i++)
    {
        wchar_t value = 0;
        SIZE_T read = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), address + i, &value, sizeof(value), &read) ||
            read != sizeof(value) || !value)
            break;
        text += value;
    }
    return text;
}
struct Callback final : NotifyCallback
{
    std::atomic<ULONG> refs{1};
    std::mutex gate;
    JsonArray rows;
    unsigned invalid = 0, total = 0, invalidOwner = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **result) override
    {
        if (!result)
            return E_POINTER;
        *result = nullptr;
        if (id == IID_IUnknown || id == __uuidof(NotifyCallback))
        {
            *result = static_cast<NotifyCallback *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto count = --refs;
        if (!count)
            delete this;
        return count;
    }
    HRESULT STDMETHODCALLTYPE Notify(ULONG event, ShellNotifyItem *source) override
    {
        try
        {
            {
                std::scoped_lock lock(gate);
                ++total;
            }
            ShellNotifyItem item{};
            SIZE_T read = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), source, &item, sizeof(item), &read) || read != sizeof(item))
            {
                std::scoped_lock lock(gate);
                ++invalid;
                return S_OK;
            }
            DWORD pid = 0;
            if (!IsWindow(item.owner) || !GetWindowThreadProcessId(item.owner, &pid))
            {
                std::scoped_lock lock(gate);
                ++invalidOwner;
                return S_OK;
            }
            JsonObject row;
            Text(row, L"executable", SafeText(item.executable, 1024));
            Text(row, L"tooltip", SafeText(item.tooltip, 512));
            Text(row, L"ownerHwnd", std::to_wstring(reinterpret_cast<uintptr_t>(item.owner)));
            Number(row, L"pid", pid);
            Number(row, L"iconId", item.id);
            Number(row, L"event", event);
            Number(row, L"preference", item.preference);
            HICON clone = item.icon ? CopyIcon(item.icon) : nullptr;
            Flag(row, L"iconCopySucceeded", clone != nullptr);
            if (clone)
                DestroyIcon(clone);
            wchar_t guid[40]{};
            StringFromGUID2(item.guid, guid, 40);
            Text(row, L"guid", guid);
            std::scoped_lock lock(gate);
            if (rows.Size() < 256)
                rows.Append(row);
            return S_OK;
        }
        catch (...)
        {
            return E_FAIL;
        }
    }
};
} // namespace
JsonObject ReadShellTrayOnSta()
{
    JsonObject result;
    winrt::com_ptr<TrayNotify> tray;
    auto hr = CoCreateInstance(TrayClass, nullptr, CLSCTX_LOCAL_SERVER, __uuidof(TrayNotify), tray.put_void());
    Number(result, L"activationResult", hr);
    if (FAILED(hr))
    {
        Flag(result, L"available", false);
        return result;
    }
    winrt::com_ptr<Callback> callback;
    callback.attach(new Callback);
    ULONG cookie = 0;
    hr = tray->RegisterCallback(callback.get(), &cookie);
    Number(result, L"registerResult", hr);
    if (SUCCEEDED(hr))
    {
        Handle timer{CreateWaitableTimerW(nullptr, TRUE, nullptr)};
        LARGE_INTEGER due{};
        due.QuadPart = -1500000;
        Check(SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE) != FALSE);
        while (MsgWaitForMultipleObjects(1, &timer.value, FALSE, INFINITE, QS_ALLINPUT) == WAIT_OBJECT_0 + 1)
        {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        auto ended = tray->UnregisterCallback(&cookie);
        Number(result, L"unregisterResult", ended);
    }
    {
        std::scoped_lock lock(callback->gate);
        result.Insert(L"icons", callback->rows);
        Number(result, L"invalidCallbacks", callback->invalid);
        Number(result, L"totalCallbacks", callback->total);
        Number(result, L"invalidOwner", callback->invalidOwner);
    }
    Flag(result, L"available", SUCCEEDED(hr));
    Flag(result, L"preferencesModified", false);
    return result;
}
JsonObject InspectShellTrayCompatibility()
{
    std::wstring serialized;
    std::exception_ptr error;
    std::thread thread([&] {
        try
        {
            winrt::init_apartment(winrt::apartment_type::single_threaded);
            serialized = ReadShellTrayOnSta().Stringify();
            winrt::uninit_apartment();
        }
        catch (...)
        {
            error = std::current_exception();
        }
    });
    thread.join();
    if (error)
        std::rethrow_exception(error);
    return JsonObject::Parse(serialized);
}
} // namespace magic::system
