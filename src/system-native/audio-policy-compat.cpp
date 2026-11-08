#include "providers.h"
#include <mmdeviceapi.h>
// Windows compatibility ABI, independently declared. This is not a public
// Microsoft contract. See docs/development/native-system-windows-compat.md.
namespace magic::system
{
namespace
{
struct __declspec(uuid("F8679F50-850A-41CF-9C72-430F290290C8")) Policy : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Reserved0() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved1() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved2() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved3() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved4() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved5() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved6() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved7() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved8() = 0;
    virtual HRESULT STDMETHODCALLTYPE Reserved9() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(LPCWSTR, ERole) = 0;
};
constexpr GUID PolicyClass{0x870af99c, 0x171d, 0x4f9e, {0xaf, 0x0d, 0xe6, 0x3d, 0xf4, 0x0c, 0x2b, 0xc9}};
winrt::com_ptr<Policy> Open()
{
    winrt::com_ptr<Policy> result;
    winrt::check_hresult(
        CoCreateInstance(PolicyClass, nullptr, CLSCTX_INPROC_SERVER, __uuidof(Policy), result.put_void()));
    return result;
}
} // namespace
bool AudioPolicyAvailable()
{
    try
    {
        return bool(Open());
    }
    catch (...)
    {
        return false;
    }
}
JsonObject SetAudioDefault(JsonObject const &args)
{
    auto id = RequiredText(args, L"deviceId", 4096);
    winrt::com_ptr<IMMDeviceEnumerator> enumerator;
    winrt::check_hresult(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                          __uuidof(IMMDeviceEnumerator), enumerator.put_void()));
    winrt::com_ptr<IMMDevice> device;
    winrt::check_hresult(enumerator->GetDevice(id.c_str(), device.put()));
    DWORD state = 0;
    winrt::check_hresult(device->GetState(&state));
    EDataFlow flow = eAll;
    winrt::check_hresult(device.as<IMMEndpoint>()->GetDataFlow(&flow));
    if (state != DEVICE_STATE_ACTIVE || flow != eRender)
        throw std::invalid_argument("Output endpoint is inactive or no longer offered");
    std::wstring prior[3];
    for (int n = 0; n < 3; n++)
    {
        winrt::com_ptr<IMMDevice> old;
        if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, ERole(n), old.put())))
        {
            LPWSTR name = nullptr;
            if (SUCCEEDED(old->GetId(&name)))
            {
                prior[n] = name;
                CoTaskMemFree(name);
            }
        }
    }
    auto policy = Open();
    int changed = 0;
    try
    {
        for (int n = 0; n < 3; n++)
        {
            winrt::check_hresult(policy->SetDefaultEndpoint(id.c_str(), ERole(n)));
            ++changed;
        }
    }
    catch (...)
    {
        for (int n = 0; n < changed; n++)
            if (!prior[n].empty())
            {
                winrt::com_ptr<IMMDevice> current;
                if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eRender, ERole(n), current.put())))
                {
                    LPWSTR name = nullptr;
                    if (SUCCEEDED(current->GetId(&name)))
                    {
                        bool owns = id == name;
                        CoTaskMemFree(name);
                        if (owns)
                            policy->SetDefaultEndpoint(prior[n].c_str(), ERole(n));
                    }
                }
            }
        throw;
    }
    JsonObject result;
    Flag(result, L"accepted", true);
    Number(result, L"roles", changed);
    return result;
}
} // namespace magic::system
