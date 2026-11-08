#include "providers.h"
#include <cmath>
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>

namespace magic::system
{
namespace
{
class AudioEvents final : public IMMNotificationClient, public IAudioEndpointVolumeCallback
{
    std::atomic<ULONG> references{1};

  public:
    EDataFlow deviceFlow = eRender;

  private:
  public:
    std::shared_ptr<ChangeState> state;
    std::atomic<bool> defaultChanged{true};
    std::atomic<bool> active{true};
    void Notify()
    {
        if (active.load())
            state->Notify(2);
    }
    explicit AudioEvents(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **result) override
    {
        if (!result)
            return E_POINTER;
        *result = nullptr;
        if (id == __uuidof(IUnknown) || id == __uuidof(IMMNotificationClient))
            *result = static_cast<IMMNotificationClient *>(this);
        else if (id == __uuidof(IAudioEndpointVolumeCallback))
            *result = static_cast<IAudioEndpointVolumeCallback *>(this);
        else
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
        ULONG n = --references;
        if (!n)
            delete this;
        return n;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override
    {
        defaultChanged = true;
        Notify();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override
    {
        defaultChanged = true;
        Notify();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override
    {
        defaultChanged = true;
        Notify();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override
    {
        if (flow == deviceFlow && role == eMultimedia)
        {
            defaultChanged = true;
            Notify();
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, PROPERTYKEY) override
    {
        Notify();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA) override
    {
        Notify();
        return S_OK;
    }
};
std::wstring DeviceId(IMMDevice *device)
{
    LPWSTR text = nullptr;
    winrt::check_hresult(device->GetId(&text));
    std::wstring id = text;
    CoTaskMemFree(text);
    return id;
}
std::wstring DeviceName(IMMDevice *device)
{
    winrt::com_ptr<IPropertyStore> properties;
    if (FAILED(device->OpenPropertyStore(STGM_READ, properties.put())))
        return L"";
    PROPVARIANT value{};
    std::wstring result;
    if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
        result = value.pwszVal;
    PropVariantClear(&value);
    return result;
}
} // namespace
struct AudioProvider::Impl
{
    EDataFlow flow;
    std::shared_ptr<ChangeState> state;
    winrt::com_ptr<IMMDeviceEnumerator> enumerator;
    winrt::com_ptr<IMMDevice> device;
    winrt::com_ptr<IAudioEndpointVolume> volume;
    winrt::com_ptr<AudioEvents> callback;
    bool canSelectDefault = AudioPolicyAvailable();
    bool subscribed = false, deviceRegistered = false, volumeRegistered = false;
    explicit Impl(std::shared_ptr<ChangeState> s, bool capture)
        : flow(capture ? eCapture : eRender), state(std::move(s))
    {
    }
    void ReleaseVolume()
    {
        if (volumeRegistered && volume)
            volume->UnregisterControlChangeNotify(callback.get());
        volumeRegistered = false;
        volume = nullptr;
        device = nullptr;
    }
    void Stop()
    {
        if (callback)
            callback->active = false;
        ReleaseVolume();
        if (deviceRegistered && enumerator)
            enumerator->UnregisterEndpointNotificationCallback(callback.get());
        deviceRegistered = false;
        subscribed = false;
        enumerator = nullptr;
        callback = nullptr;
    }
    ~Impl()
    {
        Stop();
    }
    void Refresh()
    {
        if (!enumerator)
            winrt::check_hresult(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                                  __uuidof(IMMDeviceEnumerator), enumerator.put_void()));
        if (subscribed && !deviceRegistered)
        {
            if (!callback)
            {
                callback.attach(new AudioEvents(state));
                callback->deviceFlow = flow;
            }
            winrt::check_hresult(enumerator->RegisterEndpointNotificationCallback(callback.get()));
            deviceRegistered = true;
        }
        bool changed = callback && callback->defaultChanged.exchange(false);
        if (!volume || changed)
        {
            ReleaseVolume();
            HRESULT hr = enumerator->GetDefaultAudioEndpoint(flow, eMultimedia, device.put());
            if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
                return;
            winrt::check_hresult(hr);
            winrt::check_hresult(
                device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_INPROC_SERVER, nullptr, volume.put_void()));
            if (subscribed)
            {
                winrt::check_hresult(volume->RegisterControlChangeNotify(callback.get()));
                volumeRegistered = true;
            }
        }
    }
    JsonObject Health() const
    {
        JsonObject o;
        Flag(o, L"subscribed", subscribed);
        Number(o, L"deviceCallbacks", deviceRegistered ? 1 : 0);
        Number(o, L"volumeCallbacks", volumeRegistered ? 1 : 0);
        return o;
    }
    JsonObject Read()
    {
        Refresh();
        JsonObject o = Health();
        Flag(o, L"available", bool(volume));
        Flag(o, L"canSelectDefault", flow == eRender && canSelectDefault);
        if (volume)
        {
            float level = 0;
            BOOL muted = FALSE;
            winrt::check_hresult(volume->GetMasterVolumeLevelScalar(&level));
            winrt::check_hresult(volume->GetMute(&muted));
            Number(o, L"volume", level);
            Flag(o, L"muted", muted != FALSE);
            Text(o, L"defaultDeviceId", DeviceId(device.get()));
            Text(o, L"defaultDeviceName", DeviceName(device.get()));
        }
        JsonArray rows;
        winrt::com_ptr<IMMDeviceCollection> devices;
        winrt::check_hresult(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, devices.put()));
        UINT count = 0;
        devices->GetCount(&count);
        for (UINT i = 0; i < std::min(count, 64u); i++)
        {
            winrt::com_ptr<IMMDevice> item;
            if (FAILED(devices->Item(i, item.put())))
                continue;
            JsonObject row;
            Text(row, L"id", DeviceId(item.get()));
            Text(row, L"name", DeviceName(item.get()));
            rows.Append(row);
        }
        o.Insert(L"devices", rows);
        return o;
    }
};
AudioProvider::AudioProvider(std::shared_ptr<ChangeState> state, bool capture)
    : impl(std::make_unique<Impl>(std::move(state), capture))
{
}
AudioProvider::~AudioProvider() = default;
JsonObject AudioProvider::Read()
{
    try
    {
        auto result = impl->Read();
        if (!impl->subscribed)
            impl->Stop();
        return result;
    }
    catch (...)
    {
        if (!impl->subscribed)
            impl->Stop();
        throw;
    }
}
JsonObject AudioProvider::Health() const
{
    return impl->Health();
}
JsonObject AudioProvider::Subscribe(bool enabled)
{
    if (!enabled)
        impl->Stop();
    else
    {
        impl->subscribed = true;
        try
        {
            impl->Refresh();
        }
        catch (...)
        {
            impl->Stop();
            throw;
        }
    }
    return impl->Health();
}
JsonObject AudioProvider::Set(JsonObject const &request)
{
    auto action = RequiredText(request, L"action", 32);
    if (action != L"set-volume" && action != L"set-mute")
        throw std::invalid_argument("Unsupported audio action");
    if (action == L"set-volume")
    {
        double level = request.GetNamedNumber(L"value");
        if (!std::isfinite(level) || level < 0 || level > 1)
            throw std::invalid_argument("Volume outside 0..1");
    }
    bool cleanup = !impl->subscribed;
    try
    {
        impl->Refresh();
        if (!impl->volume)
            throw std::invalid_argument("No active output device");
        auto expected = RequiredText(request, L"deviceId", 4096);
        if (expected != DeviceId(impl->device.get()))
            throw std::invalid_argument("Default audio device changed");
        if (action == L"set-volume")
            winrt::check_hresult(
                impl->volume->SetMasterVolumeLevelScalar(float(request.GetNamedNumber(L"value")), nullptr));
        else
            winrt::check_hresult(impl->volume->SetMute(request.GetNamedBoolean(L"value"), nullptr));
        JsonObject o;
        Flag(o, L"accepted", true);
        if (action == L"set-volume") { float actual=0; winrt::check_hresult(impl->volume->GetMasterVolumeLevelScalar(&actual)); Number(o,L"value",actual); BOOL muted=FALSE;winrt::check_hresult(impl->volume->GetMute(&muted));Flag(o,L"muted",muted!=FALSE); }
        if (cleanup)
            impl->Stop();
        return o;
    }
    catch (...)
    {
        if (cleanup)
            impl->Stop();
        throw;
    }
}
} // namespace magic::system
